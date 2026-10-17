use crate::{
    clipboard,
    core::{Protection, Replacements, extract_output, payload},
    http::Client,
    logging::Logger,
    rich,
    store::{Store, new_id},
};
use native_windows_gui as nwg;
use serde_json::{Value, json};
use std::{
    ptr::{null, null_mut},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Sender},
    },
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{Foundation::*, System::Threading::*, UI::WindowsAndMessaging::*};
use zeroize::Zeroizing;
#[derive(Clone)]
pub enum UiEvent {
    Phase(&'static str),
    Finished,
    Notify(String, String),
    Settings,
    Update(String),
    ReadyToExit,
    StartupChecked,
    Trial(Value),
    Log(String, Value),
}
#[derive(Clone)]
pub struct Bus {
    pub sender: Sender<UiEvent>,
    pub notice: nwg::NoticeSender,
    pub observe_trials: bool,
}
impl Bus {
    pub fn send(&self, event: UiEvent) {
        if self.sender.send(event).is_ok() {
            self.notice.notice();
        }
    }
}
struct Event(HANDLE);
unsafe impl Send for Event {}
unsafe impl Sync for Event {}
impl Drop for Event {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
enum Command {
    Run(Instant),
    Reload,
    Stop,
}
#[derive(Clone)]
pub struct Worker {
    commands: Sender<Command>,
    event: Arc<Event>,
    pub busy: Arc<AtomicBool>,
}
impl Worker {
    pub fn new(store: Store, logger: Logger, bus: Bus, owner: usize) -> Result<Self, String> {
        Self::configured(store, logger, bus, owner, None, None)
    }
    pub fn configured(
        store: Store,
        logger: Logger,
        bus: Bus,
        owner: usize,
        replay: Option<Arc<Replay>>,
        override_settings: Option<crate::core::Settings>,
    ) -> Result<Self, String> {
        let handle = unsafe { CreateEventW(null(), 0, 0, null()) };
        if handle.is_null() {
            return Err(clipboard::error());
        }
        let event = Arc::new(Event(handle));
        let (tx, rx) = mpsc::channel();
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let busy = Arc::new(AtomicBool::new(false));
        let worker_event = event.clone();
        let worker_busy = busy.clone();
        thread::Builder::new()
            .name("correction".into())
            .spawn(move || {
                if let Err(error) = clipboard::initialize_sta() {
                    let _ = ready_tx.send(Err(error.clone()));
                    bus.send(UiEvent::Notify("Startup failed".into(), error));
                    return;
                }
                let window = match clipboard::WorkerWindow::new() {
                    Ok(w) => w,
                    Err(e) => {
                        let _ = ready_tx.send(Err(e.clone()));
                        bus.send(UiEvent::Notify("Startup failed".into(), e));
                        return;
                    }
                };
                let mut settings =
                    override_settings.unwrap_or_else(|| store.load_settings().unwrap_or_default());
                let mut keys = store.load_keys().unwrap_or_default();
                let mut replacements = load_replacements(&store, &logger);
                let mut client = if replay.is_none() {
                    Client::new().ok().map(Arc::new)
                } else {
                    None
                };
                if let Some(client) = client.clone() {
                    std::thread::spawn(move || client.warm());
                }
                let _ = ready_tx.send(Ok(()));
                loop {
                    let mut stop = false;
                    while let Ok(command) = rx.try_recv() {
                        match command {
                            Command::Stop => {
                                stop = true;
                                break;
                            }
                            Command::Reload => {
                                if let Ok(next) = store.load_settings() {
                                    settings = next;
                                }
                                match store.load_keys() {
                                    Ok(next) => keys = next,
                                    Err(_) => {
                                        keys = Default::default();
                                        logger.event("apikey_load_failed");
                                    }
                                }
                                replacements = load_replacements(&store, &logger);
                            }
                            Command::Run(started) => {
                                let result =
                                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                                        if client.is_none() && replay.is_none() {
                                            match Client::new() {
                                                Ok(next) => client = Some(Arc::new(next)),
                                                Err(error) => logger.event(format!(
                                                    "client_init_failed error={}",
                                                    serde_json::to_string(&error)
                                                        .unwrap_or_default()
                                                )),
                                            };
                                        }
                                        run(
                                            started,
                                            RunContext {
                                                owner,
                                                listener: window.0 as usize,
                                                settings: &settings,
                                                key: if replay.is_some() {
                                                    Some("offline-replay-no-network")
                                                } else {
                                                    keys.active()
                                                },
                                                replacements: &replacements,
                                                client: client.as_deref(),
                                                replay: replay.as_deref(),
                                                logger: &logger,
                                                bus: &bus,
                                                store: &store,
                                            },
                                        )
                                    }));
                                if result.is_err() {
                                    logger.event("run_failed reason=panic");
                                    bus.send(UiEvent::Notify(
                                        "Spell check failed".into(),
                                        "The correction stopped safely.".into(),
                                    ));
                                }
                                worker_busy.store(false, Ordering::Release);
                                bus.send(UiEvent::Phase(""));
                                bus.send(UiEvent::Finished);
                                replacements = load_replacements(&store, &logger);
                            }
                        }
                    }
                    if stop {
                        break;
                    }
                    unsafe {
                        let handles = [worker_event.0];
                        let result = MsgWaitForMultipleObjectsEx(
                            1,
                            handles.as_ptr(),
                            240000,
                            QS_ALLINPUT,
                            MWMO_INPUTAVAILABLE,
                        );
                        if result == WAIT_TIMEOUT && replay.is_none() {
                            if let Some(client) = client.clone() {
                                std::thread::spawn(move || client.warm());
                            } else {
                                client = Client::new().ok().map(Arc::new);
                                if let Some(client) = &client {
                                    client.warm();
                                }
                            }
                        }
                        let mut msg = std::mem::zeroed();
                        while PeekMessageW(&mut msg, null_mut(), 0, 0, PM_REMOVE) != 0 {
                            TranslateMessage(&msg);
                            DispatchMessageW(&msg);
                        }
                    }
                }
            })
            .map_err(|e| e.to_string())?;
        ready_rx
            .recv_timeout(Duration::from_secs(5))
            .map_err(|_| "Correction worker did not become ready within five seconds")??;
        Ok(Self {
            commands: tx,
            event,
            busy,
        })
    }
    pub fn trigger(&self, logger: &Logger) {
        if self
            .busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            logger.event("guard_rejected reason=already_running");
            return;
        }
        if self.commands.send(Command::Run(Instant::now())).is_err() {
            self.busy.store(false, Ordering::Release);
        }
        unsafe {
            SetEvent(self.event.0);
        }
    }
    pub fn reload(&self) {
        let _ = self.commands.send(Command::Reload);
        unsafe {
            SetEvent(self.event.0);
        }
    }
    pub fn stop(&self) {
        let _ = self.commands.send(Command::Stop);
        unsafe {
            SetEvent(self.event.0);
        }
    }
}
fn load_replacements(store: &Store, logger: &Logger) -> Replacements {
    match std::fs::read(&store.replacements_path)
        .ok()
        .map(|b| Replacements::parse(&b))
    {
        Some(Ok(r)) => r,
        _ => {
            logger.event("replacements_unavailable");
            Replacements::default()
        }
    }
}
fn ms(start: Instant) -> f64 {
    start.elapsed().as_secs_f64() * 1000.
}
struct RunContext<'a> {
    owner: usize,
    listener: usize,
    settings: &'a crate::core::Settings,
    key: Option<&'a str>,
    replacements: &'a Replacements,
    client: Option<&'a Client>,
    replay: Option<&'a Replay>,
    logger: &'a Logger,
    bus: &'a Bus,
    store: &'a Store,
}
fn run(started: Instant, context: RunContext<'_>) -> Result<(), String> {
    let RunContext {
        owner,
        listener,
        settings,
        key,
        replacements,
        client,
        replay,
        logger,
        bus,
        store,
    } = context;
    let id = new_id();
    let target = clipboard::foreground();
    let evidence = if settings.developer_logging {
        crate::evidence::Evidence::begin(target.clone())
    } else {
        None
    };
    let mut backup = None;
    let mut detail = json!({"run_id":id,"status":"run_failed","model":settings.model,"developer_logging_enabled":settings.developer_logging,"input_text":"","output_text":"","paste_text":"","paste_html":"","events":["run_started"]});
    let mut corrected_on_clipboard = false;
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let key = key
            .filter(|s| !s.trim().is_empty())
            .ok_or("Enter an OpenAI API key in Settings.")?;
        let key = Zeroizing::new(key.to_owned());
        if client.is_none()&&replay.is_none(){return Err("Windows HTTP client unavailable; selection was left unchanged.".into());}
        backup=Some(clipboard::Backup::capture(owner as _)?);
        bus.send(UiEvent::Phase("Copying text..."));
        let capture_start = Instant::now();
        let captured = clipboard::capture(listener as _)?;
        let capture_ms = ms(capture_start);
        detail["input_text"] = json!(captured.text);
        detail["input_chars"] = json!(captured.text.encode_utf16().count());
        detail["clipboard_html"] = json!(captured.html);
        if settings.developer_logging{use base64::Engine;detail["clipboard_chromium_mime_base64"]=json!(base64::engine::general_purpose::STANDARD.encode(&captured.native));}
        detail["clipboard_rtf"] = json!(captured.rtf);
        detail["clipboard_formats"] = json!(captured.formats);
        detail["clipboard_chromium_mime_status"] = json!(if settings.developer_logging
            && !captured.native.is_empty()
        {
            "ok"
        } else {
            "disabled"
        });
        let history = clipboard::write(owner as _, &captured.text, "", None, true).is_ok();
        detail["captured_text_history_excluded"] = json!(history);
        let input = crate::terminal::normalize(&captured.text, &target.2);
        let protection = Protection::new(&input)?;
        let request_payload = payload(&protection.text, &settings.model);
        detail["request_payload"] = json!(serde_json::to_string(&request_payload).unwrap_or_default());
        bus.send(UiEvent::Phase("Sending to AI..."));
        let request_started=Instant::now();
        let response=match replay{Some(replay)=>replay.request(&request_payload),None=>client.ok_or("HTTP client unavailable")?.request(&request_payload,&key,||bus.send(UiEvent::Phase("Waiting for AI...")))}?;
        let request_ms=ms(request_started);
        detail["status_code"] = json!(response.status);
        detail["request_attempts"] = json!(response.attempts);
        detail["raw_response"] = json!(response.raw);
        let usage=&response.body["usage"];
        detail["tokens"]=json!({"input":usage["input_tokens"],"output":usage["output_tokens"],"total":usage["total_tokens"],"cached":usage["input_tokens_details"]["cached_tokens"],"reasoning":usage["output_tokens_details"]["reasoning_tokens"]});
        if !(200..300).contains(&response.status) {
            detail["status"] = json!("request_failed");
            return Err(response.body["error"]["message"]
                .as_str()
                .unwrap_or("OpenAI request failed.")
                .to_owned());
        }
        let raw = extract_output(&response.body)?;
        detail["raw_ai_output"] = json!(raw);
        let post = Instant::now();
        let (output, applied, leak, echo) = replacements.process(&raw, &protection)?;
        let post_ms = ms(post);
        detail["output_text"] = json!(output);
        detail["output_chars"] = json!(output.encode_utf16().count());
        detail["text_changed"] = json!(captured.text != output);
        detail["replacements"] = json!({"applied":applied,"count":applied.len(),"protected_values":protection.entries.len()});
        detail["prompt_leak"] = json!({"triggered":leak});
        if echo {
            detail["events"]
                .as_array_mut()
                .unwrap()
                .push(json!("echoed_original_removed"));
        }
        let mapped = rich::rewrite(&captured.html, &captured.text, &output, &captured.native);
        detail["rich_text"] = serde_json::to_value(&mapped).unwrap_or_default();
        detail["paste_text"] = json!(mapped.text);
        if settings.developer_logging{use base64::Engine;detail["paste_chromium_mime_base64"]=json!(mapped.native.as_ref().map(|b|base64::engine::general_purpose::STANDARD.encode(b)).unwrap_or_default());}
        detail["paste_html"] = json!(mapped.html);
        detail["paste_text_chars"] = json!(mapped.text.encode_utf16().count());
        detail["paste_html_chars"] = json!(mapped.html.encode_utf16().count());
        bus.send(UiEvent::Phase("Pasting..."));
        let paste = Instant::now();
        let sequence = clipboard::write(
            owner as _,
            &mapped.text,
            &mapped.html,
            mapped.native.as_deref(),
            false,
        )?;
        corrected_on_clipboard = true;
        if let Some(backup)=backup.as_mut(){backup.disarm();}
        if settings.developer_logging {
            detail["clipboard_readback"] = crate::evidence::readback_before_input(owner, sequence);
        }
        // Fully-rendered native memory is already published; CloseClipboard precedes the input batch.
        clipboard::modifiers_released(Duration::from_millis(1200))?;
        clipboard::chord(0x56)?;
        let paste_ms = ms(paste);
        detail["status"] = json!("success");
        detail["corrected_text_on_clipboard"] = json!(true);
        detail["clipboard_write_sequence"] = json!(sequence);
        detail["timings"] = json!({"clipboard_ms":capture_ms,"request_ms":request_ms,"api_ms":request_ms,"request_send_ms":response.send_ms,"request_wait_ms":response.wait_ms,"response_download_ms":response.download_ms,"replacements_ms":post_ms,"paste_ms":paste_ms,"total_ms":ms(started)});
        Ok(())
    })).unwrap_or_else(|_|Err("The correction stopped safely after an internal error.".into()));
    if let Err(error) = &result {
        detail["error_message"] = json!(error);
        detail["corrected_text_on_clipboard"] = json!(corrected_on_clipboard);
        if !corrected_on_clipboard {
            detail["original_clipboard_restored"] =
                json!(backup.as_mut().is_some_and(|b| b.restore()));
        }
        bus.send(UiEvent::Notify("Spell check failed".into(), error.clone()));
        if key.is_none() {
            bus.send(UiEvent::Settings);
        }
    }
    logger.event(format!(
        "run_completed run_id={id} status={} total_ms={:.3}",
        detail["status"].as_str().unwrap_or("run_failed"),
        ms(started)
    ));
    if settings.developer_logging {
        detail["developer_evidence"] = if evidence.is_some() {
            json!({"schema_version":1,"manifest_path":format!("developer-evidence/{id}/manifest.json"),"status":"pending"})
        } else {
            json!({"schema_version":1,"status":"skipped_busy"})
        };
    }
    detail["active_exe"] = json!(target.2);
    detail["paste_method"] = json!("SendInput");
    let mut inline = detail.clone();
    for name in [
        "clipboard_html",
        "clipboard_rtf",
        "paste_text",
        "paste_html",
        "clipboard_chromium_mime_base64",
    ] {
        if let Some(text) = detail[name].as_str() {
            let chars = text.encode_utf16().count();
            inline[format!("{name}_chars")] = json!(chars);
            inline[format!("{name}_truncated")] = json!(chars > 512 * 1024);
            if chars > 512 * 1024 {
                let mut length = 0;
                let prefix: String = text
                    .chars()
                    .take_while(|c| {
                        length += c.len_utf16();
                        length <= 512 * 1024
                    })
                    .collect();
                inline[name] = json!(prefix);
            }
        }
    }
    if let Some(rich) = inline.get_mut("rich_text").and_then(Value::as_object_mut) {
        rich.remove("text");
        rich.remove("html");
        rich.insert(
            "source_html_present".into(),
            json!(
                detail["clipboard_html"]
                    .as_str()
                    .is_some_and(|s| !s.is_empty())
            ),
        );
    }
    if let Some(readback) = detail.get("clipboard_readback") {
        inline["clipboard_readback"] = json!({"status":readback["status"],"boundary":readback["boundary"],"sequence":readback["sequence"]});
    }
    logger.data("spellcheck_detail", inline);
    if bus.observe_trials {
        bus.send(UiEvent::Trial(detail.clone()));
    }
    if let Some(evidence) = evidence {
        evidence.finish(
            store.clone(),
            logger.clone(),
            detail,
            clipboard::foreground(),
        );
    }
    result
}

pub struct Replay {
    responses: std::collections::HashMap<String, Vec<u8>>,
}
impl Replay {
    pub fn new(fixtures: &[crate::benchmark::Fixture]) -> Result<Self, String> {
        let mut responses = std::collections::HashMap::new();
        for f in fixtures {
            let protected = Protection::new(&f.input)?;
            let key = format!(
                "instructions: {}\ntext input: {}",
                crate::core::PROMPT,
                protected.text
            )
            .replace("\r\n", "\n");
            if responses.insert(key,serde_json::to_vec(&json!({"output":[{"type":"message","content":[{"type":"output_text","text":f.raw_ai_output}]}],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}})).map_err(|e|e.to_string())?).is_some(){return Err("Ambiguous replay fixture".into());}
        }
        Ok(Self { responses })
    }
    fn request(&self, payload: &Value) -> Result<crate::http::Response, String> {
        let started = Instant::now();
        let bytes = serde_json::to_vec(payload).map_err(|e| e.to_string())?;
        let parsed: Value = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
        let key = parsed["input"][0]["content"][0]["text"]
            .as_str()
            .ok_or("Invalid replay payload")?
            .replace("\r\n", "\n");
        let body = serde_json::from_slice(
            self.responses
                .get(&key)
                .ok_or("Replay request does not match frozen input")?,
        )
        .map_err(|e| e.to_string())?;
        Ok(crate::http::Response {
            raw: serde_json::to_string(&body).unwrap_or_default(),
            body,
            status: 200,
            send_ms: 0.,
            wait_ms: 0.,
            download_ms: 0.,
            total_ms: ms(started),
            attempts: 1,
        })
    }
}
