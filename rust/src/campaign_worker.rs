//! Disposable benchmark coordinator; its destination editor lives in another process.
use crate::{
    benchmark::option,
    logging::Logger,
    runtime::{Bus, UiEvent, Worker},
    store::Store,
};
use native_windows_gui as nwg;
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    fs,
    path::{Path, PathBuf},
    rc::Rc,
    sync::mpsc,
};
use windows_sys::Win32::UI::WindowsAndMessaging::*;
static EXPECTED: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);
pub fn verify_capture(text: &str) -> Result<(), String> {
    if let Some(expected) = EXPECTED
        .lock()
        .map_err(|_| "Fixture guard poisoned")?
        .as_ref()
        && text.replace("\r\n", "\n") != expected.replace("\r\n", "\n")
    {
        return Err("Captured selection differs from authorized synthetic fixture; no API request permitted".into());
    }
    Ok(())
}

pub fn run(args: &[String]) -> Result<(), String> {
    if cfg!(debug_assertions) || !args.iter().any(|a| a == "--allow-desktop") {
        return Err("Campaign worker needs Release and --allow-desktop".into());
    }
    let output = PathBuf::from(option(args, "--output").ok_or("--output required")?);
    fs::create_dir(&output).map_err(|e| e.to_string())?;
    crate::request_budget::campaign(Path::new(
        &option(args, "--campaign-ledger").ok_or("--campaign-ledger required")?,
    ))?;
    crate::request_budget::initialize(&output.join("http-reservations.jsonl"), 100)?;
    let store = Store {
        directory: option(args, "--settings")
            .ok_or("--settings required")?
            .into(),
        log_directory: output.join("logs"),
        replacements_path: option(args, "--replacements")
            .ok_or("--replacements required")?
            .into(),
    };
    let mut settings = store.load_settings()?;
    settings.developer_logging = false;
    nwg::init().map_err(|e| e.to_string())?;
    crate::clipboard::initialize_sta()?;
    let mut window = nwg::Window::default();
    nwg::Window::builder()
        .title("USC disposable campaign coordinator")
        .size((0, 0))
        .flags(nwg::WindowFlags::WINDOW)
        .build(&mut window)
        .map_err(|e| e.to_string())?;
    let mut notice = nwg::Notice::default();
    nwg::Notice::builder()
        .parent(&window)
        .build(&mut notice)
        .map_err(|e| e.to_string())?;
    let (tx, rx) = mpsc::channel();
    let bus = Bus {
        sender: tx,
        notice: notice.sender(),
        observe_trials: true,
    };
    let logger = Logger::new(store.log_directory.clone());
    let replay = if let Some(path) = option(args, "--replay-cases") {
        let cases: Vec<crate::benchmark::Case> =
            serde_json::from_slice(&fs::read(path).map_err(|e| e.to_string())?)
                .map_err(|e| e.to_string())?;
        let fixtures = cases
            .iter()
            .map(|c| {
                Ok(crate::benchmark::Fixture {
                    id: c.id.clone(),
                    input: c.text.clone(),
                    raw_ai_output: crate::core::Protection::new(&c.expected)?.text,
                    output: c.expected.clone(),
                    paste_text: c.expected.clone(),
                    paste_html: String::new(),
                })
            })
            .collect::<Result<Vec<_>, String>>()?;
        Some(std::sync::Arc::new(crate::runtime::Replay::new(&fixtures)?))
    } else {
        None
    };
    let worker = Worker::configured(
        store,
        logger.clone(),
        bus,
        window.handle.hwnd().unwrap() as usize,
        replay,
        Some(settings.clone()),
    )?;
    fs::write(
        output.join("ready.json"),
        json!({"pid":std::process::id(),"model":settings.model,"prompt":crate::core::PROMPT})
            .to_string(),
    )
    .map_err(|e| e.to_string())?;
    let active = Rc::new(RefCell::new((0u64, false)));
    let state = active.clone();
    let target = output.clone();
    let worker2 = worker.clone();
    let logger2 = logger.clone();
    let handler = nwg::bind_raw_event_handler(
        &window.handle,
        crate::style::raw_handler_id(940),
        move |_, message, _, _| {
            if message != WM_TIMER {
                return None;
            }
            while let Ok(event) = rx.try_recv() {
                if let UiEvent::Trial(detail) = event {
                    let id = state.borrow().0;
                    let _ = fs::write(target.join(format!("detail-{id}.json")), detail.to_string());
                    state.borrow_mut().1 = false;
                }
            }
            if target.join("stop").exists() {
                nwg::stop_thread_dispatch();
                return Some(0);
            }
            if !state.borrow().1
                && let Ok(raw) = fs::read(target.join("command.json"))
                    && let Ok(command) = serde_json::from_slice::<Value>(&raw) {
                        let id = command["id"].as_u64().unwrap_or(0);
                        if id > state.borrow().0 {
                        let mut title=[0u16;512];
                        let length=unsafe {GetWindowTextW(GetForegroundWindow(),title.as_mut_ptr(),512)};
                        if !String::from_utf16_lossy(&title[..length as usize]).contains("USC disposable editor") {
                            let _=fs::write(target.join(format!("detail-{id}.json")),json!({"status":"run_failed","error_message":"Foreground is not disposable fixture"}).to_string());
                            *state.borrow_mut()=(id,false);
                            return Some(0);
                        }
                        if let Ok(mut expected)=EXPECTED.lock() { *expected=command["expected_input"].as_str().map(str::to_owned); }
                            *state.borrow_mut() = (id, true);
                            let mut ticks = 0i64;
                            let mut frequency = 0i64;
                            unsafe {
                                windows_sys::Win32::System::Performance::QueryPerformanceCounter(
                                    &mut ticks,
                                );
                                windows_sys::Win32::System::Performance::QueryPerformanceFrequency(
                                    &mut frequency,
                                );
                            }
                            let _ = fs::write(
                                target.join(format!("trigger-{id}.json")),
                                json!({"ticks":ticks,"frequency":frequency}).to_string(),
                            );
                            worker2.trigger(&logger2);
                        }
                    }
            Some(0)
        },
    )
    .map_err(|e| e.to_string())?;
    unsafe {
        SetTimer(window.handle.hwnd().unwrap().cast(), 1, 5, None);
    }
    nwg::dispatch_thread_events();
    worker.stop();
    logger.flush();
    let _ = nwg::unbind_raw_event_handler(&handler);
    Ok(())
}
