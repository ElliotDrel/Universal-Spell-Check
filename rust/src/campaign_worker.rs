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
static EXPECTED: std::sync::OnceLock<std::sync::Mutex<String>> = std::sync::OnceLock::new();
static DESTINATION: std::sync::OnceLock<std::sync::Mutex<(usize, u32)>> =
    std::sync::OnceLock::new();
pub fn verify_destination() -> Result<(), String> {
    if let Some(destination) = DESTINATION.get() {
        let expected = destination
            .lock()
            .map_err(|_| "Fixture destination guard poisoned")?;
        let hwnd = unsafe { GetForegroundWindow() };
        let mut pid = 0;
        unsafe {
            GetWindowThreadProcessId(hwnd, &mut pid);
        }
        if hwnd as usize != expected.0 || pid != expected.1 {
            return Err("Disposable editor lost native foreground identity; paste blocked".into());
        }
    }
    Ok(())
}
pub fn verify_capture(text: &str) -> Result<(), String> {
    if let Some(expected) = EXPECTED.get()
        && text.replace("\r\n", "\n")
            != expected
                .lock()
                .map_err(|_| "Fixture guard poisoned")?
                .replace("\r\n", "\n")
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
    if !matches!(settings.model.as_str(), "gpt-4.1" | "gpt-5.4-mini") {
        return Err("Unsupported saved model; campaign will not change models".into());
    }
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
                    if crate::store::atomic_write(&target.join(format!("detail-{id}.json")), detail.to_string().as_bytes()).is_err() { nwg::stop_thread_dispatch(); return Some(0); }
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
                        let hwnd=unsafe {GetForegroundWindow()};let mut pid=0;
                        unsafe {GetWindowThreadProcessId(hwnd,&mut pid);}
                        let title=String::from_utf16_lossy(&title[..length as usize]);
                        if !matches!(title.as_str(),"USC disposable editor"|"USC disposable editor - Google Chrome") || command["expected_hwnd"].as_u64()!=Some(hwnd as usize as u64) || command["expected_pid"].as_u64()!=Some(pid as u64) || pid==0 {
                            let _=fs::write(target.join(format!("detail-{id}.json")),json!({"status":"run_failed","error_message":"Foreground is not disposable fixture"}).to_string());
                            *state.borrow_mut()=(id,false);
                            return Some(0);
                        }
                        let Some(input)=command["expected_input"].as_str().filter(|s|!s.is_empty()) else {
                            let _=crate::store::atomic_write(&target.join(format!("detail-{id}.json")),b"{\"status\":\"run_failed\",\"error_message\":\"Missing authorized synthetic input\"}");
                            *state.borrow_mut()=(id,false);return Some(0);
                        };
                        let Ok(mut expected)=EXPECTED.get_or_init(||std::sync::Mutex::new(String::new())).lock() else {nwg::stop_thread_dispatch();return Some(0);};
                        *expected=input.to_owned();drop(expected);
                        let Ok(mut destination)=DESTINATION.get_or_init(||std::sync::Mutex::new((0,0))).lock() else {nwg::stop_thread_dispatch();return Some(0);};
                        *destination=(hwnd as usize,pid);drop(destination);
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn campaign_capture_guard_refuses_unexpected_text() {
        assert!(verify_capture("ordinary non-campaign text").is_ok());
        assert!(
            EXPECTED
                .set(std::sync::Mutex::new(
                    "Synthetic fixture\r\nsecond line".into()
                ))
                .is_ok()
        );
        assert!(verify_capture("Synthetic fixture\nsecond line").is_ok());
        assert!(verify_capture("Different selection").is_err());
    }
}
