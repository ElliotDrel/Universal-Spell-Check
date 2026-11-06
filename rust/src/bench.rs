//! `--bench`: drive the real correction path end to end, the way a user does.
//!
//! The harness opens its own editor window, selects a case's text, presses the real
//! Ctrl+Alt+F24 chord with SendInput and lets the production `Worker` copy, correct and paste.
//! The editor answers the copy with the case's text and HTML (like a rich source app) and records the paste.
//! Latency is measured from the chord to the paste landing in the editor. Without `--live` the
//! OpenAI call is replayed from `bench/replay.json`, so runs are free and deterministic; `--live`
//! sends real requests with the channel's saved key. Tray, dashboard and overlay are not drawn.
use crate::{
    channel, clipboard,
    core::Settings,
    logging::Logger,
    runtime::{Bus, Replay, UiEvent, Worker},
    store::Store,
};
use native_windows_gui as nwg;
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    collections::BTreeMap,
    fs,
    path::PathBuf,
    rc::Rc,
    sync::{Arc, Mutex, mpsc},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::*,
    UI::{Input::KeyboardAndMouse::*, Shell::*, WindowsAndMessaging::*},
};

#[derive(Clone, Deserialize)]
pub struct Case {
    pub id: String,
    pub text: String,
    pub expected: String,
    #[serde(default)]
    pub html: String,
}
/// A recorded model answer and the exact paste it must produce.
#[derive(Clone, Deserialize, serde::Serialize)]
pub struct Fixture {
    pub id: String,
    pub input: String,
    pub raw_ai_output: String,
    pub output: String,
    pub paste_text: String,
    pub paste_html: String,
}

const PASTED: u32 = WM_APP + 22;
const TIMER: usize = 2;
struct Paste {
    at: Instant,
    text: String,
    clipboard: clipboard::Clipboard,
}
struct Editor {
    case: Mutex<Option<Case>>,
    paste: Mutex<Option<Result<Paste, String>>>,
    parent: usize,
}
fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|p| p.into_inner())
}
unsafe extern "system" fn editor_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
    _id: usize,
    data: usize,
) -> LRESULT {
    let state = unsafe { &*(data as *const Editor) };
    if message == WM_COPY || (message == WM_CHAR && w == 3) {
        let copied = lock(&state.case)
            .clone()
            .map(|case| clipboard::write(state.parent as _, &case.text, &case.html, None, false));
        if !matches!(copied, Some(Ok(_))) {
            *lock(&state.paste) = Some(Err("Editor could not serve the copy".into()));
            unsafe { PostMessageW(state.parent as _, PASTED, 0, 0) };
        }
        return 0;
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if message == WM_PASTE || (message == WM_CHAR && w == 22) {
        let at = Instant::now();
        let text = unsafe {
            let mut buffer = vec![0u16; GetWindowTextLengthW(hwnd) as usize + 1];
            let read = GetWindowTextW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32);
            String::from_utf16_lossy(&buffer[..read as usize])
        };
        let paste = clipboard::read(state.parent as _).map(|clipboard| Paste {
            at,
            text,
            clipboard,
        });
        *lock(&state.paste) = Some(paste);
        unsafe { PostMessageW(state.parent as _, PASTED, 0, 0) };
    }
    result
}
/// Presses and releases Ctrl+Alt+`vk` like a keyboard would.
fn press_hotkey(vk: u16) -> Result<(), String> {
    let key = |vk, flags| INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk,
                wScan: 0,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    };
    let inputs = [
        key(VK_CONTROL, 0),
        key(VK_MENU, 0),
        key(vk, 0),
        key(vk, KEYEVENTF_KEYUP),
        key(VK_MENU, KEYEVENTF_KEYUP),
        key(VK_CONTROL, KEYEVENTF_KEYUP),
    ];
    let sent = unsafe {
        SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            size_of::<INPUT>() as i32,
        )
    };
    if sent as usize == inputs.len() {
        Ok(())
    } else {
        Err(format!("SendInput blocked: {}", clipboard::error()))
    }
}
fn same(a: &str, b: &str) -> bool {
    a.replace("\r\n", "\n") == b.replace("\r\n", "\n")
}

struct Run {
    window: nwg::Window,
    editor: nwg::TextBox,
    notice: nwg::Notice,
    worker: Worker,
    logger: Logger,
    events: mpsc::Receiver<UiEvent>,
    shared: Arc<Editor>,
    cases: Vec<Case>,
    replay: Vec<Fixture>,
    live: bool,
    case: usize,
    /// Negative while warming up.
    index: isize,
    runs: usize,
    warmup: usize,
    pressed: Option<Instant>,
    hotkey: Option<Instant>,
    detail: Option<Value>,
    trials: Vec<Value>,
    /// Live answers in `bench/replay.json` format, one per case.
    recorded: Vec<Fixture>,
    error: Option<String>,
}
impl Run {
    fn hwnd(&self) -> HWND {
        self.window.handle.hwnd().unwrap().cast()
    }
    fn timer(&self, ms: u32) {
        unsafe { SetTimer(self.hwnd(), TIMER, ms, None) };
    }
    fn stop(&mut self, error: Option<String>) {
        self.error = error;
        nwg::stop_thread_dispatch();
    }
    /// Loads the next case, selects it and presses the hotkey once the editor is focused.
    fn start_trial(&mut self) {
        if self.case >= self.cases.len() {
            return self.stop(None);
        }
        let case = self.cases[self.case].clone();
        self.editor.set_text(&case.text);
        *lock(&self.shared.case) = Some(case.clone());
        *lock(&self.shared.paste) = None;
        let editor: HWND = self.editor.handle.hwnd().unwrap().cast();
        unsafe {
            ShowWindow(self.hwnd(), SW_RESTORE);
            SetForegroundWindow(self.hwnd());
            SetFocus(editor);
            SendMessageW(editor, 0x00B1, 0, -1); // EM_SETSEL: select all
        }
        let ready = unsafe { GetForegroundWindow() == self.hwnd() && GetFocus() == editor }
            && same(&self.editor.text(), &case.text);
        if !ready {
            return self.stop(Some(
                "The bench window could not take focus. Run it on an idle, unlocked desktop."
                    .into(),
            ));
        }
        self.detail = None;
        self.hotkey = None;
        self.pressed = Some(Instant::now());
        if let Err(error) = press_hotkey(channel::BENCH_VK as u16) {
            return self.stop(Some(error));
        }
        self.timer(100);
    }
    fn poll(&mut self) {
        while let Ok(event) = self.events.try_recv() {
            if let UiEvent::Trial(detail) = event {
                self.detail = Some(detail);
            }
        }
        let pressed = self.pressed.unwrap();
        if self.hotkey.is_none() && pressed.elapsed() > Duration::from_secs(2) {
            return self.stop(Some("The hotkey never arrived; focus was lost.".into()));
        }
        if pressed.elapsed() > Duration::from_secs(60) {
            return self.stop(Some("Correction timed out after 60 s.".into()));
        }
        let failed = self
            .detail
            .as_ref()
            .is_some_and(|d| d["status"] != "success");
        let pasted = lock(&self.shared.paste).is_some();
        if self.detail.is_some() && (pasted || failed) {
            self.finish_trial();
        } else {
            self.timer(20);
        }
    }
    fn finish_trial(&mut self) {
        let detail = self.detail.take().unwrap();
        let case = self.cases[self.case].clone();
        let pressed = self.pressed.take().unwrap();
        let mut timings = detail["timings"].as_object().cloned().unwrap_or_default();
        timings.retain(|_, v| v.is_number());
        let wanted_text = detail["paste_text"].as_str().unwrap_or("");
        let wanted_html = detail["paste_html"].as_str().unwrap_or("");
        let mut error = (detail["status"] != "success").then(|| {
            detail["error_message"]
                .as_str()
                .unwrap_or("Failed")
                .to_owned()
        });
        let mut output = String::new();
        match lock(&self.shared.paste).take() {
            Some(Ok(paste)) => {
                output = paste.text;
                timings.insert(
                    "user_ms".into(),
                    json!(paste.at.duration_since(pressed).as_secs_f64() * 1000.),
                );
                if !same(&output, wanted_text)
                    || !same(&paste.clipboard.text, wanted_text)
                    || paste.clipboard.html != wanted_html
                {
                    error.get_or_insert("Pasted text or HTML differs from the correction".into());
                }
            }
            Some(Err(e)) => error = Some(e),
            None => {
                error.get_or_insert("Nothing was pasted".into());
            }
        }
        if !self.live
            && let Some(f) = self.replay.iter().find(|f| f.id == case.id)
            && (wanted_text != f.paste_text
                || wanted_html != f.paste_html
                || detail["output_text"] != f.output.as_str())
        {
            error.get_or_insert("Correction differs from bench/replay.json".into());
        }
        let trial = json!({
            "case_id": case.id, "warmup": self.index < 0, "success": error.is_none(),
            "expected_match": same(&output, &case.expected), "error": error,
            "output": output, "timings": timings,
        });
        if self.live && error.is_none() && !self.recorded.iter().any(|f| f.id == case.id) {
            self.recorded.push(Fixture {
                id: case.id.clone(),
                input: case.text.clone(),
                raw_ai_output: detail["raw_ai_output"].as_str().unwrap_or("").into(),
                output: detail["output_text"].as_str().unwrap_or("").into(),
                paste_text: wanted_text.into(),
                paste_html: wanted_html.into(),
            });
        }
        self.trials.push(trial);
        self.index += 1;
        if self.index >= self.runs as isize {
            self.case += 1;
            self.index = -(self.warmup as isize);
        }
        self.timer(50);
    }
}

fn option(args: &[String], name: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == name).map(|w| w[1].clone())
}
fn count(args: &[String], name: &str, default: usize) -> Result<usize, String> {
    option(args, name).map_or(Ok(default), |v| {
        v.parse().map_err(|_| format!("Invalid {name}"))
    })
}
fn read_json<T: serde::de::DeserializeOwned>(path: &str) -> Result<T, String> {
    let bytes = fs::read(path).map_err(|e| format!("{path}: {e}"))?;
    serde_json::from_slice(bytes.strip_prefix(b"\xEF\xBB\xBF").unwrap_or(&bytes))
        .map_err(|e| format!("{path}: {e}"))
}

pub fn run(args: &[String]) -> Result<(), String> {
    if cfg!(debug_assertions) {
        return Err("Build with --release; debug timings are meaningless.".into());
    }
    let live = args.iter().any(|a| a == "--live");
    let runs = count(args, "--runs", 10)?;
    let warmup = count(args, "--warmup", 2)?;
    let cases: Vec<Case> =
        read_json(&option(args, "--cases").unwrap_or("bench/cases.json".into()))?;
    let replay: Vec<Fixture> =
        read_json(&option(args, "--replay").unwrap_or("bench/replay.json".into()))?;
    let replacements =
        PathBuf::from(option(args, "--replacements").unwrap_or("replacements.json".into()));
    let output = PathBuf::from(option(args, "--output").unwrap_or_else(|| {
        std::env::temp_dir()
            .join(format!("usc-bench-{}", std::process::id()))
            .to_string_lossy()
            .into()
    }));
    let cases: Vec<Case> = match option(args, "--case") {
        Some(id) => cases.into_iter().filter(|c| c.id == id).collect(),
        None => cases,
    };
    if cases.is_empty() {
        return Err("No matching cases".into());
    }
    if !live {
        for case in &cases {
            if !replay
                .iter()
                .any(|f| f.id == case.id && f.input == case.text)
            {
                return Err(format!(
                    "bench/replay.json has no recording for {}",
                    case.id
                ));
            }
        }
    }
    fs::create_dir_all(&output).map_err(|e| e.to_string())?;
    // Bench logs stay out of the shared corpus; settings and keys are read, never written.
    let (store, settings) = if live {
        let mut store = Store::standard()?;
        let settings = store.load_settings()?;
        store.log_directory = output.join("logs");
        (store, settings)
    } else {
        (
            Store::isolated(output.join("settings")),
            Settings::default(),
        )
    };
    let store = Store {
        replacements_path: replacements,
        ..store
    };
    let mut settings = settings;
    settings.developer_logging = false;

    nwg::init().map_err(|e| e.to_string())?;
    clipboard::initialize_sta()?;
    let mut window = nwg::Window::default();
    nwg::Window::builder()
        .title(channel::BENCH_TITLE)
        .size((900, 500))
        .flags(nwg::WindowFlags::MAIN_WINDOW | nwg::WindowFlags::VISIBLE)
        .build(&mut window)
        .map_err(|e| e.to_string())?;
    let mut editor = nwg::TextBox::default();
    nwg::TextBox::builder()
        .parent(&window)
        .position((20, 20))
        .size((850, 425))
        .limit(1024 * 1024)
        .build(&mut editor)
        .map_err(|e| e.to_string())?;
    let mut notice = nwg::Notice::default();
    nwg::Notice::builder()
        .parent(&window)
        .build(&mut notice)
        .map_err(|e| e.to_string())?;
    let (tx, events) = mpsc::channel();
    let bus = Bus {
        sender: tx,
        notice: notice.sender(),
        observe_trials: true,
    };
    let logger = Logger::new(store.log_directory.clone());
    let hwnd = window.handle.hwnd().unwrap();
    let recorded = (!live)
        .then(|| Replay::new(&replay).map(Arc::new))
        .transpose()?;
    let worker = Worker::configured(
        store,
        logger.clone(),
        bus,
        hwnd as usize,
        recorded,
        Some(settings),
    )?;
    let shared = Arc::new(Editor {
        case: Mutex::new(None),
        paste: Mutex::new(None),
        parent: hwnd as usize,
    });
    unsafe {
        let editor = editor.handle.hwnd().unwrap().cast();
        if SetWindowSubclass(editor, Some(editor_proc), 1, Arc::as_ptr(&shared) as usize) == 0 {
            return Err(clipboard::error());
        }
        if RegisterHotKey(
            hwnd.cast(),
            1,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            channel::BENCH_VK,
        ) == 0
        {
            return Err("Ctrl+Alt+F24 is taken; is another bench running?".into());
        }
    }
    let state = Rc::new(RefCell::new(Run {
        window,
        editor,
        notice,
        worker,
        logger: logger.clone(),
        events,
        shared,
        cases,
        replay,
        live,
        case: 0,
        index: -(warmup as isize),
        runs,
        warmup,
        pressed: None,
        hotkey: None,
        detail: None,
        trials: Vec::new(),
        recorded: Vec::new(),
        error: None,
    }));
    let weak = Rc::downgrade(&state);
    let handler = nwg::full_bind_event_handler(&state.borrow().window.handle, move |e, _, h| {
        let Some(state) = weak.upgrade() else { return };
        let Ok(mut state) = state.try_borrow_mut() else {
            return;
        };
        if e == nwg::Event::OnWindowClose {
            state.stop(Some("Bench window closed".into()));
        } else if e == nwg::Event::OnNotice && h == state.notice.handle && state.pressed.is_some() {
            state.poll();
        }
    });
    let weak = Rc::downgrade(&state);
    let raw = nwg::bind_raw_event_handler(
        &state.borrow().window.handle,
        crate::style::raw_handler_id(920),
        move |_, message, w, _| {
            let state = weak.upgrade()?;
            let Ok(mut state) = state.try_borrow_mut() else {
                return None;
            };
            match message {
                WM_HOTKEY if w == 1 => {
                    if state.pressed.is_some() && state.hotkey.is_none() {
                        state.hotkey = Some(Instant::now());
                        state.worker.trigger(&state.logger);
                    }
                    Some(0)
                }
                WM_TIMER if w == TIMER => {
                    unsafe { KillTimer(state.hwnd(), TIMER) };
                    if state.pressed.is_some() {
                        state.poll();
                    } else {
                        state.start_trial();
                    }
                    Some(0)
                }
                PASTED => {
                    if state.pressed.is_some() {
                        state.poll();
                    }
                    Some(0)
                }
                _ => None,
            }
        },
    )
    .map_err(|e| e.to_string())?;
    state.borrow().timer(150);
    nwg::dispatch_thread_events();
    let state = state.borrow_mut();
    state.worker.stop();
    unsafe {
        UnregisterHotKey(hwnd.cast(), 1);
        KillTimer(hwnd.cast(), TIMER);
        RemoveWindowSubclass(
            state.editor.handle.hwnd().unwrap().cast(),
            Some(editor_proc),
            1,
        );
    }
    nwg::unbind_event_handler(&handler);
    nwg::unbind_raw_event_handler(&raw).map_err(|e| e.to_string())?;
    logger.flush();
    if live {
        fs::write(
            output.join("recorded.json"),
            serde_json::to_vec_pretty(&state.recorded).unwrap(),
        )
        .map_err(|e| e.to_string())?;
    }
    let summary = summarize(&state.trials, &state.cases, live, state.error.as_deref());
    fs::write(
        output.join("summary.json"),
        serde_json::to_vec_pretty(&summary).unwrap(),
    )
    .map_err(|e| e.to_string())?;
    println!("{}", report(&summary));
    println!("Full results: {}", output.join("summary.json").display());
    if let Some(error) = &state.error {
        return Err(error.clone());
    }
    if summary["failed"].as_u64() != Some(0) {
        return Err("Some corrections failed; see summary.json".into());
    }
    Ok(())
}

fn median(mut values: Vec<f64>) -> Value {
    if values.is_empty() {
        return Value::Null;
    }
    values.sort_by(f64::total_cmp);
    let mid = values.len() / 2;
    json!(if values.len().is_multiple_of(2) {
        (values[mid - 1] + values[mid]) / 2.
    } else {
        values[mid]
    })
}
fn medians(trials: &[&Value]) -> Value {
    let mut keys = std::collections::BTreeSet::new();
    for t in trials {
        keys.extend(
            t["timings"]
                .as_object()
                .into_iter()
                .flat_map(|m| m.keys().cloned()),
        );
    }
    keys.into_iter()
        .map(|k| {
            let v = trials
                .iter()
                .filter_map(|t| t["timings"][&k].as_f64())
                .collect();
            (k, median(v))
        })
        .collect::<serde_json::Map<_, _>>()
        .into()
}
fn summarize(trials: &[Value], cases: &[Case], live: bool, error: Option<&str>) -> Value {
    let measured: Vec<_> = trials.iter().filter(|t| t["warmup"] == false).collect();
    let ok: Vec<_> = measured
        .iter()
        .copied()
        .filter(|t| t["success"] == true)
        .collect();
    let per_case: BTreeMap<_, _> = cases
        .iter()
        .map(|c| {
            let mine: Vec<_> = ok
                .iter()
                .copied()
                .filter(|t| t["case_id"] == c.id)
                .collect();
            (c.id.clone(), medians(&mine))
        })
        .collect();
    let failures: Vec<_> = trials
        .iter()
        .filter(|t| t["success"] != true)
        .map(|t| json!({"case_id": t["case_id"], "error": t["error"]}))
        .collect();
    json!({
        "mode": if live { "live" } else { "replay" },
        "version": channel::VERSION,
        "measured": measured.len(), "failed": failures.len(), "error": error,
        "median": medians(&ok), "cases": per_case, "failures": failures, "trials": trials,
    })
}
fn report(summary: &Value) -> String {
    let ms = |v: &Value| v.as_f64().map_or("-".into(), |v| format!("{v:.1}"));
    let mut out = format!(
        "{} bench, {} measured, {} failed\n{:<12}{:>10}{:>10}{:>10}{:>10}\n",
        summary["mode"].as_str().unwrap_or(""),
        summary["measured"],
        summary["failed"],
        "case",
        "user_ms",
        "api_ms",
        "copy_ms",
        "paste_ms"
    );
    let mut row = |name: &str, t: &Value| {
        out += &format!(
            "{name:<12}{:>10}{:>10}{:>10}{:>10}\n",
            ms(&t["user_ms"]),
            ms(&t["request_ms"]),
            ms(&t["clipboard_ms"]),
            ms(&t["paste_ms"])
        );
    };
    for (id, t) in summary["cases"].as_object().into_iter().flatten() {
        row(id, t);
    }
    row("MEDIAN", &summary["median"]);
    out
}
#[cfg(test)]
mod tests {
    use super::*;
    /// Request size drives API latency, the bulk of every correction. CI fails a PR that grows
    /// any value above main's. Regenerate with `USC_UPDATE_BUDGET=1 cargo test perf_budget`.
    #[test]
    fn perf_budget_matches_request_bytes() {
        let cases: Vec<Case> =
            serde_json::from_str(include_str!("../../bench/cases.json")).unwrap();
        let model = Settings::default().model;
        let measured: BTreeMap<_, _> = cases
            .iter()
            .map(|c| {
                let payload = crate::core::Protection::new(&c.text)
                    .unwrap()
                    .payload(&model);
                (
                    format!("request_bytes.{}", c.id),
                    serde_json::to_vec(&payload).unwrap().len(),
                )
            })
            .collect();
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../bench/perf-budget.json");
        let expected = serde_json::to_string_pretty(&measured).unwrap() + "\n";
        if std::env::var_os("USC_UPDATE_BUDGET").is_some() {
            fs::write(path, &expected).unwrap();
        }
        let committed = fs::read_to_string(path)
            .unwrap_or_default()
            .replace("\r\n", "\n");
        assert_eq!(
            committed, expected,
            "Run USC_UPDATE_BUDGET=1 cargo test perf_budget"
        );
    }
    #[test]
    fn median_handles_even_odd_and_empty() {
        assert_eq!(median(vec![3., 1., 2.]), json!(2.));
        assert_eq!(median(vec![0., 10.]), json!(5.));
        assert!(median(Vec::new()).is_null());
    }
}
