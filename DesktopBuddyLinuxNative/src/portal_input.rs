
use std::collections::HashMap;
use std::ffi::{c_char, CString};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use ashpd::desktop::{
    remote_desktop::{DeviceType, KeyState, RemoteDesktop},
    screencast::{CursorMode, Screencast, SourceType},
    PersistMode,
};

const SCROLL_UNIT: f64 = 15.0;

enum Msg {

    Motion { u: f64, v: f64 },
    TouchDown { slot: u32, u: f64, v: f64 },
    TouchMotion { slot: u32, u: f64, v: f64 },
    TouchUp { slot: u32 },
    Scroll { steps: i32 },
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

static LAST_ERROR: OnceLock<Mutex<Option<CString>>> = OnceLock::new();

fn set_last_error(msg: &str) {
    let cell = LAST_ERROR.get_or_init(|| Mutex::new(None));
    if let Ok(mut guard) = cell.lock() {
        *guard = CString::new(msg).ok();
    }
    log::warn!("[DesktopBuddy input] {msg}");
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_last_error() -> *const c_char {
    let Some(cell) = LAST_ERROR.get() else {
        return std::ptr::null();
    };
    match cell.lock() {
        Ok(guard) => match guard.as_ref() {
            Some(s) => s.as_ptr(),
            None => std::ptr::null(),
        },
        Err(_) => std::ptr::null(),
    }
}

struct Ready {
    width: f64,
    height: f64,
}

struct SessionInfo {
    node_id: u32,
    width: f64,
    height: f64,
    is_monitor: bool,
    token: Option<String>,
}

async fn setup<'a>(
    remote: &RemoteDesktop<'a>,
    screencast: &Screencast<'a>,
    session: &ashpd::desktop::Session<'a, RemoteDesktop<'a>>,
    token: Option<&str>,
) -> Result<(Ready, u32, bool, Option<String>), String> {
    remote
        .select_devices(
            session,
            DeviceType::Keyboard | DeviceType::Pointer | DeviceType::Touchscreen,
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
            None,
            PersistMode::DoNot,
        )
        .await
        .map_err(|e| format!("select_sources: {e}"))?;

    let response = remote
        .start(session, None)
        .await
        .map_err(|e| format!("start: {e}"))?
        .response()
        .map_err(|e| format!("start response: {e}"))?;

    let restore_token = response.restore_token().map(|s| s.to_owned());
    let streams = response.streams().ok_or_else(|| "no streams".to_string())?;
    let stream = streams.first().ok_or_else(|| "empty streams".to_string())?;
    let node_id = stream.pipe_wire_node_id();
    let (w, h) = stream.size().unwrap_or((0, 0));
    let is_monitor = matches!(stream.source_type(), Some(SourceType::Monitor));
    Ok((
        Ready {
            width: w.max(1) as f64,
            height: h.max(1) as f64,
        },
        node_id,
        is_monitor,
        restore_token,
    ))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_start(
    token_ptr: *const u8,
    token_len: usize,
    out_session_id: *mut u64,
    out_selection: *mut crate::DbLinuxSelection,
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
    let (ready_tx, ready_rx) = std::sync::mpsc::channel::<Result<SessionInfo, String>>();

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

            let (ready, node_id, is_monitor, restore_token) = match setup(&remote, &screencast, &session, token.as_deref()).await
            {
                Ok(v) => v,
                Err(e) => {
                    let _ = ready_tx.send(Err(e));
                    return;
                }
            };

            let stream_node = node_id;
            let _ = ready_tx.send(Ok(SessionInfo {
                node_id,
                width: ready.width,
                height: ready.height,
                is_monitor,
                token: restore_token,
            }));

            while let Ok(msg) = rx.recv().await {
                match msg {
                    Msg::Motion { u, v } => {
                        let x = (u.clamp(0.0, 1.0)) * ready.width;
                        let y = (v.clamp(0.0, 1.0)) * ready.height;
                        if let Err(e) = remote
                            .notify_pointer_motion_absolute(&session, stream_node, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_pointer_motion_absolute: {e}"));
                        }
                    }
                    Msg::TouchDown { slot, u, v } => {
                        let x = u.clamp(0.0, 1.0) * ready.width;
                        let y = v.clamp(0.0, 1.0) * ready.height;
                        if let Err(e) = remote
                            .notify_touch_down(&session, stream_node, slot, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_touch_down: {e}"));
                        }
                    }
                    Msg::TouchMotion { slot, u, v } => {
                        let x = u.clamp(0.0, 1.0) * ready.width;
                        let y = v.clamp(0.0, 1.0) * ready.height;
                        if let Err(e) = remote
                            .notify_touch_motion(&session, stream_node, slot, x, y)
                            .await
                        {
                            set_last_error(&format!("notify_touch_motion: {e}"));
                        }
                    }
                    Msg::TouchUp { slot } => {
                        if let Err(e) = remote.notify_touch_up(&session, slot).await {
                            set_last_error(&format!("notify_touch_up: {e}"));
                        }
                    }
                    Msg::Scroll { steps } => {
                        let dy = steps as f64 * SCROLL_UNIT;
                        if let Err(e) = remote.notify_pointer_axis(&session, 0.0, dy, true).await {
                            set_last_error(&format!("notify_pointer_axis: {e}"));
                        }
                    }
                    Msg::Key { keysym, pressed } => {
                        let state = if pressed {
                            KeyState::Pressed
                        } else {
                            KeyState::Released
                        };
                        if let Err(e) = remote.notify_keyboard_keysym(&session, keysym, state).await {
                            set_last_error(&format!("notify_keyboard_keysym: {e}"));
                        }
                    }
                    Msg::Stop => break,
                }
            }

        });
    });

    let info = match ready_rx.recv() {
        Ok(Ok(info)) => info,
        Ok(Err(e)) => {
            set_last_error(&format!("session setup failed: {e}"));
            let _ = thread.join();
            return -2;
        }
        Err(_) => {
            set_last_error("session setup thread ended without reporting a result");
            return -3;
        }
    };

    if !out_selection.is_null() {
        let mut sel = crate::DbLinuxSelection {
            node_id: info.node_id,
            width: info.width.max(1.0) as u32,
            height: info.height.max(1.0) as u32,
            is_monitor: if info.is_monitor { 1 } else { 0 },
            ..Default::default()
        };
        if let Some(tok) = &info.token {
            let bytes = tok.as_bytes();
            let count = bytes.len().min(sel.restore_token.len().saturating_sub(1));
            sel.restore_token[..count].copy_from_slice(&bytes[..count]);
            sel.restore_token_len = count as u32;
        }
        unsafe { *out_selection = sel };
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

fn send(session_id: u64, msg: Msg) -> bool {
    if let Ok(map) = sessions().lock() {
        if let Some(s) = map.get(&session_id) {
            return s.tx.try_send(msg).is_ok();
        }
    }
    false
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_motion(session_id: u64, u: f64, v: f64) {
    send(session_id, Msg::Motion { u, v });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_down(session_id: u64, slot: u32, u: f64, v: f64) -> i32 {
    if send(session_id, Msg::TouchDown { slot, u, v }) {
        0
    } else {
        -1
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_motion(session_id: u64, slot: u32, u: f64, v: f64) {
    send(session_id, Msg::TouchMotion { slot, u, v });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_touch_up(session_id: u64, slot: u32) {
    send(session_id, Msg::TouchUp { slot });
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_input_scroll(session_id: u64, steps: i32) {
    send(session_id, Msg::Scroll { steps });
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
