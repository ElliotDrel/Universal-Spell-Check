use crate::{
    clipboard,
    core::{Protection, Replacements, extract_output, payload},
    http::Client,
    logging::Logger,
    rich,
    store::{Store, new_id},
    timing::{LocalTimings, measure},
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
    #[cfg(feature = "delivery-experiments")]
    completion: Option<Arc<std::sync::Mutex<Option<thread::JoinHandle<()>>>>>,
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
        #[cfg(feature = "delivery-experiments")]
        let delivery_variant = crate::delivery::Variant::from_environment()?;
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
        let _correction = thread::Builder::new()
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
                let mut replacements = {
                    #[cfg(feature = "delivery-experiments")]
                    if delivery_variant == crate::delivery::Variant::CachedReady {
                        Replacements::default()
                    } else {
                        load_replacements(&store, &logger)
                    }
                    #[cfg(not(feature = "delivery-experiments"))]
                    {
                        load_replacements(&store, &logger)
                    }
                };
                #[cfg(feature = "delivery-experiments")]
                let mut replacement_cache = crate::delivery::ReplacementCache::default();
                #[cfg(feature = "delivery-experiments")]
                if delivery_variant == crate::delivery::Variant::CachedReady {
                    refresh_cached(&mut replacement_cache, &store, &logger);
                }
                #[cfg(feature = "delivery-experiments")]
                let finalizer = if delivery_variant == crate::delivery::Variant::OwnedWorker {
                    match crate::delivery::Finalizer::new() {
                        Ok(worker) => Some(worker),
                        Err(error) => {
                            logger
                                .event(format!("diagnostic_finalizer_unavailable error={error:?}"));
                            None
                        }
                    }
                } else {
                    None
                };
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
                                #[cfg(feature = "delivery-experiments")]
                                if delivery_variant == crate::delivery::Variant::CachedReady {
                                    refresh_cached(&mut replacement_cache, &store, &logger);
                                }
                                #[cfg(feature = "delivery-experiments")]
                                if delivery_variant != crate::delivery::Variant::CachedReady {
                                    replacements = load_replacements(&store, &logger);
                                }
                                #[cfg(not(feature = "delivery-experiments"))]
                                {
                                    replacements = load_replacements(&store, &logger);
                                }
                            }
                            Command::Run(started) => {
                                #[cfg(feature = "delivery-experiments")]
                                let mut delivery_run_id = None;
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
                                                replacements: {
                                                    #[cfg(feature = "delivery-experiments")]
                                                    if delivery_variant
                                                        == crate::delivery::Variant::CachedReady
                                                    {
                                                        &replacement_cache.parsed
                                                    } else {
                                                        &replacements
                                                    }
                                                    #[cfg(not(feature = "delivery-experiments"))]
                                                    {
                                                        &replacements
                                                    }
                                                },
                                                #[cfg(feature = "delivery-experiments")]
                                                delivery_variant,
                                                #[cfg(feature = "delivery-experiments")]
                                                delivery_run_id: &mut delivery_run_id,
                                                #[cfg(feature = "delivery-experiments")]
                                                finalizer: finalizer.as_ref(),
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
                                #[cfg(feature = "delivery-experiments")]
                                if delivery_variant != crate::delivery::Variant::Baseline {
                                    if delivery_variant == crate::delivery::Variant::CachedReady {
                                        refresh_cached(&mut replacement_cache, &store, &logger);
                                    } else {
                                        replacements = load_replacements(&store, &logger);
                                    }
                                }
                                worker_busy.store(false, Ordering::Release);
                                #[cfg(feature = "delivery-experiments")]
                                if delivery_variant != crate::delivery::Variant::Baseline {
                                    logger.event(format!(
                                        "delivery_worker_ready run_id={} variant={} elapsed_ms={:.3}",
                                        delivery_run_id.as_deref().unwrap_or("unavailable"),
                                        delivery_variant.name(),
                                        ms(started)
                                    ));
                                }
                                bus.send(UiEvent::Phase(""));
                                bus.send(UiEvent::Finished);
                                #[cfg(feature = "delivery-experiments")]
                                if delivery_variant == crate::delivery::Variant::Baseline {
                                    replacements = load_replacements(&store, &logger);
                                }
                                #[cfg(not(feature = "delivery-experiments"))]
                                {
                                    replacements = load_replacements(&store, &logger);
                                }
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
                #[cfg(feature = "delivery-experiments")]
                if delivery_variant == crate::delivery::Variant::OwnedWorker {
                    drop(finalizer);
                    logger.event("diagnostic_finalizer_shutdown status=drained");
                }
            })
            .map_err(|e| e.to_string())?;
        ready_rx
            .recv_timeout(Duration::from_secs(5))
            .map_err(|_| "Correction worker did not become ready within five seconds")??;
        Ok(Self {
            #[cfg(feature = "delivery-experiments")]
            completion: if delivery_variant == crate::delivery::Variant::OwnedWorker {
                Some(Arc::new(std::sync::Mutex::new(Some(_correction))))
            } else {
                None
            },
            commands: tx,
            event,
            busy,
        })
    }
    pub fn trigger(&self, logger: &Logger) {
        let started = Instant::now();
        if self
            .busy
            .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
            .is_err()
        {
            logger.event("guard_rejected reason=already_running");
            return;
        }
        if self.commands.send(Command::Run(started)).is_err() {
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
        #[cfg(feature = "delivery-experiments")]
        if let Some(completion) = &self.completion {
            let join = completion.lock().unwrap_or_else(|e| e.into_inner()).take();
            if let Some(join) = join {
                let _ = join.join();
            }
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
#[cfg(feature = "delivery-experiments")]
fn refresh_cached(cache: &mut crate::delivery::ReplacementCache, store: &Store, logger: &Logger) {
    if cache
        .refresh(std::fs::read(&store.replacements_path))
        .is_err()
    {
        logger.event("replacements_unavailable");
    }
}
fn ms(start: Instant) -> f64 {
    start.elapsed().as_secs_f64() * 1000.
}
struct RunContext<'a> {
    #[cfg(feature = "delivery-experiments")]
    delivery_run_id: &'a mut Option<String>,
    #[cfg(feature = "delivery-experiments")]
    delivery_variant: crate::delivery::Variant,
    #[cfg(feature = "delivery-experiments")]
    finalizer: Option<&'a crate::delivery::Finalizer>,
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
        #[cfg(feature = "delivery-experiments")]
        delivery_run_id,
        #[cfg(feature = "delivery-experiments")]
        delivery_variant,
        #[cfg(feature = "delivery-experiments")]
        finalizer,
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
    #[cfg(feature = "delivery-experiments")]
    let lean = delivery_variant.lean(settings.developer_logging, bus.observe_trials);
    #[cfg(not(feature = "delivery-experiments"))]
    let lean = false;
    let mut timings = LocalTimings {
        dispatch_ms: Some(ms(started)),
        ..Default::default()
    };
    let (id, target, evidence) = measure(&mut timings.setup_ms, || {
        let id = new_id();
        let target = clipboard::foreground();
        let evidence = if settings.developer_logging {
            crate::evidence::Evidence::begin(target.clone())
        } else {
            None
        };
        (id, target, evidence)
    });
    #[cfg(feature = "delivery-experiments")]
    if delivery_variant != crate::delivery::Variant::Baseline {
        *delivery_run_id = Some(id.clone());
    }
    let mut request_phases = None;
    let mut recovery_ms = None;
    let mut backup = None;
    let mut detail = json!({"run_id":id,"status":"run_failed","model":settings.model,"developer_logging_enabled":settings.developer_logging,"input_text":"","output_text":"","paste_text":"","paste_html":"","events":["run_started"]});
    if lean {
        detail["diagnostics_mode"] = json!("lean");
        detail["diagnostics_omitted"] = json!([
            "clipboard_html",
            "clipboard_rtf",
            "clipboard_formats",
            "request_payload",
            "request_native",
            "raw_response",
            "raw_ai_output",
            "rich_text.text",
            "rich_text.html",
            "paste_html"
        ]);
    }
    let mut corrected_on_clipboard = false;
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let key = measure(&mut timings.setup_ms, || -> Result<_, String> {
        let key = key
            .filter(|s| !s.trim().is_empty())
            .ok_or("Enter an OpenAI API key in Settings.")?;
        let key = Zeroizing::new(key.to_owned());
        if client.is_none()&&replay.is_none(){return Err("Windows HTTP client unavailable; selection was left unchanged.".into());}
        Ok(key)
        })?;
        backup=Some(measure(&mut timings.backup_ms, || clipboard::Backup::capture(owner as _))?);
        bus.send(UiEvent::Phase("Copying text..."));
        let captured = measure(&mut timings.clipboard_ms, || {
            #[cfg(feature = "delivery-experiments")]
            if lean { clipboard::capture_lean(listener as _, &mut timings.capture) } else { clipboard::capture(listener as _, &mut timings.capture) }
            #[cfg(not(feature = "delivery-experiments"))]
            { clipboard::capture(listener as _, &mut timings.capture) }
        })?;
        measure(&mut timings.diagnostics_ms, || {
        detail["input_text"] = json!(captured.text);
        detail["input_chars"] = json!(captured.text.encode_utf16().count());
        if !lean { detail["clipboard_html"] = json!(captured.html); }
        else { detail["diagnostics_mode"] = json!("lean"); detail["source_html_present"] = json!(!captured.html.is_empty()); }
        if settings.developer_logging{use base64::Engine;detail["clipboard_chromium_mime_base64"]=json!(base64::engine::general_purpose::STANDARD.encode(&captured.native));}
        detail["clipboard_rtf"] = json!(captured.rtf);
        detail["clipboard_formats"] = json!(captured.formats);
        detail["clipboard_html_status"] = json!(captured.html_status);
        detail["clipboard_rtf_status"] = json!(captured.rtf_status);
        detail["clipboard_chromium_mime_status"] = json!(
            if settings.developer_logging || captured.native_status != "ok" {
                captured.native_status
            } else {
                "disabled"
            }
        );
        });
        let history = measure(&mut timings.history_exclude_ms, || clipboard::write(owner as _, &captured.text, "", None, true)).is_ok();
        detail["captured_text_history_excluded"] = json!(history);
        let (protection, request_payload) = measure(&mut timings.prepare_ms, || -> Result<_, String> {
        let input = crate::terminal::normalize(&captured.text, &target.2);
        let protection = Protection::new(&input)?;
        let request_payload = payload(&protection.text, &settings.model);
        Ok((protection, request_payload))
        })?;
        measure(&mut timings.diagnostics_ms, || {
        if !lean { detail["request_payload"] = json!(serde_json::to_string(&request_payload).unwrap_or_default()); }
        });
        bus.send(UiEvent::Phase("Sending to AI..."));
        let response=measure(&mut timings.request_ms, || match replay{Some(replay)=>replay.request(&request_payload),None=>client.ok_or("HTTP client unavailable")?.request(&request_payload,&key,||bus.send(UiEvent::Phase("Waiting for AI...")))})?;
        // Retain existing transport labels; these are not isolated upload/server phases.
        if replay.is_none() { request_phases = Some((response.send_ms, response.wait_ms, response.download_ms)); }
        measure(&mut timings.diagnostics_ms, || {
        if !lean { detail["request_native"] = json!({"final_response_attempt":response.attempts,"native":response.native,"send_api_ms":response.send_api_ms,"send_completion_wait_ms":response.send_completion_wait_ms}); }
        detail["status_code"] = json!(response.status);
        detail["request_attempts"] = json!(response.attempts);
        if !lean { detail["raw_response"] = json!(response.raw); }
        let usage=&response.body["usage"];
        detail["tokens"]=json!({"input":usage["input_tokens"],"output":usage["output_tokens"],"total":usage["total_tokens"],"cached":usage["input_tokens_details"]["cached_tokens"],"reasoning":usage["output_tokens_details"]["reasoning_tokens"]});
        });
        if !(200..300).contains(&response.status) {
            detail["status"] = json!("request_failed");
            return Err(response.body["error"]["message"]
                .as_str()
                .unwrap_or("OpenAI request failed.")
                .to_owned());
        }
        let raw = extract_output(&response.body)?;
        if !lean { detail["raw_ai_output"] = json!(raw); }
        let (output, applied, leak, echo) = measure(&mut timings.replacements_ms, || replacements.process(&raw, &protection))?;
        measure(&mut timings.diagnostics_ms, || {
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
        });
        let mapped = measure(&mut timings.rich_mapping_ms, || rich::rewrite(&captured.html, &captured.text, &output, &captured.native));
        measure(&mut timings.diagnostics_ms, || {
        if lean {
            detail["rich_text"] = json!({"mode":mapped.mode,"attempted":mapped.attempted,"applied":mapped.applied,"reason":mapped.reason,"paragraph_count":mapped.paragraph_count,"link_status":mapped.link_status,"native_docs_status":mapped.native_docs_status,"source_html_present":!captured.html.is_empty()});
        } else { detail["rich_text"] = serde_json::to_value(&mapped).unwrap_or_default(); }
        #[cfg(feature = "delivery-experiments")]
        if lean { crate::delivery::record_bounded(&mut detail, "paste_text", &mapped.text); }
        else { detail["paste_text"] = json!(mapped.text); }
        #[cfg(not(feature = "delivery-experiments"))]
        { detail["paste_text"] = json!(mapped.text); }
        if settings.developer_logging{use base64::Engine;detail["paste_chromium_mime_base64"]=json!(mapped.native.as_ref().map(|b|base64::engine::general_purpose::STANDARD.encode(b)).unwrap_or_default());}
        if !lean { detail["paste_html"] = json!(mapped.html); } else { detail.as_object_mut().unwrap().remove("paste_html"); }
        if !lean {
            detail["paste_text_chars"] = json!(mapped.text.encode_utf16().count());
            detail["paste_html_chars"] = json!(mapped.html.encode_utf16().count());
        }
        });
        bus.send(UiEvent::Phase("Pasting..."));
        let sequence = measure(&mut timings.paste_ms, || -> Result<_, String> {
        let sequence = measure(&mut timings.paste.write_ms, || clipboard::write(
            owner as _,
            &mapped.text,
            &mapped.html,
            mapped.native.as_deref(),
            false,
        ))?;
        corrected_on_clipboard = true;
        if let Some(backup)=backup.as_mut(){backup.disarm();}
        if settings.developer_logging {
            measure(&mut timings.paste.readback_ms, || {
            detail["clipboard_readback"] = crate::evidence::readback_before_input(owner, sequence);
            });
        }
        // CloseClipboard publishes data, but live consumers still need the established handoff period.
        measure(&mut timings.paste.settle_ms, clipboard::settle_publication);
        measure(&mut timings.paste.modifiers_ms, || clipboard::modifiers_released(Duration::from_millis(1200)))?;
        measure(&mut timings.paste.input_ms, || clipboard::chord(0x56))?;
        Ok(sequence)
        })?;
        detail["status"] = json!("success");
        detail["corrected_text_on_clipboard"] = json!(true);
        detail["clipboard_write_sequence"] = json!(sequence);
        Ok(())
    })).unwrap_or_else(|_|Err("The correction stopped safely after an internal error.".into()));
    let total_ms = ms(started);
    if let Err(error) = &result {
        detail["error_message"] = json!(error);
        detail["corrected_text_on_clipboard"] = json!(corrected_on_clipboard);
        if !corrected_on_clipboard {
            detail["original_clipboard_restored"] = json!(measure(&mut recovery_ms, || backup
                .as_mut()
                .is_some_and(|b| b.restore())));
        }
        bus.send(UiEvent::Notify("Spell check failed".into(), error.clone()));
        if key.is_none() {
            bus.send(UiEvent::Settings);
        }
    }
    #[cfg(feature = "delivery-experiments")]
    let finalize_started = Instant::now();
    let mut measured = serde_json::to_value(&timings).unwrap_or_default();
    measured["total_ms"] = json!(total_ms);
    measured["unattributed_ms"] = json!(total_ms - timings.accounted_ms());
    measured["recovery_ms"] = json!(recovery_ms);
    measured["api_ms"] = if replay.is_none() {
        json!(timings.request_ms)
    } else {
        Value::Null
    };
    measured["request_send_ms"] = json!(request_phases.map(|p| p.0));
    measured["request_wait_ms"] = json!(request_phases.map(|p| p.1));
    measured["response_download_ms"] = json!(request_phases.map(|p| p.2));
    detail["timings"] = measured;
    detail["timing_schema_version"] = json!(1);
    detail["timing_boundary"] = json!(if result.is_ok() {
        "accepted_trigger_to_input_return"
    } else {
        "accepted_trigger_to_failure_before_recovery"
    });
    detail["request_mode"] = json!(if replay.is_some() {
        "offline_replay"
    } else {
        "live"
    });
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
    #[cfg(feature = "delivery-experiments")]
    if delivery_variant != crate::delivery::Variant::Baseline {
        detail["delivery_variant"] = json!(delivery_variant.name());
    }
    #[cfg(feature = "delivery-experiments")]
    if matches!(
        delivery_variant,
        crate::delivery::Variant::Lean | crate::delivery::Variant::OwnedWorker
    ) {
        if lean {
            // No evidence or trial consumer exists: transfer the detail rather than clone it.
            logger.data("spellcheck_detail", crate::delivery::compact_owned(detail));
            logger.event(format!(
                "diagnostic_finalize_completed run_id={id} status=ok elapsed_ms={:.3}",
                ms(finalize_started)
            ));
            return result;
        }
        detail["delivery_variant"] = json!(delivery_variant.name());
        let prepared = evidence
            .map(|e| e.prepare_finish(store.clone(), logger.clone(), clipboard::foreground()));
        let bytes = crate::delivery::owned_bytes(&detail);
        let logger = logger.clone();
        let bus = bus.clone();
        let fallback_logger = logger.clone();
        let failure_logger = logger.clone();
        let failure_id = id.clone();
        let job: crate::delivery::Job = Box::new(move || {
            let completed = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                logger.data(
                    "spellcheck_detail",
                    if delivery_variant == crate::delivery::Variant::Lean {
                        crate::delivery::compact(&detail)
                    } else {
                        crate::delivery::baseline_inline(&detail)
                    },
                );
                match (bus.observe_trials, prepared) {
                    (true, Some(finish)) => {
                        bus.send(UiEvent::Trial(detail.clone()));
                        finish(detail);
                    }
                    (true, None) => bus.send(UiEvent::Trial(detail)),
                    (false, Some(finish)) => finish(detail),
                    (false, None) => {}
                }
            }));
            failure_logger.event(format!(
                "diagnostic_finalize_completed run_id={failure_id} status={} elapsed_ms={:.3}",
                if completed.is_ok() { "ok" } else { "panic" },
                ms(finalize_started)
            ));
        });
        if delivery_variant == crate::delivery::Variant::OwnedWorker {
            match finalizer {
                Some(worker) => {
                    if let Err((reason, job)) = worker.submit(bytes, job) {
                        fallback_logger
                            .event(format!("diagnostic_finalizer_fallback reason={reason}"));
                        job();
                    }
                }
                None => {
                    fallback_logger.event("diagnostic_finalizer_fallback reason=unavailable");
                    job();
                }
            }
        } else {
            job();
        }
        return result;
    }
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
    #[cfg(feature = "delivery-experiments")]
    if delivery_variant == crate::delivery::Variant::CachedReady {
        logger.event(format!(
            "diagnostic_finalize_completed run_id={id} status=ok elapsed_ms={:.3}",
            ms(finalize_started)
        ));
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
            native: Value::Null,
            raw: serde_json::to_string(&body).unwrap_or_default(),
            body,
            status: 200,
            send_ms: 0.,
            send_api_ms: None,
            send_completion_wait_ms: None,
            wait_ms: 0.,
            download_ms: 0.,
            total_ms: ms(started),
            attempts: 1,
        })
    }
}
