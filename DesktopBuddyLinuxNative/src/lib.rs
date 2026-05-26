use std::os::fd::RawFd;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::Duration;

use drm_fourcc::{DrmFormat, DrmFourcc, DrmModifier};
use libc::dup;
use wlx_capture::frame::{DmabufFrame, WlxFrame};
use wlx_capture::pipewire::{pipewire_select_screen, PipewireCapture};
use wlx_capture::WlxCapture;

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
    pub modifier: u64,
    pub has_modifier: u32,
    pub plane_count: u32,
    pub mouse_valid: u32,
    pub mouse_x: f32,
    pub mouse_y: f32,
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
        }
    }
}

struct CaptureRuntime {
    stop: Arc<AtomicBool>,
    latest: Arc<Mutex<Option<DbLinuxFrame>>>,
    worker: Option<JoinHandle<()>>,
}

impl CaptureRuntime {
    fn stop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

impl Drop for CaptureRuntime {
    fn drop(&mut self) {
        self.stop();
    }
}

struct CallbackState {
    latest: Arc<Mutex<Option<DbLinuxFrame>>>,
}

static CAPTURE: OnceLock<Mutex<Option<CaptureRuntime>>> = OnceLock::new();

fn capture_slot() -> &'static Mutex<Option<CaptureRuntime>> {
    CAPTURE.get_or_init(|| Mutex::new(None))
}

fn select_screen() -> Result<wlx_capture::pipewire::PipewireSelectScreenResult, wlx_capture::pipewire::AshpdError> {
    async_std::task::block_on(pipewire_select_screen(
        None, true, false, false, false,
    ))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_select_stream(out_selection: *mut DbLinuxSelection) -> i32 {
    if out_selection.is_null() {
        return -1;
    }

    let selection = match select_screen() {
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
        let count = bytes.len().min(result.restore_token.len().saturating_sub(1));
        result.restore_token[..count].copy_from_slice(&bytes[..count]);
        result.restore_token_len = count as u32;
    }

    unsafe { *out_selection = result };
    0
}

fn default_formats(modifiers: &[u64]) -> Vec<DrmFormat> {
    let mut modifiers: Vec<DrmModifier> = modifiers.iter().copied().map(DrmModifier::from).collect();
    if !modifiers.iter().any(|modifier| u64::from(*modifier) == 0) {
        modifiers.push(DrmModifier::from(0));
    }

    [DrmFourcc::Argb8888, DrmFourcc::Xrgb8888]
        .into_iter()
        .flat_map(|code| {
            modifiers
                .iter()
                .copied()
                .map(move |modifier| DrmFormat { code, modifier })
        })
        .collect()
}

fn duplicate_frame_fd(frame: &DmabufFrame) -> Option<DbLinuxFrame> {
    if frame.num_planes != 1 {
        return None;
    }

    let plane = frame.planes[0];
    let fd: RawFd = plane.fd?;
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
        offset: plane.offset,
        stride: plane.stride,
        modifier: u64::from(frame.format.drm_format.modifier),
        has_modifier: 1,
        plane_count: 1,
        mouse_valid: u32::from(frame.mouse.is_some()),
        mouse_x: frame.mouse.as_ref().map_or(0.0, |mouse| mouse.x),
        mouse_y: frame.mouse.as_ref().map_or(0.0, |mouse| mouse.y),
    })
}

fn handle_frame(state: &CallbackState, frame: WlxFrame) -> Option<()> {
    match frame {
        WlxFrame::Dmabuf(frame) => {
            if let Some(frame) = duplicate_frame_fd(&frame)
                && let Ok(mut latest) = state.latest.lock()
            {
                if let Some(old) = latest.take() {
                    if old.fd >= 0 {
                        unsafe { libc::close(old.fd) };
                    }
                }
                *latest = Some(frame);
            }
        }
        WlxFrame::MemFd(_) => {}
        WlxFrame::MemPtr(_) => {}
        WlxFrame::Implicit => {}
    }

    Some(())
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_start(modifiers: *const u64, modifier_count: usize) -> i32 {
    let modifiers = if modifiers.is_null() || modifier_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(modifiers, modifier_count) }.to_vec()
    };

    capture_start_with_formats(default_formats(&modifiers))
}

fn capture_start_with_formats(formats: Vec<DrmFormat>) -> i32 {
    let selection = match select_screen() {
        Ok(selection) => selection,
        Err(_) => return -10,
    };

    let Some(stream) = selection.streams.first() else {
        return -11;
    };

    capture_start_node_with_formats(stream.node_id, formats)
}

fn capture_start_node_with_formats(node_id: u32, formats: Vec<DrmFormat>) -> i32 {
    let mut slot = match capture_slot().lock() {
        Ok(slot) => slot,
        Err(_) => return -1,
    };

    if let Some(runtime) = slot.as_mut() {
        runtime.stop();
    }

    let stop = Arc::new(AtomicBool::new(false));
    let latest = Arc::new(Mutex::new(None));
    let thread_stop = stop.clone();
    let thread_latest = latest.clone();

    let worker = thread::spawn(move || {
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

    *slot = Some(CaptureRuntime {
        stop,
        latest,
        worker: Some(worker),
    });

    0
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_start_node(node_id: u32, modifiers: *const u64, modifier_count: usize) -> i32 {
    if node_id == 0 {
        return -12;
    }

    let modifiers = if modifiers.is_null() || modifier_count == 0 {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(modifiers, modifier_count) }.to_vec()
    };

    capture_start_node_with_formats(node_id, default_formats(&modifiers))
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_capture_poll(out_frame: *mut DbLinuxFrame) -> i32 {
    if out_frame.is_null() {
        return -1;
    }

    let slot = match capture_slot().lock() {
        Ok(slot) => slot,
        Err(_) => return -2,
    };

    let Some(runtime) = slot.as_ref() else {
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
pub extern "C" fn db_linux_capture_stop() {
    if let Ok(mut slot) = capture_slot().lock() {
        if let Some(mut runtime) = slot.take() {
            runtime.stop();
        }
    }
}
