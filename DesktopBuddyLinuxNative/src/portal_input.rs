
use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use ashpd::desktop::{
    remote_desktop::{Axis, DeviceType, KeyState, RemoteDesktop},
    screencast::{CursorMode, Screencast, SourceType},
    PersistMode,
};

enum Msg {

    Motion { u: f64, v: f64 },
    Button { button: i32, pressed: bool },
    AxisDiscrete { steps: i32 },
    Key { keysym: i32, pressed: bool },
    Stop,
}

struct InputSession {
    tx: async_channel::Sender<Msg>,
    thread: Option<JoinHandle<()>>,
}

impl InputSession {
    fn stop(&mut self) {
        let _ = self.tx.try_send(Msg::Stop);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

static SESSIONS: OnceLock<Mutex<HashMap<u64, InputSession>>> = OnceLock::new();
static NEXT_SESSION_ID: AtomicU64 = AtomicU64::new(1);

fn sessions() -> &'static Mutex<HashMap<u64, InputSession>> {
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

struct Ready {
    width: f64,
    height: f64,
}

async fn setup<'a>(
    remote: &RemoteDesktop<'a>,
    screencast: &Screencast<'a>,
    session: &ashpd::desktop::Session<'a, RemoteDesktop<'a>>,
    token: Option<&str>,
) -> Result<(Ready, u32), String> {
    remote
        .select_devices(
            session,
            DeviceType::Keyboard | DeviceType::Pointer,
            token,
            PersistMode::ExplicitlyRevoked,
        )
        .await
        .map_err(|e| format!("select_devices: {e}"))?;

    screencast
        .select_sources(
            session,
            CursorMode::Embedded,
            SourceType::Monitor | SourceType::Window | SourceType::Virtual,
            false,
            token,
            PersistMode::ExplicitlyRevoked,
        )
        .await
        .map_err(|e| format!("select_sources: {e}"))?;

    let response = remote
        .start(session, None)
        .await
        .map_err(|e| format!("start: {e}"))?
        .response()
        .map_err(|e| format!("start response: {e}"))?;

    let streams = response.streams().ok_or_else(|| "no streams".to_string())?;
    let stream = streams.first().ok_or_else(|| "empty streams".to_string())?;
    let node_id = stream.pipe_wire_node_id();
    let (w, h) = stream.size().unwrap_or((0, 0));
    Ok((
        Ready {
            width: w.max(1) as f64,
            height: h.max(1) as f64,
        },
        node_id,
    ))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_start(
    token_ptr: *const u8,
    token_len: usize,
    out_session_id: *mut u64,
) -> i32 {
    if out_session_id.is_null() {
        return -1;
    }
    unsafe { *out_session_id = 0 };

    let token: Option<String> = if token_ptr.is_null() || token_len == 0 {
        None
    } else {
        let slice = unsafe { std::slice::from_raw_parts(token_ptr, token_len) };
        std::str::from_utf8(slice).ok().map(|s| s.to_owned())
    };

    let (tx, rx) = async_channel::unbounded::<Msg>();
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<(), String>>();

    let thread = thread::spawn(move || {
        async_std::task::block_on(async move {
            let remote = match RemoteDesktop::new().await {
                Ok(r) => r,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("RemoteDesktop::new: {e}")));
                    return;
                }
            };
            let screencast = match Screencast::new().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("Screencast::new: {e}")));
                    return;
                }
            };
            let session = match remote.create_session().await {
                Ok(s) => s,
                Err(e) => {
                    let _ = ready_tx.send(Err(format!("create_session: {e}")));
                    return;
                }
            };

            let (ready, _node_id) = match setup(&remote, &screencast, &session, token.as_deref()).await
            {
                Ok(v) => v,
                Err(e) => {
                    let _ = ready_tx.send(Err(e));
                    return;
                }
            };

            let stream_node = _node_id;
            let _ = ready_tx.send(Ok(()));

            while let Ok(msg) = rx.recv().await {
                match msg {
                    Msg::Motion { u, v } => {
                        let x = (u.clamp(0.0, 1.0)) * ready.width;
                        let y = (v.clamp(0.0, 1.0)) * ready.height;
                        let _ = remote
                            .notify_pointer_motion_absolute(&session, stream_node, x, y)
                            .await;
                    }
                    Msg::Button { button, pressed } => {
                        let state = if pressed {
                            KeyState::Pressed
                        } else {
                            KeyState::Released
                        };
                        let _ = remote.notify_pointer_button(&session, button, state).await;
                    }
                    Msg::AxisDiscrete { steps } => {
                        let _ = remote
                            .notify_pointer_axis_discrete(&session, Axis::Vertical, steps)
                            .await;
                    }
                    Msg::Key { keysym, pressed } => {
                        let state = if pressed {
                            KeyState::Pressed
                        } else {
                            KeyState::Released
                        };
                        let _ = remote.notify_keyboard_keysym(&session, keysym, state).await;
                    }
                    Msg::Stop => break,
                }
            }

        });
    });

    match ready_rx.recv() {
        Ok(Ok(())) => {}
        Ok(Err(e)) => {
            log::warn!("[DesktopBuddy input] session setup failed: {e}");
            let _ = thread.join();
            return -2;
        }
        Err(_) => {
            return -3;
        }
    }

    let id = NEXT_SESSION_ID.fetch_add(1, Ordering::Relaxed);
    let mut map = match sessions().lock() {
        Ok(m) => m,
        Err(_) => return -4,
    };
    map.insert(
        id,
        InputSession {
            tx,
            thread: Some(thread),
        },
    );
    unsafe { *out_session_id = id };
    0
}

fn send(session_id: u64, msg: Msg) {
    if let Ok(map) = sessions().lock() {
        if let Some(s) = map.get(&session_id) {
            let _ = s.tx.try_send(msg);
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_motion(session_id: u64, u: f64, v: f64) {
    send(session_id, Msg::Motion { u, v });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_button(session_id: u64, button: i32, pressed: i32) {
    send(
        session_id,
        Msg::Button {
            button,
            pressed: pressed != 0,
        },
    );
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_scroll(session_id: u64, steps: i32) {
    send(session_id, Msg::AxisDiscrete { steps });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_key(session_id: u64, keysym: i32, pressed: i32) {
    send(
        session_id,
        Msg::Key {
            keysym,
            pressed: pressed != 0,
        },
    );
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_stop(session_id: u64) {
    if let Ok(mut map) = sessions().lock() {
        if let Some(mut s) = map.remove(&session_id) {
            s.stop();
        }
    }
}
