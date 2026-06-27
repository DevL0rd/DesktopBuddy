
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::convert::TryInto;
use std::rc::Rc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use pipewire as pw;
use pw::types::ObjectType;
use pw::{properties::properties, spa};
use spa::param::format::{MediaSubtype, MediaType};
use spa::param::format_utils;
use spa::pod::Pod;

const SAMPLE_RATE: u32 = 48000;
const CHANNELS: u32 = 2;
const OUR_NODE_NAME: &str = "desktopbuddy-audio";

const MAX_BUFFERED_SAMPLES: usize = (SAMPLE_RATE as usize) * (CHANNELS as usize) * 2;

struct AudioRuntime {
    stop: pw::channel::Sender<()>,
    pcm: Arc<Mutex<Vec<f32>>>,
    worker: Option<JoinHandle<()>>,
}

impl AudioRuntime {
    fn stop(&mut self) {
        let _ = self.stop.send(());
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

static AUDIO_CAPTURES: OnceLock<Mutex<HashMap<u64, AudioRuntime>>> = OnceLock::new();
static NEXT_AUDIO_ID: AtomicU64 = AtomicU64::new(1);

fn audio_captures() -> &'static Mutex<HashMap<u64, AudioRuntime>> {
    AUDIO_CAPTURES.get_or_init(|| Mutex::new(HashMap::new()))
}

struct UserData {
    format: spa::param::audio::AudioInfoRaw,
}

struct TapState {
    self_pid: u32,
    our_node_id: Option<u32>,
    link_factory: Option<String>,
    include_node: HashMap<u32, bool>,
    out_ports: HashMap<u32, (u32, String)>,
    our_in_ports: HashMap<u32, String>,
    created: HashSet<(u32, u32)>,
    links: Vec<pw::link::Link>,
}

impl TapState {
    fn new(self_pid: u32) -> Self {
        Self {
            self_pid,
            our_node_id: None,
            link_factory: None,
            include_node: HashMap::new(),
            out_ports: HashMap::new(),
            our_in_ports: HashMap::new(),
            created: HashSet::new(),
            links: Vec::new(),
        }
    }
}

fn channels_match(a: &str, b: &str) -> bool {
    a == b || a == "MONO" || b == "MONO" || a.is_empty() || b.is_empty()
}

fn try_link(state: &Rc<RefCell<TapState>>, core: &pw::core::CoreRc) {
    let mut st = state.borrow_mut();
    let (Some(our_node), Some(factory)) = (st.our_node_id, st.link_factory.clone()) else {
        return;
    };
    if st.our_in_ports.is_empty() {
        return;
    }

    let mut to_create: Vec<(u32, u32, u32)> = Vec::new();
    for (&out_port, (out_node, out_ch)) in st.out_ports.iter() {
        if st.include_node.get(out_node) != Some(&true) {
            continue;
        }
        for (&in_port, in_ch) in st.our_in_ports.iter() {
            if channels_match(out_ch, in_ch) && !st.created.contains(&(out_port, in_port)) {
                to_create.push((*out_node, out_port, in_port));
            }
        }
    }

    for (out_node, out_port, in_port) in to_create {
        let mut props = pw::properties::PropertiesBox::new();
        props.insert("link.output.node", out_node.to_string());
        props.insert("link.output.port", out_port.to_string());
        props.insert("link.input.node", our_node.to_string());
        props.insert("link.input.port", in_port.to_string());
        match core.create_object::<pw::link::Link>(&factory, &props) {
            Ok(link) => {
                st.created.insert((out_port, in_port));
                st.links.push(link);
                eprintln!(
                    "[DesktopBuddy audio] tapped node {} port {} -> capture port {}",
                    out_node, out_port, in_port
                );
            }
            Err(e) => eprintln!("[DesktopBuddy audio] link create failed: {e}"),
        }
    }
}

fn handle_global(
    global: &pw::registry::GlobalObject<&spa::utils::dict::DictRef>,
    state: &Rc<RefCell<TapState>>,
    core: &pw::core::CoreRc,
) {
    let Some(props) = global.props else { return };
    match global.type_ {
        ObjectType::Factory => {
            if props.get("factory.type.name") == Some(ObjectType::Link.to_str()) {
                if let Some(name) = props.get("factory.name") {
                    state.borrow_mut().link_factory = Some(name.to_owned());
                }
            }
        }
        ObjectType::Node => {
            let node_name = props.get("node.name").unwrap_or("");
            let media_class = props.get("media.class").unwrap_or("");
            if node_name == OUR_NODE_NAME {
                state.borrow_mut().our_node_id = Some(global.id);
            } else if media_class == "Stream/Output/Audio" {
                let pid = props
                    .get("application.process.id")
                    .and_then(|p| p.parse::<u32>().ok())
                    .unwrap_or(0);
                let mut st = state.borrow_mut();
                let include = pid != st.self_pid;
                st.include_node.insert(global.id, include);
            }
        }
        ObjectType::Port => {
            let node_id = props
                .get("node.id")
                .and_then(|n| n.parse::<u32>().ok());
            let Some(node_id) = node_id else { return };
            let direction = props.get("port.direction").unwrap_or("");
            let channel = props.get("audio.channel").unwrap_or("").to_owned();
            let mut st = state.borrow_mut();
            if Some(node_id) == st.our_node_id && direction == "in" {
                st.our_in_ports.insert(global.id, channel);
            } else if direction == "out" {
                st.out_ports.insert(global.id, (node_id, channel));
            }
        }
        _ => {}
    }
    try_link(state, core);
}

fn handle_global_remove(id: u32, state: &Rc<RefCell<TapState>>) {
    let mut st = state.borrow_mut();
    st.include_node.remove(&id);
    st.out_ports.remove(&id);
    st.our_in_ports.remove(&id);

    st.created
        .retain(|&(out_port, in_port)| out_port != id && in_port != id);
}

fn run_capture(pcm: Arc<Mutex<Vec<f32>>>, rx: pw::channel::Receiver<()>) -> Result<(), pw::Error> {
    pw::init();

    let mainloop = pw::main_loop::MainLoopRc::new(None)?;
    let context = pw::context::ContextRc::new(&mainloop, None)?;
    let core = context.connect_rc(None)?;
    let registry = core.get_registry_rc()?;

    let state = Rc::new(RefCell::new(TapState::new(std::process::id())));

    let quit_loop = mainloop.clone();
    let _recv = rx.attach(mainloop.loop_(), move |_| quit_loop.quit());

    let props = properties! {
        *pw::keys::MEDIA_TYPE => "Audio",
        *pw::keys::MEDIA_CATEGORY => "Capture",
        *pw::keys::MEDIA_ROLE => "Music",
        *pw::keys::NODE_NAME => OUR_NODE_NAME,
        "node.autoconnect" => "false",
    };

    let data = UserData {
        format: Default::default(),
    };

    let stream = pw::stream::StreamBox::new(&core, OUR_NODE_NAME, props)?;
    let pcm_cb = pcm.clone();
    let _listener = stream
        .add_local_listener_with_user_data(data)
        .param_changed(|_, user_data, id, param| {
            let Some(param) = param else { return };
            if id != pw::spa::param::ParamType::Format.as_raw() {
                return;
            }
            let Ok((media_type, media_subtype)) = format_utils::parse_format(param) else {
                return;
            };
            if media_type != MediaType::Audio || media_subtype != MediaSubtype::Raw {
                return;
            }
            let _ = user_data.format.parse(param);
        })
        .process(move |stream, _user_data| {
            let Some(mut buffer) = stream.dequeue_buffer() else {
                return;
            };
            let datas = buffer.datas_mut();
            if datas.is_empty() {
                return;
            }
            let chunk_size = datas[0].chunk().size() as usize;
            let Some(samples) = datas[0].data() else {
                return;
            };
            let count = chunk_size.min(samples.len()) / std::mem::size_of::<f32>();
            if count == 0 {
                return;
            }
            if let Ok(mut guard) = pcm_cb.lock() {
                for i in 0..count {
                    let start = i * std::mem::size_of::<f32>();
                    let bytes = &samples[start..start + std::mem::size_of::<f32>()];
                    guard.push(f32::from_le_bytes(bytes.try_into().unwrap()));
                }
                if guard.len() > MAX_BUFFERED_SAMPLES {
                    let drop = guard.len() - MAX_BUFFERED_SAMPLES;
                    guard.drain(0..drop);
                }
            }
        })
        .register()?;

    let reg_state = state.clone();
    let reg_core = core.clone();
    let remove_state = state.clone();
    let _reg_listener = registry
        .add_listener_local()
        .global(move |global| handle_global(global, &reg_state, &reg_core))
        .global_remove(move |id| handle_global_remove(id, &remove_state))
        .register();

    let mut audio_info = spa::param::audio::AudioInfoRaw::new();
    audio_info.set_format(spa::param::audio::AudioFormat::F32LE);
    audio_info.set_rate(SAMPLE_RATE);
    audio_info.set_channels(CHANNELS);
    let obj = pw::spa::pod::Object {
        type_: pw::spa::utils::SpaTypes::ObjectParamFormat.as_raw(),
        id: pw::spa::param::ParamType::EnumFormat.as_raw(),
        properties: audio_info.into(),
    };
    let values: Vec<u8> = pw::spa::pod::serialize::PodSerializer::serialize(
        std::io::Cursor::new(Vec::new()),
        &pw::spa::pod::Value::Object(obj),
    )
    .map_err(|_| pw::Error::CreationFailed)?
    .0
    .into_inner();
    let mut params = [Pod::from_bytes(&values).ok_or(pw::Error::CreationFailed)?];

    stream.connect(
        spa::utils::Direction::Input,
        None,
        pw::stream::StreamFlags::MAP_BUFFERS | pw::stream::StreamFlags::RT_PROCESS,
        &mut params,
    )?;

    mainloop.run();
    Ok(())
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_audio_start(out_capture_id: *mut u64) -> i32 {
    if out_capture_id.is_null() {
        return -1;
    }
    unsafe { *out_capture_id = 0 };

    let pcm = Arc::new(Mutex::new(Vec::<f32>::new()));
    let (tx, rx) = pw::channel::channel::<()>();
    let pcm_thread = pcm.clone();
    let worker = thread::spawn(move || {
        if let Err(e) = run_capture(pcm_thread, rx) {
            eprintln!("[DesktopBuddy audio] capture ended: {e}");
        }
    });

    let capture_id = NEXT_AUDIO_ID.fetch_add(1, Ordering::Relaxed);
    let mut captures = match audio_captures().lock() {
        Ok(captures) => captures,
        Err(_) => return -2,
    };
    captures.insert(
        capture_id,
        AudioRuntime {
            stop: tx,
            pcm,
            worker: Some(worker),
        },
    );

    unsafe { *out_capture_id = capture_id };
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_audio_poll(capture_id: u64, dst: *mut f32, max_samples: i32) -> i32 {
    if dst.is_null() || max_samples <= 0 {
        return 0;
    }
    let captures = match audio_captures().lock() {
        Ok(captures) => captures,
        Err(_) => return 0,
    };
    let Some(runtime) = captures.get(&capture_id) else {
        return 0;
    };
    let mut guard = match runtime.pcm.lock() {
        Ok(guard) => guard,
        Err(_) => return 0,
    };
    let count = guard.len().min(max_samples as usize);
    if count == 0 {
        return 0;
    }
    unsafe { std::ptr::copy_nonoverlapping(guard.as_ptr(), dst, count) };
    guard.drain(0..count);
    count as i32
}

#[unsafe(no_mangle)]
pub extern "C" fn db_linux_audio_stop(capture_id: u64) {
    if let Ok(mut captures) = audio_captures().lock() {
        if let Some(mut runtime) = captures.remove(&capture_id) {
            runtime.stop();
        }
    }
}
