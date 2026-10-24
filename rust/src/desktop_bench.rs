use crate::{
    benchmark::{Case, Fixture, hash, option, summarize},
    channel, clipboard,
    core::{PROMPT, Settings},
    logging::Logger,
    runtime::{Bus, Replay, UiEvent, Worker},
    store::Store,
};
use native_windows_gui as nwg;
use serde_json::{Value, json};
use std::{
    cell::RefCell,
    collections::BTreeMap,
    fs,
    io::Write,
    path::PathBuf,
    rc::Rc,
    sync::{Arc, Mutex, mpsc},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::*,
    UI::{Input::KeyboardAndMouse::*, Shell::*, WindowsAndMessaging::*},
};
struct Paste {
    at: Instant,
    text: String,
    clipboard: clipboard::Clipboard,
    trace: Value,
}
struct Editor {
    fixture: Mutex<Option<Case>>,
    completion: Mutex<Option<Result<Paste, String>>>,
    parent: usize,
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
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let case = state
                .fixture
                .lock()
                .unwrap_or_else(|p| p.into_inner())
                .clone()
                .ok_or("No source fixture")?;
            clipboard::write(state.parent as _, &case.text, &case.html, None, false).map(|_| ())
        }));
        if !matches!(result, Ok(Ok(()))) {
            *state.completion.lock().unwrap_or_else(|p| p.into_inner()) =
                Some(Err("Native source fixture copy failed".into()));
            unsafe {
                PostMessageW(state.parent as _, WM_APP + 22, 0, 0);
            }
        }
        return 0;
    }
    let is_paste = message == WM_PASTE || (message == WM_CHAR && w == 22);
    let mut trace = if is_paste {
        unsafe {
            use windows_sys::Win32::System::DataExchange::*;
            let mut a = 0u32;
            let mut b = 0u32;
            SendMessageW(
                hwnd,
                0xb0,
                (&mut a as *mut u32) as usize,
                (&mut b as *mut u32) as isize,
            );
            let holder = GetOpenClipboardWindow();
            let mut holder_pid = 0;
            GetWindowThreadProcessId(holder, &mut holder_pid);
            json!({"message":message,"selection_start":a,"selection_end":b,"holder":holder as usize,"holder_pid":holder_pid,"ctrl_state":GetKeyState(VK_CONTROL as i32),"focused":GetFocus()==hwnd,"sequence_before_default":GetClipboardSequenceNumber()})
        }
    } else {
        Value::Null
    };
    if is_paste {
        unsafe {
            SetLastError(0);
        }
    }
    let result = unsafe { DefSubclassProc(hwnd, message, w, l) };
    if is_paste {
        trace["default_error"] = json!(unsafe { GetLastError() });
    }
    if message == WM_PASTE || (message == WM_CHAR && w == 22) {
        let at = Instant::now();
        let copied = (|| -> Result<Paste, String> {
            unsafe {
                let length = GetWindowTextLengthW(hwnd);
                let mut text = vec![0u16; length as usize + 1];
                let read = GetWindowTextW(hwnd, text.as_mut_ptr(), text.len() as i32);
                let text = String::from_utf16_lossy(&text[..read as usize]);
                let clipboard = clipboard::read(state.parent as _)?;
                Ok(Paste {
                    at,
                    text,
                    clipboard,
                    trace,
                })
            }
        })();
        *state.completion.lock().unwrap_or_else(|p| p.into_inner()) = Some(copied);
        unsafe {
            PostMessageW(state.parent as _, WM_APP + 22, 0, 0);
        }
    }
    result
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
    fixtures: Vec<Fixture>,
    case: usize,
    index: isize,
    runs: usize,
    warmup: usize,
    start: Option<Instant>,
    dispatched: Option<Instant>,
    detail: Option<Value>,
    evidence_done: bool,
    developer: bool,
    live: bool,
    raw: fs::File,
    trials: Vec<Value>,
    output: PathBuf,
    error: Option<String>,
    prepare_started: Option<Instant>,
    setup_timeout: Duration,
    deadline: Instant,
}
impl Run {
    fn timer(&self, ms: u32) {
        unsafe {
            SetTimer(self.window.handle.hwnd().unwrap().cast(), 2, ms, None);
        }
    }
    fn prepare(&mut self) {
        if self.case >= self.cases.len() {
            nwg::stop_thread_dispatch();
            return;
        }
        if self.prepare_started.is_none() {
            self.prepare_started = Some(Instant::now());
            self.editor.set_text(&self.cases[self.case].text);
            *self
                .shared
                .fixture
                .lock()
                .unwrap_or_else(|p| p.into_inner()) = Some(self.cases[self.case].clone());
            *self
                .shared
                .completion
                .lock()
                .unwrap_or_else(|p| p.into_inner()) = None;
        }
        let parent = self.window.handle.hwnd().unwrap();
        let child = self.editor.handle.hwnd().unwrap();
        unsafe {
            ShowWindow(parent.cast(), SW_RESTORE);
            SetForegroundWindow(parent.cast());
            SetFocus(child.cast());
            SendMessageW(child.cast(), 0xb1, 0, -1);
        }
        let mut a = 0u32;
        let mut b = 0u32;
        unsafe {
            SendMessageW(
                child.cast(),
                0xb0,
                (&mut a as *mut u32) as usize,
                (&mut b as *mut u32) as isize,
            );
        }
        let foreground = unsafe { GetForegroundWindow() };
        let focus = unsafe { GetFocus() };
        let editor_text = self.editor.text();
        let editor_chars = editor_text.encode_utf16().count();
        let text_matches =
            editor_text.replace("\r\n", "\n") == self.cases[self.case].text.replace("\r\n", "\n");
        let ready = foreground == parent.cast()
            && focus == child.cast()
            && a == 0
            && b as usize == editor_chars
            && text_matches;
        if !ready {
            if self.prepare_started.unwrap().elapsed() > self.setup_timeout {
                self.logger.data("benchmark_setup_failed", json!({
                    "case_id": self.cases[self.case].id,
                    "foreground_matches": foreground == parent.cast(),
                    "focus_matches": focus == child.cast(),
                    "selection_start": a, "selection_end": b,
                    "editor_utf16_chars": editor_chars,
                    "expected_utf16_chars": self.cases[self.case].text.encode_utf16().count(),
                    "text_matches": text_matches,
                    "expected_window": parent as usize, "foreground_window": foreground as usize,
                    "expected_editor": child as usize, "focused_window": focus as usize
                }));
                self.abort("Foreground/selection could not be verified; no hotkey posted.");
            } else {
                self.timer(40);
            }
            return;
        }
        self.start = Some(Instant::now());
        self.dispatched = None;
        self.detail = None;
        self.evidence_done = !self.developer;
        self.deadline = Instant::now() + Duration::from_secs(90);
        self.prepare_started = None;
        unsafe {
            PostMessageW(parent.cast(), WM_HOTKEY, 1, 0);
        }
        self.timer(100);
    }
    fn abort(&mut self, error: &str) {
        if self.start.take().is_some() && self.case < self.cases.len() {
            let trial = json!({"case_id":self.cases[self.case].id,"index":self.index,"warmup":self.index<0,"success":false,"quality_match":false,"timings":{},"output":"","error":error});
            let _ = writeln!(self.raw, "{trial}");
            let _ = self.raw.flush();
            self.trials.push(trial);
            let _ = self.write_summary();
        }
        self.error = Some(error.into());
        self.logger.event(format!(
            "benchmark_safety_abort reason={}",
            serde_json::to_string(error).unwrap_or_default()
        ));
        nwg::stop_thread_dispatch();
    }
    fn events(&mut self) {
        while let Ok(event) = self.events.try_recv() {
            match event {
                UiEvent::Trial(detail) => {
                    if detail["developer_evidence"]["status"] != "pending" {
                        self.evidence_done = true;
                    }
                    self.detail = Some(detail);
                }
                UiEvent::Log(name, data)
                    if name == "developer_evidence_completed"
                        && self
                            .detail
                            .as_ref()
                            .is_some_and(|d| d["run_id"] == data["run_id"]) =>
                {
                    self.evidence_done = true;
                }
                _ => {}
            }
        }
        self.finish_if_ready();
    }
    fn finish_if_ready(&mut self) {
        let Some(detail) = self.detail.as_ref() else {
            return;
        };
        if !self.evidence_done {
            return;
        }
        let failed = detail["status"] != "success";
        let completion = self
            .shared
            .completion
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .take();
        if !failed && completion.is_none() {
            return;
        }
        let started = self.start.unwrap();
        let case = &self.cases[self.case];
        let mut timings = detail["timings"].as_object().cloned().unwrap_or_default();
        timings.remove("capture");
        timings.remove("paste");
        // External total exists only when the editor actually acknowledges a paste.
        timings.remove("total_ms");
        timings.insert("request_ms".into(), detail["timings"]["request_ms"].clone());
        timings.insert(
            "capture_ms".into(),
            detail["timings"]["clipboard_ms"].clone(),
        );
        timings.insert("paste_ms".into(), detail["timings"]["paste_ms"].clone());
        timings.insert(
            "coordinator_total_ms".into(),
            detail["timings"]["total_ms"].clone(),
        );
        timings.insert(
            "hotkey_dispatch_ms".into(),
            self.dispatched
                .map(|t| json!(t.duration_since(started).as_secs_f64() * 1000.))
                .unwrap_or(Value::Null),
        );
        let requested = detail["paste_text"].as_str().unwrap_or("");
        let requested_html = detail["paste_html"].as_str().unwrap_or("");
        let mut observed_clipboard = Value::Null;
        let mut output = String::new();
        let mut error = None;
        let mut success = !failed;
        match completion {
            Some(Ok(paste)) => {
                observed_clipboard = json!({"text":paste.clipboard.text,"html":paste.clipboard.html,"sequence":paste.clipboard.sequence,"paste_entry":paste.trace,"editor_text_at_finish":self.editor.text()});
                output = paste.text;
                timings.insert(
                    "total_ms".into(),
                    json!(paste.at.duration_since(started).as_secs_f64() * 1000.),
                );
                if output.replace("\r\n", "\n") != requested.replace("\r\n", "\n")
                    || paste.clipboard.text.replace("\r\n", "\n") != requested.replace("\r\n", "\n")
                    || paste.clipboard.html != requested_html
                {
                    success = false;
                    error = Some("Editor text or requested CF_HTML mismatch".to_owned());
                }
            }
            Some(Err(e)) => {
                success = false;
                error = Some(e);
            }
            None => {
                success = false;
                error = Some(
                    detail["error_message"]
                        .as_str()
                        .unwrap_or("No paste receipt")
                        .into(),
                );
            }
        }
        if !self.live && success {
            let expected = self.fixtures.iter().find(|f| f.id == case.id).unwrap();
            if requested != expected.paste_text
                || requested_html != expected.paste_html
                || detail["output_text"] != expected.output
            {
                success = false;
                error = Some("Frozen replay mapping differs from reference".into());
            }
        }
        if self.live && success && !self.fixtures.iter().any(|f| f.id == case.id) {
            self.fixtures.push(Fixture {
                id: case.id.clone(),
                input: case.text.clone(),
                raw_ai_output: detail["raw_ai_output"].as_str().unwrap_or("").into(),
                output: detail["output_text"].as_str().unwrap_or("").into(),
                paste_text: requested.into(),
                paste_html: requested_html.into(),
            });
            if let Err(error) = fs::write(
                self.output.join("fixtures.json"),
                serde_json::to_vec_pretty(&self.fixtures).unwrap_or_default(),
            ) {
                self.abort(&format!("Cannot preserve live fixtures: {error}"));
                return;
            }
        }
        let trial = json!({"case_id":case.id,"index":self.index,"warmup":self.index<0,"success":success,"quality_match":output.replace("\r\n","\n")==case.expected.replace("\r\n","\n"),"timings":timings,"coordinator_timings":detail["timings"],"timing_schema_version":detail["timing_schema_version"],"timing_boundary":detail["timing_boundary"],"output":output,"error":error,"run_id":detail["run_id"],"observed_clipboard":observed_clipboard});
        if let Err(error) = writeln!(self.raw, "{trial}").and_then(|_| self.raw.flush()) {
            self.abort(&format!("Cannot preserve benchmark trial: {error}"));
            return;
        }
        self.trials.push(trial);
        self.start = None;
        if let Err(error) = self.write_summary() {
            self.abort(&format!("Cannot preserve benchmark summary: {error}"));
            return;
        }
        if !success
            && (!failed
                || unsafe { GetForegroundWindow() } != self.window.handle.hwnd().unwrap().cast())
        {
            self.abort("Unsafe fixture, mapping, or foreground failure; block stopped.");
            return;
        }
        self.index += 1;
        if self.index >= self.runs as isize {
            self.case += 1;
            self.index = -(self.warmup as isize);
        }
        self.start = None;
        self.detail = None;
        self.dispatched = None;
        self.prepare_started = None;
        self.timer(150);
    }
    fn write_summary(&self) -> Result<(), String> {
        let measured: Vec<_> = self
            .trials
            .iter()
            .filter(|t| t["warmup"] == false)
            .collect();
        let warm: Vec<_> = self.trials.iter().filter(|t| t["warmup"] == true).collect();
        let groups: BTreeMap<_, _> = self
            .cases
            .iter()
            .map(|c| {
                (
                    &c.id,
                    summarize(
                        &measured
                            .iter()
                            .copied()
                            .filter(|t| t["case_id"] == c.id)
                            .collect::<Vec<_>>(),
                    ),
                )
            })
            .collect();
        let summary = json!({"aggregate":summarize(&measured),"warmup":summarize(&warm),"cases":groups,"block_error":self.error});
        fs::write(
            self.output.join("summary.json"),
            serde_json::to_vec_pretty(&summary).unwrap(),
        )
        .map_err(|e| e.to_string())
    }
}
pub fn run(args: &[String]) -> Result<(), String> {
    if cfg!(debug_assertions) {
        return Err("Desktop benchmarks require a Release build.".into());
    }
    if !args.iter().any(|s| s == "--allow-desktop") {
        return Err(
            "Desktop benchmark requires --allow-desktop and an explicitly idle computer.".into(),
        );
    }
    let mode = option(args, "--mode").ok_or("--mode local or e2e required")?;
    if mode != "local" && mode != "e2e" {
        return Err("Invalid desktop mode".into());
    }
    let benchmark_vk = option(args, "--benchmark-vk")
        .map(|value| value.parse::<u32>().map_err(|_| "Invalid --benchmark-vk"))
        .transpose()?
        .unwrap_or(channel::BENCH_VK);
    if !(1..=254).contains(&benchmark_vk) {
        return Err("--benchmark-vk must be a Windows virtual-key code from 1 to 254".into());
    }
    let setup_ms = option(args, "--setup-timeout-ms")
        .map(|value| {
            value
                .parse::<u64>()
                .map_err(|_| "Invalid --setup-timeout-ms")
        })
        .transpose()?
        .unwrap_or(3000);
    if !(3000..=120000).contains(&setup_ms) {
        return Err("--setup-timeout-ms must be between 3000 and 120000".into());
    }
    let live = mode == "e2e";
    let output = PathBuf::from(option(args, "--output").ok_or("--output required")?);
    if output.exists()
        && fs::read_dir(&output)
            .map_err(|e| e.to_string())?
            .next()
            .is_some()
    {
        return Err("Output must be empty".into());
    }
    let cases_path = option(args, "--cases").ok_or("--cases required")?;
    let case_bytes = fs::read(&cases_path).map_err(|e| e.to_string())?;
    let cases: Vec<Case> = serde_json::from_slice(&case_bytes).map_err(|e| e.to_string())?;
    if cases.is_empty()
        || cases
            .iter()
            .any(|c| c.id.trim().is_empty() || c.text.trim().is_empty())
        || cases
            .iter()
            .map(|c| &c.id)
            .collect::<std::collections::HashSet<_>>()
            .len()
            != cases.len()
    {
        return Err("No cases".into());
    }
    let runs = option(args, "--runs")
        .unwrap_or("10".into())
        .parse::<usize>()
        .map_err(|_| "Invalid runs")?;
    let warmup = option(args, "--warmup")
        .unwrap_or("2".into())
        .parse::<usize>()
        .map_err(|_| "Invalid warmup")?;
    if runs == 0 || runs > 1000 || warmup > 100 {
        return Err("Invalid run count".into());
    }
    let replacements_path =
        PathBuf::from(option(args, "--replacements").ok_or("--replacements required")?);
    let replacements_bytes = fs::read(&replacements_path).map_err(|e| e.to_string())?;
    let fixture_path = option(args, "--fixtures");
    let fixture_bytes = fixture_path
        .as_ref()
        .map(fs::read)
        .transpose()
        .map_err(|e| e.to_string())?;
    let fixtures: Vec<Fixture> = fixture_bytes
        .as_ref()
        .map(|b| serde_json::from_slice(b))
        .transpose()
        .map_err(|e| e.to_string())?
        .unwrap_or_default();
    if !live {
        for c in &cases {
            if fixtures
                .iter()
                .filter(|f| f.id == c.id && f.input == c.text)
                .count()
                != 1
            {
                return Err(format!("Missing or mismatched fixture {}", c.id));
            }
        }
    }
    let developer = args.iter().any(|s| s == "--developer-logging");
    fs::create_dir_all(&output).map_err(|e| e.to_string())?;
    let mut store = if live {
        let directory = PathBuf::from(
            option(args, "--settings").ok_or("Live mode requires explicit --settings directory")?,
        );
        Store {
            directory: directory.clone(),
            log_directory: output.clone(),
            replacements_path: replacements_path.clone(),
        }
    } else {
        Store::isolated(output.join("offline-settings"))
    };
    store.log_directory = if developer {
        PathBuf::from(option(args,"--evidence-log-dir").ok_or("Developer logging requires an explicit --evidence-log-dir to match the C# store condition")?)
    } else {
        output.clone()
    };
    store.replacements_path = replacements_path;
    let mut settings = if live {
        store.load_settings()?
    } else {
        Settings::default()
    };
    let saved_developer = settings.developer_logging;
    settings.developer_logging = developer;
    if let Some(model) = option(args, "--model") {
        settings.model = model;
    }
    if !matches!(settings.model.as_str(), "gpt-4.1" | "gpt-5.4-mini") {
        return Err("Unsupported model".into());
    }
    if live {
        let keys = store.load_keys()?;
        let key = keys.active().ok_or("No saved API key")?;
        if key == "offline-replay-no-network" {
            return Err("Offline placeholder rejected before network access".into());
        }
    }
    let git = |args: &[&str]| {
        std::process::Command::new("git")
            .args(args)
            .output()
            .ok()
            .filter(|o| o.status.success())
            .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_owned())
    };
    let manifest = json!({"worktree_commit":git(&["rev-parse","HEAD"]),"dirty":git(&["status","--porcelain"]),"created_utc":time::OffsetDateTime::now_utc().to_string(),"timing_schema_version":1,"schema_version":2,"implementation":"rust","options":{"mode":mode,"runs":runs,"warmup":warmup,"developer_logging":developer},"model":settings.model,"prompt":PROMPT,"developer_logging":developer,"saved_developer_logging":saved_developer,"machine_name":std::env::var("COMPUTERNAME").unwrap_or_default(),"case_ids":cases.iter().map(|c|&c.id).collect::<Vec<_>>(),"cases_sha256":hash(&case_bytes),"fixture_sha256":fixture_bytes.as_ref().map(|b|hash(b)),"replacements_sha256":hash(&replacements_bytes),"product_binary_sha256":hash(&fs::read(std::env::current_exe().map_err(|e|e.to_string())?).map_err(|e|e.to_string())?),"hotkey_trigger":"posted_registered_message","benchmark_vk":benchmark_vk,"setup_timeout_ms":setup_ms,"notes":"Registered Ctrl+Alt+benchmark_vk; real Rust coordinator/capture/rich mapper/native clipboard/SendInput; native edit procedure completion verifies exact text and requested CF_HTML. No physical-key delay, browser rendering, tray or dashboard."});
    fs::write(
        output.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .map_err(|e| e.to_string())?;
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
    let (tx, rx) = mpsc::channel();
    let bus = Bus {
        sender: tx,
        notice: notice.sender(),
        observe_trials: true,
    };
    let logger = Logger::observed(output.clone(), Some(bus.clone()));
    let hwnd = window.handle.hwnd().unwrap();
    let replay = if live {
        None
    } else {
        Some(Arc::new(Replay::new(&fixtures)?))
    };
    let worker = Worker::configured(
        store,
        logger.clone(),
        bus,
        hwnd as usize,
        replay,
        Some(settings),
    )?;
    let shared = Arc::new(Editor {
        fixture: Mutex::new(None),
        completion: Mutex::new(None),
        parent: hwnd as usize,
    });
    let pointer = Arc::as_ptr(&shared) as usize;
    unsafe {
        if SetWindowSubclass(
            editor.handle.hwnd().unwrap().cast(),
            Some(editor_proc),
            1,
            pointer,
        ) == 0
        {
            return Err(clipboard::error());
        }
        if RegisterHotKey(
            hwnd.cast(),
            1,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            benchmark_vk,
        ) == 0
        {
            return Err(clipboard::error());
        }
    }
    let raw = fs::File::create(output.join("trials.jsonl")).map_err(|e| e.to_string())?;
    let state = Rc::new(RefCell::new(Run {
        window,
        editor,
        notice,
        worker,
        logger: logger.clone(),
        events: rx,
        shared,
        cases,
        fixtures,
        case: 0,
        index: -(warmup as isize),
        runs,
        warmup,
        start: None,
        dispatched: None,
        detail: None,
        evidence_done: false,
        developer,
        live,
        raw,
        trials: Vec::new(),
        output,
        error: None,
        prepare_started: None,
        setup_timeout: Duration::from_millis(setup_ms),
        deadline: Instant::now(),
    }));
    let weak = Rc::downgrade(&state);
    let handler =
        nwg::full_bind_event_handler(&state.borrow().window.handle, move |event, _, handle| {
            let Some(state) = weak.upgrade() else {
                return;
            };
            let Ok(mut state) = state.try_borrow_mut() else {
                return;
            };
            if event == nwg::Event::OnWindowClose {
                state.abort("Benchmark window closed");
            }
            if event == nwg::Event::OnNotice && handle == state.notice.handle {
                state.events();
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
                    if state.start.is_none() {
                        state.abort("Unexpected hotkey outside a prepared trial");
                    } else {
                        state.dispatched = Some(Instant::now());
                        state.worker.trigger(&state.logger);
                    }
                    Some(0)
                }
                WM_TIMER if w == 2 => {
                    unsafe {
                        KillTimer(state.window.handle.hwnd().unwrap().cast(), 2);
                    }
                    if state.start.is_none() {
                        state.prepare();
                    } else {
                        if state.dispatched.is_none()
                            && state.start.unwrap().elapsed() > Duration::from_secs(2)
                        {
                            state.abort("Hotkey dispatch missing");
                        } else if Instant::now() > state.deadline {
                            state.abort("Coordinator/paste/evidence timed out");
                        } else {
                            state.events();
                            if state.start.is_some() {
                                state.timer(100);
                            }
                        }
                    }
                    Some(0)
                }
                m if m == WM_APP + 22 => {
                    state.finish_if_ready();
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
        KillTimer(hwnd.cast(), 2);
        RemoveWindowSubclass(
            state.editor.handle.hwnd().unwrap().cast(),
            Some(editor_proc),
            1,
        );
    }
    nwg::unbind_event_handler(&handler);
    nwg::unbind_raw_event_handler(&raw).map_err(|e| e.to_string())?;
    logger.flush();
    state.write_summary()?;
    if let Some(error) = &state.error {
        return Err(error.clone());
    }
    if state.trials.iter().any(|t| t["success"] != true) {
        return Err("Desktop benchmark retained failed trials".into());
    }
    Ok(())
}
