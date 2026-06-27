use std::collections::HashMap;
use std::os::fd::RawFd;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use libc::dup;
use pipewire as pw;
use wlx_capture::WlxCapture;

mod audio;
mod portal_input;
use wlx_capture::frame::{MemFdFrame, WlxFrame};
use wlx_capture::pipewire::{PipewireCapture, pipewire_select_screen};

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct DbLinuxFrame {
    pub status: i32,
    pub fd: i32,
    pub width: u32,
    pub height: u32,
    pub fourcc: u32,
    pub offset: u32,
    pub stride: i32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct DbLinuxSelection {
    pub status: i32,
    pub node_id: u32,
    pub width: u32,
    pub height: u32,
    pub position_x: i32,
    pub position_y: i32,
    pub has_position: u32,
    pub restore_token_len: u32,
    pub restore_token: [u8; 256],

    pub is_monitor: u32,

    pub name_len: u32,
    pub name: [u8; 256],
}

impl Default for DbLinuxSelection {
    fn default() -> Self {
        Self {
            status: 0,
            node_id: 0,
            width: 0,
            height: 0,
            position_x: 0,
            position_y: 0,
            has_position: 0,
            restore_token_len: 0,
            restore_token: [0; 256],
            is_monitor: 0,
            name_len: 0,
            name: [0; 256],
        }
    }
}

fn fetch_node_label(node_id: u32) -> (String, bool) {
    let out = Arc::new(Mutex::new((String::new(), false)));
    let (tx, rx) = pw::channel::channel::<()>();

    let timer = thread::spawn(move || {
        thread::sleep(Duration::from_millis(400));
        let _ = tx.send(());
    });

    let scan = out.clone();
    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(move || {
        pw::init();
        let mainloop = match pw::main_loop::MainLoopRc::new(None) {
            Ok(m) => m,
            Err(_) => return,
        };
        let context = match pw::context::ContextRc::new(&mainloop, None) {
            Ok(c) => c,
            Err(_) => return,
        };
        let core = match context.connect_rc(None) {
            Ok(c) => c,
            Err(_) => return,
        };
        let registry = match core.get_registry_rc() {
            Ok(r) => r,
            Err(_) => return,
        };

        let quit = mainloop.clone();
        let _recv = rx.attach(mainloop.loop_(), move |_| quit.quit());

        let found = scan.clone();
        let _listener = registry
            .add_listener_local()
            .global(move |global| {
                if global.id != node_id {
                    return;
                }
                let Some(props) = global.props else { return };
                let name = props
                    .get("media.name")
                    .or_else(|| props.get("node.description"))
                    .or_else(|| props.get("node.name"))
                    .unwrap_or("");
                let role = props.get("media.role").unwrap_or("");
                if let Ok(mut guard) = found.lock() {
                    guard.0 = name.to_string();
                    guard.1 = role.eq_ignore_ascii_case("Screen");
                }
            })
            .register();

        mainloop.run();
    }));

    let _ = timer.join();
    out.lock().map(|g| g.clone()).unwrap_or_default()
}

struct CaptureRuntime {
    stop: Arc<AtomicBool>,
    latest: Arc<Mutex<Option<DbLinuxFrame>>>,
    worker: Option<JoinHandle<()>>,

    alive: Arc<AtomicBool>,
    monitor_stop: Option<pw::channel::Sender<()>>,
    monitor: Option<JoinHandle<()>>,
}

impl CaptureRuntime {
    fn stop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(tx) = self.monitor_stop.take() {
            let _ = tx.send(());
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        if let Some(monitor) = self.monitor.take() {
            let _ = monitor.join();
        }
    }
}

fn run_node_monitor(
    node_id: u32,
    alive: Arc<AtomicBool>,
    rx: pw::channel::Receiver<()>,
) -> Result<(), pw::Error> {
    pw::init();

    let mainloop = pw::main_loop::MainLoopRc::new(None)?;
    let context = pw::context::ContextRc::new(&mainloop, None)?;
    let core = context.connect_rc(None)?;
    let registry = core.get_registry_rc()?;

    let quit_loop = mainloop.clone();
    let _recv = rx.attach(mainloop.loop_(), move |_| quit_loop.quit());

    let remove_alive = alive.clone();
    let remove_quit = mainloop.clone();
    let _reg_listener = registry
        .add_listener_local()
        .global_remove(move |id| {
            if id == node_id {
                remove_alive.store(false, Ordering::Release);
                remove_quit.quit();
            }
        })
        .register();

    mainloop.run();
    Ok(())
}

impl Drop for CaptureRuntime {
    fn drop(&mut self) {
        self.stop();
    }
}

struct CallbackState {
    latest: Arc<Mutex<Option<DbLinuxFrame>>>,
}

static CAPTURES: OnceLock<Mutex<HashMap<u64, CaptureRuntime>>> = OnceLock::new();
static NEXT_CAPTURE_ID: AtomicU64 = AtomicU64::new(1);

fn captures() -> &'static Mutex<HashMap<u64, CaptureRuntime>> {
    CAPTURES.get_or_init(|| Mutex::new(HashMap::new()))
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct DbLinuxStreamInfo {
    pub running: i32,
    pub broken: i32,
    pub width: i32,
    pub height: i32,
    pub write_pos: i64,
    pub keyframe_pos: i64,
    pub frames: i64,
}

fn select_screen(
    token: Option<&str>,
) -> Result<wlx_capture::pipewire::PipewireSelectScreenResult, wlx_capture::pipewire::AshpdError> {

    async_std::task::block_on(pipewire_select_screen(token, true, false, true, false))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_select_stream(
    token_ptr: *const u8,
    token_len: usize,
    out_selection: *mut DbLinuxSelection,
) -> i32 {
    if out_selection.is_null() {
        return -1;
    }

    let token: Option<String> = if token_ptr.is_null() || token_len == 0 {
        None
    } else {
        let slice = unsafe { std::slice::from_raw_parts(token_ptr, token_len) };
        std::str::from_utf8(slice).ok().map(|s| s.to_owned())
    };

    let selection = match select_screen(token.as_deref()) {
        Ok(selection) => selection,
        Err(_) => {
            unsafe {
                *out_selection = DbLinuxSelection {
                    status: -10,
                    ..Default::default()
                };
            }
            return -10;
        }
    };

    let Some(stream) = selection.streams.first() else {
        unsafe {
            *out_selection = DbLinuxSelection {
                status: -11,
                ..Default::default()
            };
        }
        return -11;
    };

    let mut result = DbLinuxSelection {
        status: 0,
        node_id: stream.node_id,
        ..Default::default()
    };

    if let Some((width, height)) = stream.size {
        result.width = width.max(1) as u32;
        result.height = height.max(1) as u32;
    }

    if let Some((x, y)) = stream.position {
        result.position_x = x;
        result.position_y = y;
        result.has_position = 1;
    }

    if let Some(token) = selection.restore_token {
        let bytes = token.as_bytes();
        let count = bytes
            .len()
            .min(result.restore_token.len().saturating_sub(1));
        result.restore_token[..count].copy_from_slice(&bytes[..count]);
        result.restore_token_len = count as u32;
    }

    let (name, is_monitor) = fetch_node_label(result.node_id);
    result.is_monitor = if is_monitor { 1 } else { 0 };
    if !name.is_empty() {
        let bytes = name.as_bytes();
        let count = bytes.len().min(result.name.len().saturating_sub(1));
        result.name[..count].copy_from_slice(&bytes[..count]);
        result.name_len = count as u32;
    }

    unsafe { *out_selection = result };
    0
}

fn duplicate_memfd_frame(frame: &MemFdFrame) -> Option<DbLinuxFrame> {
    let fd: RawFd = frame.plane.fd?;
    let owned_fd = unsafe { dup(fd) };
    if owned_fd < 0 {
        return None;
    }

    Some(DbLinuxFrame {
        status: 0,
        fd: owned_fd,
        width: frame.format.width,
        height: frame.format.height,
        fourcc: frame.format.drm_format.code as u32,
        offset: frame.plane.offset,
        stride: frame.plane.stride,
    })
}

fn store_latest_frame(state: &CallbackState, frame: DbLinuxFrame) {
    if let Ok(mut latest) = state.latest.lock() {
        if let Some(old) = latest.take() {
            if old.fd >= 0 {
                unsafe { libc::close(old.fd) };
            }
        }
        *latest = Some(frame);
    }
}

fn copy_frame_bytes(frame: &mut DbLinuxFrame, dst: *mut u8, dst_size: usize) -> i32 {
    if dst.is_null() || frame.fd < 0 {
        return -20;
    }

    let fd = frame.fd;
    frame.fd = -1;
    if frame.width == 0 || frame.height == 0 || frame.stride <= 0 {
        unsafe { libc::close(fd) };
        return -21;
    }

    let row_bytes = frame.width as usize * 4;
    let stride = frame.stride as usize;
    if row_bytes > stride {
        unsafe { libc::close(fd) };
        return -22;
    }

    let Some(required) = row_bytes.checked_mul(frame.height as usize) else {
        unsafe { libc::close(fd) };
        return -23;
    };
    if dst_size < required {
        unsafe { libc::close(fd) };
        return -24;
    }

    for y in 0..frame.height as usize {
        let src_offset = frame.offset as i64 + (y * stride) as i64;
        let row = unsafe { dst.add(y * row_bytes) };
        let mut copied = 0usize;
        while copied < row_bytes {
            let n = unsafe {
                libc::pread(
                    fd,
                    row.add(copied).cast(),
                    row_bytes - copied,
                    (src_offset + copied as i64) as libc::off_t,
                )
            };
            if n <= 0 {
                unsafe { libc::close(fd) };
                return -25;
            }
            copied += n as usize;
        }
    }

    unsafe { libc::close(fd) };
    0
}

fn handle_frame(state: &CallbackState, frame: WlxFrame) -> Option<()> {
    match frame {
        WlxFrame::MemFd(frame) => {
            if let Some(frame) = duplicate_memfd_frame(&frame) {
                store_latest_frame(state, frame);
            }
        }
        WlxFrame::Dmabuf(_) => {}
        WlxFrame::MemPtr(_) => {}
        WlxFrame::Implicit => {}
    }

    Some(())
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_start_node(node_id: u32, out_capture_id: *mut u64) -> i32 {
    if node_id == 0 {
        return -12;
    }
    if out_capture_id.is_null() {
        return -13;
    }

    let mut captures = match captures().lock() {
        Ok(captures) => captures,
        Err(_) => return -1,
    };

    let stop = Arc::new(AtomicBool::new(false));
    let latest = Arc::new(Mutex::new(None));
    let thread_stop = stop.clone();
    let thread_latest = latest.clone();

    let worker = thread::spawn(move || {
        let formats = Vec::new();
        let state = CallbackState {
            latest: thread_latest.clone(),
        };

        let mut capture: PipewireCapture<()> =
            PipewireCapture::new("desktopbuddy-linux-native".into(), node_id);
        <PipewireCapture<()> as WlxCapture<CallbackState, ()>>::init(
            &mut capture,
            &formats,
            state,
            handle_frame,
        );
        <PipewireCapture<()> as WlxCapture<CallbackState, ()>>::resume(&mut capture);

        while !thread_stop.load(Ordering::Acquire) {
            let _ = <PipewireCapture<()> as WlxCapture<CallbackState, ()>>::receive(&mut capture);
            thread::sleep(Duration::from_millis(4));
        }

        <PipewireCapture<()> as WlxCapture<CallbackState, ()>>::pause(&mut capture);
    });

    let alive = Arc::new(AtomicBool::new(true));
    let (monitor_tx, monitor_rx) = pw::channel::channel::<()>();
    let monitor_alive = alive.clone();
    let monitor = thread::spawn(move || {
        if let Err(e) = run_node_monitor(node_id, monitor_alive, monitor_rx) {
            log::warn!("node monitor ended: {e}");
        }
    });

    let capture_id = NEXT_CAPTURE_ID.fetch_add(1, Ordering::Relaxed);
    captures.insert(
        capture_id,
        CaptureRuntime {
            stop,
            latest,
            worker: Some(worker),
            alive,
            monitor_stop: Some(monitor_tx),
            monitor: Some(monitor),
        },
    );

    unsafe { *out_capture_id = capture_id };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_alive(capture_id: u64) -> i32 {
    let captures = match captures().lock() {
        Ok(captures) => captures,
        Err(_) => return 1,
    };
    match captures.get(&capture_id) {
        Some(runtime) => {
            if runtime.alive.load(Ordering::Acquire) {
                1
            } else {
                0
            }
        }
        None => 1,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_poll(capture_id: u64, out_frame: *mut DbLinuxFrame) -> i32 {
    if out_frame.is_null() {
        return -1;
    }

    let captures = match captures().lock() {
        Ok(captures) => captures,
        Err(_) => return -2,
    };

    let Some(runtime) = captures.get(&capture_id) else {
        return 1;
    };

    let mut latest = match runtime.latest.lock() {
        Ok(latest) => latest,
        Err(_) => return -3,
    };

    let Some(frame) = latest.take() else {
        return 1;
    };

    unsafe { *out_frame = frame };
    frame.status
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_stop(capture_id: u64) {
    if let Ok(mut captures) = captures().lock() {
        if let Some(mut runtime) = captures.remove(&capture_id) {
            runtime.stop();
        }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_stream_start(
    _node_id: u32,
    _fps: i32,
    _bitrate_mbps: i32,
    _max_res: i32,
    _rtsp_url: *const u8,
    _rtsp_url_len: usize,
    out_stream_id: *mut u64,
) -> i32 {
    if !out_stream_id.is_null() {
        unsafe { *out_stream_id = 0 };
    }

    -50
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_stream_read(
    _stream_id: u64,
    _dst: *mut u8,
    _dst_len: i32,
    _read_pos: *mut i64,
    _aligned: *mut i32,
    _min_keyframe_pos: i64,
    _keyframe_aligned: *mut i32,
    bytes_read: *mut i32,
) -> i32 {
    if !bytes_read.is_null() {
        unsafe { *bytes_read = 0 };
    }

    0
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_stream_info(_stream_id: u64, out_info: *mut DbLinuxStreamInfo) -> i32 {
    if out_info.is_null() {
        return -1;
    }

    unsafe {
        *out_info = DbLinuxStreamInfo {
            running: 0,
            broken: 1,
            width: 0,
            height: 0,
            write_pos: 0,
            keyframe_pos: -1,
            frames: 0,
        };
    }

    0
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_stream_stop(_stream_id: u64) {}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_frame_copy_and_close(
    frame: *mut DbLinuxFrame,
    dst: *mut u8,
    dst_size: usize,
) -> i32 {
    if frame.is_null() {
        return -1;
    }

    copy_frame_bytes(unsafe { &mut *frame }, dst, dst_size)
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_frame_close(frame: *mut DbLinuxFrame) {
    if frame.is_null() {
        return;
    }

    let frame = unsafe { &mut *frame };
    if frame.fd >= 0 {
        unsafe { libc::close(frame.fd) };
        frame.fd = -1;
    }
}
