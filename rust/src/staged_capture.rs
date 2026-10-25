//! Opt-in experiments. Only the desktop benchmark configures the selector.
use crate::{
    clipboard::{self, Clipboard},
    core::{Protection, payload},
    rich::PreparedSource,
    timing::{CaptureTimings, LocalTimings, measure},
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    io::{Read, Write},
    sync::{Arc, Mutex, OnceLock, mpsc},
    thread,
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Variant {
    UnicodeFirst,
    OwnedPrepare,
    Helper,
}
impl Variant {
    pub fn parse(name: &str) -> Result<Self, String> {
        match name {
            "unicode-first" => Ok(Self::UnicodeFirst),
            "owned-prepare" => Ok(Self::OwnedPrepare),
            "helper" => Ok(Self::Helper),
            _ => Err("Expected unicode-first, owned-prepare, or helper".into()),
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::UnicodeFirst => "unicode-first",
            Self::OwnedPrepare => "owned-prepare",
            Self::Helper => "helper",
        }
    }
}
static ACTIVE: OnceLock<Variant> = OnceLock::new();
pub fn configure(name: &str) -> Result<(), String> {
    ACTIVE
        .set(Variant::parse(name)?)
        .map_err(|_| "Staged capture already configured".into())
}
pub fn active() -> Option<Variant> {
    ACTIVE.get().copied()
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct Binding {
    pub run_id: String,
    pub sequence: u32,
    pub owner: usize,
    pub text: String,
}
#[derive(Clone)]
struct Trace {
    start: Instant,
    events: Arc<Mutex<Vec<Value>>>,
}
impl Trace {
    fn new() -> Self {
        Self {
            start: Instant::now(),
            events: Arc::new(Mutex::new(Vec::new())),
        }
    }
    fn mark(&self, name: &str) {
        self.events
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(json!({"event":name,"ms":self.start.elapsed().as_secs_f64()*1000.}));
    }
    fn value(&self) -> Value {
        json!(
            self.events
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .clone()
        )
    }
}
pub struct Completed<T> {
    pub captured: Clipboard,
    pub protection: Protection,
    pub request_payload: Value,
    pub response: T,
    pub prepared: Option<PreparedSource>,
    pub request_ms: f64,
    pub trace: Value,
}
// All callers use this two-gate coordinator, including failure-injection tests.
fn execute<T: Send, F: FnOnce(&Value) -> Result<T, String> + Send>(
    variant: Variant,
    run_id: &str,
    model: &str,
    target: &str,
    capture: impl FnOnce(
        &mut dyn FnMut(Binding) -> Result<(), String>,
    ) -> Result<(Clipboard, Binding), String>,
    after_capture: impl FnOnce(&Clipboard) -> Result<(), String>,
    request: F,
) -> Result<Completed<T>, String> {
    let trace = Trace::new();
    thread::scope(|scope| {
        let mut request = Some(request);
        let mut pending = None;
        let mut bound = None;
        let mut prepared_request = None;
        let mut dispatch = |binding: Binding| -> Result<(), String> {
            if binding.run_id != run_id || binding.text.trim().is_empty() {
                return Err("Invalid TextReady binding".into());
            }
            if bound.is_some() {
                return Err("Duplicate TextReady; request was not repeated".into());
            }
            trace.mark("text_ready");
            bound = Some(binding.clone());
            if variant == Variant::OwnedPrepare {
                return Ok(());
            }
            let protection = Protection::new(&crate::terminal::normalize(&binding.text, target))?;
            let request_payload = payload(&protection.text, model);
            let owned = request_payload.clone();
            let call = request.take().ok_or("Request already dispatched")?;
            let worker_trace = trace.clone();
            pending = Some(scope.spawn(move || {
                let start = Instant::now();
                worker_trace.mark("request_started");
                let response =
                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| call(&owned)))
                        .unwrap_or_else(|_| Err("Staged request panicked".into()));
                worker_trace.mark("response_ready");
                (response, start.elapsed().as_secs_f64() * 1000.)
            }));
            prepared_request = Some((protection, request_payload));
            Ok(())
        };
        let captured =
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| capture(&mut dispatch)))
                .unwrap_or_else(|_| Err("Staged capture panicked".into()));
        drop(dispatch);
        let (captured, binding) = match captured {
            Ok(v) => v,
            Err(error) => {
                if let Some(p) = pending.take() {
                    let _ = p.join();
                }
                return Err(error);
            }
        };
        if bound.as_ref() != Some(&binding)
            || binding.run_id != run_id
            || captured.sequence != binding.sequence
            || captured.text != binding.text
        {
            if let Some(p) = pending.take() {
                let _ = p.join();
            }
            return Err("SnapshotReady does not match TextReady; response discarded".into());
        }
        if [captured.html_status, captured.native_status]
            .iter()
            .any(|s| !matches!(*s, "ok" | "absent"))
        {
            if let Some(p) = pending.take() {
                let _ = p.join();
            }
            return Err("Functional rich snapshot unavailable; response discarded".into());
        }
        trace.mark("snapshot_ready");
        after_capture(&captured)?;
        trace.mark("history_exclusion_finished");
        if variant == Variant::OwnedPrepare {
            let protection = Protection::new(&crate::terminal::normalize(&binding.text, target))?;
            let request_payload = payload(&protection.text, model);
            let owned = request_payload.clone();
            let call = request.take().ok_or("Request already dispatched")?;
            let worker_trace = trace.clone();
            pending = Some(scope.spawn(move || {
                let start = Instant::now();
                worker_trace.mark("request_started");
                let response =
                    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| call(&owned)))
                        .unwrap_or_else(|_| Err("Staged request panicked".into()));
                worker_trace.mark("response_ready");
                (response, start.elapsed().as_secs_f64() * 1000.)
            }));
            prepared_request = Some((protection, request_payload));
        }
        let prepared = if variant == Variant::OwnedPrepare {
            Some(PreparedSource::new(&captured.html, &captured.text))
        } else {
            None
        };
        trace.mark("rich_source_ready");
        let (response, request_ms) = pending
            .ok_or("No request dispatched")?
            .join()
            .map_err(|_| "Staged request worker panicked")?;
        let response = response?;
        trace.mark("both_gates_ready");
        let (protection, request_payload) = prepared_request.ok_or("Missing protected request")?;
        Ok(Completed {
            captured,
            protection,
            request_payload,
            response,
            prepared,
            request_ms,
            trace: trace.value(),
        })
    })
}

pub struct NativeInput<'a> {
    pub variant: Variant,
    pub run_id: &'a str,
    pub model: &'a str,
    pub target: &'a str,
    pub listener: usize,
    pub owner: usize,
    pub diagnostics: bool,
}
pub fn native<T: Send, F: FnOnce(&Value) -> Result<T, String> + Send>(
    input: NativeInput<'_>,
    timings: &mut LocalTimings,
    history: &mut bool,
    request: F,
) -> Result<Completed<T>, String> {
    let NativeInput {
        variant,
        run_id,
        model,
        target,
        listener,
        owner,
        diagnostics,
    } = input;
    execute(
        variant,
        run_id,
        model,
        target,
        |ready| {
            measure(&mut timings.clipboard_ms, || {
                if variant == Variant::Helper {
                    helper_capture(run_id, diagnostics, ready)
                } else {
                    let mut binding = None;
                    let captured = clipboard::capture_staged(
                        listener as _,
                        &mut timings.capture,
                        diagnostics,
                        &mut |text, sequence, owner| {
                            let b = Binding {
                                run_id: run_id.into(),
                                sequence,
                                owner,
                                text: text.into(),
                            };
                            binding = Some(b.clone());
                            ready(b)
                        },
                    )?;
                    Ok((
                        captured,
                        binding.ok_or("Capture completed without TextReady")?,
                    ))
                }
            })
        },
        |captured| {
            *history = measure(&mut timings.history_exclude_ms, || {
                clipboard::write(owner as _, &captured.text, "", None, true)
            })
            .is_ok();
            Ok(())
        },
        request,
    )
}

// Frames contain owned values only. JSON is length framed before allocation.
const FRAME_LIMIT: usize = 64 * 1024 * 1024;
#[derive(Serialize, Deserialize)]
struct WireClipboard {
    text: String,
    html: String,
    rtf: String,
    native: Vec<u8>,
    formats: String,
    sequence: u32,
    html_status: String,
    rtf_status: String,
    native_status: String,
}
impl From<Clipboard> for WireClipboard {
    fn from(c: Clipboard) -> Self {
        Self {
            text: c.text,
            html: c.html,
            rtf: c.rtf,
            native: c.native,
            formats: c.formats,
            sequence: c.sequence,
            html_status: c.html_status.into(),
            rtf_status: c.rtf_status.into(),
            native_status: c.native_status.into(),
        }
    }
}
impl WireClipboard {
    fn owned(self) -> Result<Clipboard, String> {
        fn status(s: &str) -> Result<&'static str, String> {
            match s {
                "ok" => Ok("ok"),
                "absent" => Ok("absent"),
                "disabled" => Ok("disabled"),
                "too_large" => Ok("too_large"),
                "read_failed" => Ok("read_failed"),
                _ => Err("Invalid clipboard status".into()),
            }
        }
        Ok(Clipboard {
            html_status: status(&self.html_status)?,
            rtf_status: status(&self.rtf_status)?,
            native_status: status(&self.native_status)?,
            text: self.text,
            html: self.html,
            rtf: self.rtf,
            native: self.native,
            formats: self.formats,
            sequence: self.sequence,
        })
    }
}
#[derive(Serialize, Deserialize)]
#[serde(tag = "kind")]
enum Frame {
    Start {
        run_id: String,
        diagnostics: bool,
        fixture: Option<Fixture>,
    },
    TextReady {
        binding: Binding,
    },
    SnapshotReady {
        binding: Binding,
        data: WireClipboard,
    },
    Failed {
        error: String,
    },
}
fn send_frame(writer: &mut impl Write, frame: &Frame) -> Result<(), String> {
    let data = serde_json::to_vec(frame).map_err(|e| e.to_string())?;
    if data.len() > FRAME_LIMIT {
        return Err("IPC frame exceeds size limit".into());
    }
    writer
        .write_all(&(data.len() as u32).to_le_bytes())
        .and_then(|_| writer.write_all(&data))
        .and_then(|_| writer.flush())
        .map_err(|e| e.to_string())
}
fn read_frame(reader: &mut impl Read) -> Result<Frame, String> {
    let mut header = [0u8; 4];
    reader.read_exact(&mut header).map_err(|e| e.to_string())?;
    let len = u32::from_le_bytes(header) as usize;
    if len == 0 || len > FRAME_LIMIT {
        return Err("IPC frame exceeds size limit".into());
    }
    let mut data = vec![0; len];
    reader.read_exact(&mut data).map_err(|e| e.to_string())?;
    serde_json::from_slice(&data).map_err(|e| e.to_string())
}
#[derive(Clone, Serialize, Deserialize)]
struct Fixture {
    text: String,
    html: String,
    #[serde(default)]
    native: Vec<u8>,
    #[serde(default)]
    rich_delay_ms: u64,
    #[serde(default)]
    fail_rich: bool,
    #[serde(default)]
    stale: bool,
    #[serde(default)]
    panic_capture: bool,
    #[serde(default)]
    helper_hang_ms: u64,
}
impl Fixture {
    fn capture(
        &self,
        run_id: &str,
        ready: &mut dyn FnMut(Binding) -> Result<(), String>,
    ) -> Result<(Clipboard, Binding), String> {
        let binding = Binding {
            run_id: run_id.into(),
            sequence: 7,
            owner: 11,
            text: self.text.clone(),
        };
        ready(binding.clone())?;
        thread::sleep(Duration::from_millis(self.rich_delay_ms.min(2000)));
        if self.panic_capture {
            panic!("Injected capture panic");
        }
        if self.fail_rich {
            return Err("Injected delayed rich failure".into());
        }
        let mut final_binding = binding;
        if self.stale {
            final_binding.sequence += 1;
        }
        Ok((
            Clipboard {
                text: self.text.clone(),
                html: self.html.clone(),
                native: self.native.clone(),
                sequence: final_binding.sequence,
                html_status: if self.html.is_empty() { "absent" } else { "ok" },
                native_status: if self.native.is_empty() {
                    "absent"
                } else {
                    "ok"
                },
                rtf_status: "disabled",
                ..Default::default()
            },
            final_binding,
        ))
    }
}

pub fn helper_main() -> Result<(), String> {
    let Frame::Start {
        run_id,
        diagnostics,
        fixture,
    } = read_frame(&mut std::io::stdin().lock())?
    else {
        return Err("Helper requires Start frame".into());
    };
    if let Some(fixture) = &fixture {
        thread::sleep(Duration::from_millis(fixture.helper_hang_ms.min(6000)));
    }
    let mut output = std::io::stdout().lock();
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        let mut ready = |binding| send_frame(&mut output, &Frame::TextReady { binding });
        if let Some(fixture) = fixture {
            fixture.capture(&run_id, &mut ready)
        } else {
            clipboard::initialize_sta()?;
            let window = clipboard::WorkerWindow::new()?;
            let mut binding = None;
            let data = clipboard::capture_staged(
                window.0,
                &mut CaptureTimings::default(),
                diagnostics,
                &mut |text, sequence, owner| {
                    let b = Binding {
                        run_id: run_id.clone(),
                        sequence,
                        owner,
                        text: text.into(),
                    };
                    binding = Some(b.clone());
                    ready(b)
                },
            )?;
            Ok((data, binding.ok_or("Missing TextReady")?))
        }
    }))
    .unwrap_or_else(|_| Err("Capture helper panicked".into()));
    match result {
        Ok((data, binding)) => send_frame(
            &mut output,
            &Frame::SnapshotReady {
                binding,
                data: data.into(),
            },
        ),
        Err(error) => send_frame(&mut output, &Frame::Failed { error }),
    }
}
static HELPER_PID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
static HELPER_REAPED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
struct Job(windows_sys::Win32::Foundation::HANDLE);
impl Drop for Job {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}
struct Helper {
    child: std::process::Child,
    job: Option<Job>,
    reader: Option<thread::JoinHandle<()>>,
    frames: mpsc::Receiver<Result<Frame, String>>,
    deadline: Instant,
}
impl Drop for Helper {
    fn drop(&mut self) {
        drop(self.job.take());
        let _ = self.child.kill();
        HELPER_REAPED.store(
            self.child.wait().is_ok(),
            std::sync::atomic::Ordering::SeqCst,
        );
        // Closing the child ends blocking ReadFile before joining the pipe drainer.
        if let Some(reader) = self.reader.take() {
            let _ = reader.join();
        }
    }
}
impl Helper {
    fn spawn(run_id: &str, diagnostics: bool, fixture: Option<Fixture>) -> Result<Self, String> {
        use std::os::windows::{io::AsRawHandle, process::CommandExt};
        use windows_sys::Win32::System::{JobObjects::*, Threading::CREATE_NO_WINDOW};
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let job = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
        if job.is_null() {
            return Err(clipboard::error());
        }
        let job = Job(job);
        let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = unsafe { std::mem::zeroed() };
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if unsafe {
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                std::mem::size_of_val(&limits) as u32,
            )
        } == 0
        {
            return Err(clipboard::error());
        }
        let child = std::process::Command::new(exe)
            .arg("--staged-capture-helper")
            .creation_flags(CREATE_NO_WINDOW)
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::null())
            .spawn();
        let mut child = match child {
            Ok(child) => child,
            Err(e) => return Err(e.to_string()),
        };
        // Child blocks on Start: it cannot access the clipboard until job assignment succeeds.
        if unsafe { AssignProcessToJobObject(job.0, child.as_raw_handle()) } == 0 {
            let error = clipboard::error();
            let _ = child.kill();
            let _ = child.wait();
            return Err(error);
        }
        HELPER_PID.store(child.id(), std::sync::atomic::Ordering::SeqCst);
        HELPER_REAPED.store(false, std::sync::atomic::Ordering::SeqCst);
        let (tx, frames) = mpsc::sync_channel(3);
        let mut helper = Self {
            child,
            job: Some(job),
            reader: None,
            frames,
            deadline: Instant::now() + Duration::from_secs(4),
        };
        let mut output = helper.child.stdout.take().ok_or("Missing helper pipe")?;
        helper.reader = Some(thread::spawn(move || {
            loop {
                let frame = read_frame(&mut output);
                let done = frame.is_err()
                    || matches!(
                        &frame,
                        Ok(Frame::SnapshotReady { .. } | Frame::Failed { .. })
                    );
                if tx.send(frame).is_err() || done {
                    break;
                }
            }
        }));
        let mut input = helper.child.stdin.take().ok_or("Missing helper input")?;
        // Start is bounded; fixture inputs are a headless test facility, never credentials.
        send_frame(
            &mut input,
            &Frame::Start {
                run_id: run_id.into(),
                diagnostics,
                fixture,
            },
        )?;
        drop(input);
        Ok(helper)
    }
    fn next(&self) -> Result<Frame, String> {
        self.frames
            .recv_timeout(self.deadline.saturating_duration_since(Instant::now()))
            .map_err(|_| "Capture helper deadline or closed pipe")?
    }
}
fn helper_capture(
    run_id: &str,
    diagnostics: bool,
    ready: &mut dyn FnMut(Binding) -> Result<(), String>,
) -> Result<(Clipboard, Binding), String> {
    helper_capture_fixture(run_id, diagnostics, None, ready)
}
fn helper_capture_fixture(
    run_id: &str,
    diagnostics: bool,
    fixture: Option<Fixture>,
    ready: &mut dyn FnMut(Binding) -> Result<(), String>,
) -> Result<(Clipboard, Binding), String> {
    let helper = Helper::spawn(run_id, diagnostics, fixture)?;
    let first = match helper.next()? {
        Frame::TextReady { binding } => binding,
        Frame::Failed { error } => return Err(error),
        _ => return Err("Expected TextReady".into()),
    };
    if first.run_id != run_id {
        return Err("Stale helper run ID".into());
    }
    ready(first.clone())?;
    match helper.next()? {
        Frame::SnapshotReady { binding, data } if binding == first => Ok((data.owned()?, binding)),
        Frame::Failed { error } => Err(error),
        _ => Err("Invalid or stale SnapshotReady".into()),
    }
}

// Headless replay exercises the same coordinator, protected payload, source preparation,
// rich mapper and helper IPC. It never starts the app or accesses settings/credentials.
pub fn replay_cli(args: &[String]) -> Result<(), String> {
    use crate::benchmark::option;
    let variant = Variant::parse(&option(args, "--variant").ok_or("--variant required")?)?;
    let path = option(args, "--fixture").ok_or("--fixture required")?;
    let value: Value = serde_json::from_slice(&std::fs::read(path).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    let fixture: Fixture = serde_json::from_value(value.clone()).map_err(|e| e.to_string())?;
    if fixture.text.len() + fixture.html.len() + fixture.native.len() > 1024 * 1024 {
        return Err("Replay fixture exceeds 1 MiB".into());
    }
    let raw = value["raw_ai_output"]
        .as_str()
        .ok_or("raw_ai_output required")?
        .to_owned();
    let delay = value["request_delay_ms"].as_u64().unwrap_or(0).min(2000);
    let run_id = crate::store::new_id();
    let calls = std::sync::atomic::AtomicUsize::new(0);
    let completed = execute(
        variant,
        &run_id,
        "gpt-4.1",
        "",
        |ready| {
            if variant == Variant::Helper {
                helper_capture_fixture(&run_id, false, Some(fixture.clone()), ready)
            } else {
                fixture.capture(&run_id, ready)
            }
        },
        |_| Ok(()),
        |_| {
            calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            thread::sleep(Duration::from_millis(delay));
            Ok(raw)
        },
    );
    let completed = match completed {
        Ok(completed) => completed,
        Err(error) => {
            println!(
                "{}",
                json!({"variant":variant.name(),"error":error,"request_count":calls.load(std::sync::atomic::Ordering::SeqCst),"helper_pid":HELPER_PID.load(std::sync::atomic::Ordering::SeqCst),"helper_reaped":HELPER_REAPED.load(std::sync::atomic::Ordering::SeqCst)})
            );
            return Err(error);
        }
    };
    let replacements = crate::core::Replacements::default();
    let (output, _, _, _) = replacements.process(&completed.response, &completed.protection)?;
    let mapped = match completed.prepared {
        Some(prepared) => prepared.rewrite(&output, &completed.captured.native),
        None => crate::rich::rewrite(
            &completed.captured.html,
            &completed.captured.text,
            &output,
            &completed.captured.native,
        ),
    };
    let baseline = crate::rich::rewrite(
        &completed.captured.html,
        &completed.captured.text,
        &output,
        &completed.captured.native,
    );
    if serde_json::to_value(&mapped).unwrap() != serde_json::to_value(&baseline).unwrap()
        || mapped.native != baseline.native
    {
        return Err("Rich mapping differs from baseline".into());
    }
    println!(
        "{}",
        json!({"variant":variant.name(),"request_count":calls.load(std::sync::atomic::Ordering::SeqCst),"request_payload":completed.request_payload,"trace":completed.trace,"rich":mapped,"request_ms":completed.request_ms,"helper_pid":HELPER_PID.load(std::sync::atomic::Ordering::SeqCst),"helper_reaped":HELPER_REAPED.load(std::sync::atomic::Ordering::SeqCst),"note":"Component replay only; no clipboard/paste/editor timing claim"})
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    fn fixture() -> Fixture {
        Fixture {
            text: "This is a tset.".into(),
            html: crate::rich::build_cf_html(
                "<p data-pm-slice=\"1 1 []\">This is a <b>tset</b>.</p>",
            ),
            native: Vec::new(),
            rich_delay_ms: 0,
            fail_rich: false,
            stale: false,
            panic_capture: false,
            helper_hang_ms: 0,
        }
    }
    fn event(value: &Value, name: &str) -> usize {
        value
            .as_array()
            .unwrap()
            .iter()
            .position(|v| v["event"] == name)
            .unwrap()
    }
    #[test]
    fn response_first_waits_for_matching_snapshot() {
        let fixture = fixture();
        let (tx, rx) = mpsc::sync_channel(0);
        let completed = execute(
            Variant::UnicodeFirst,
            "run",
            "gpt-4.1",
            "",
            |ready| {
                fixture.capture("run", &mut |b| {
                    ready(b)?;
                    rx.recv_timeout(Duration::from_secs(2))
                        .map_err(|e| e.to_string())?;
                    Ok(())
                })
            },
            |_| Ok(()),
            |_| {
                tx.send(()).unwrap();
                Ok("response")
            },
        )
        .unwrap();
        assert!(
            event(&completed.trace, "request_started") < event(&completed.trace, "snapshot_ready")
        );
        assert!(
            event(&completed.trace, "snapshot_ready") < event(&completed.trace, "both_gates_ready")
        );
    }
    #[test]
    fn rich_first_waits_for_response_and_preserves_payload() {
        let fixture = fixture();
        let (tx, rx) = mpsc::sync_channel(0);
        let completed = execute(
            Variant::UnicodeFirst,
            "run",
            "gpt-5.4-mini",
            "",
            |ready| fixture.capture("run", ready),
            |_| {
                tx.send(()).unwrap();
                Ok(())
            },
            move |p| {
                rx.recv_timeout(Duration::from_secs(2)).unwrap();
                Ok(p.clone())
            },
        )
        .unwrap();
        assert!(
            event(&completed.trace, "snapshot_ready") < event(&completed.trace, "response_ready")
        );
        assert_eq!(
            completed.request_payload,
            payload(
                &Protection::new(&fixture.text).unwrap().text,
                "gpt-5.4-mini"
            )
        );
        assert_eq!(completed.response, completed.request_payload);
    }
    #[test]
    fn owned_prepare_dispatches_after_snapshot_and_exclusion() {
        let fixture = fixture();
        let completed = execute(
            Variant::OwnedPrepare,
            "run",
            "gpt-4.1",
            "",
            |ready| fixture.capture("run", ready),
            |_| Ok(()),
            |_| Ok(()),
        )
        .unwrap();
        assert!(completed.prepared.is_some());
        assert!(
            event(&completed.trace, "history_exclusion_finished")
                < event(&completed.trace, "request_started")
        );
    }
    #[test]
    fn rich_failure_stale_snapshot_and_panic_never_retry_request() {
        for (failure, stale, panic_capture) in [
            (true, false, false),
            (false, true, false),
            (false, false, true),
        ] {
            let mut fixture = fixture();
            fixture.fail_rich = failure;
            fixture.stale = stale;
            fixture.panic_capture = panic_capture;
            let calls = AtomicUsize::new(0);
            let commits = AtomicUsize::new(0);
            let result = execute(
                Variant::UnicodeFirst,
                "run",
                "gpt-4.1",
                "",
                |ready| fixture.capture("run", ready),
                |_| {
                    commits.fetch_add(1, Ordering::SeqCst);
                    Ok(())
                },
                |_| {
                    calls.fetch_add(1, Ordering::SeqCst);
                    Ok(())
                },
            );
            assert!(result.is_err());
            assert_eq!(calls.load(Ordering::SeqCst), 1);
            assert_eq!(commits.load(Ordering::SeqCst), 0);
        }
    }
    #[test]
    fn duplicate_text_does_not_duplicate_request() {
        let fixture = fixture();
        let calls = AtomicUsize::new(0);
        let result = execute(
            Variant::UnicodeFirst,
            "run",
            "gpt-4.1",
            "",
            |ready| {
                fixture.capture("run", &mut |b| {
                    ready(b.clone())?;
                    ready(b)
                })
            },
            |_| Ok(()),
            |_| {
                calls.fetch_add(1, Ordering::SeqCst);
                Ok(())
            },
        );
        assert!(result.is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }
    #[test]
    fn request_panic_and_failed_functional_flavor_cannot_pass_gates() {
        let fixture = fixture();
        let result = execute::<(), _>(
            Variant::UnicodeFirst,
            "run",
            "gpt-4.1",
            "",
            |ready| fixture.capture("run", ready),
            |_| Ok(()),
            |_| panic!("Injected request panic"),
        );
        assert!(result.is_err());
        let result = execute(
            Variant::UnicodeFirst,
            "run",
            "gpt-4.1",
            "",
            |ready| {
                let (mut c, b) = fixture.capture("run", ready)?;
                c.native_status = "read_failed";
                Ok((c, b))
            },
            |_| Err("Must not publish".into()),
            |_| Ok(()),
        );
        assert!(result.is_err());
    }
    #[test]
    fn all_variants_keep_protected_terminal_payload_exact() {
        for variant in [
            Variant::UnicodeFirst,
            Variant::OwnedPrepare,
            Variant::Helper,
        ] {
            for model in ["gpt-4.1", "gpt-5.4-mini"] {
                let mut fixture = fixture();
                fixture.text =
                    "A tset https://example.com/a?x=1 and C:\\USC\\draft.txt\r\n  wrapped".into();
                let expected = payload(
                    &Protection::new(&crate::terminal::normalize(
                        &fixture.text,
                        "WindowsTerminal",
                    ))
                    .unwrap()
                    .text,
                    model,
                );
                let completed = execute(
                    variant,
                    "run",
                    model,
                    "WindowsTerminal",
                    |ready| fixture.capture("run", ready),
                    |_| Ok(()),
                    |p| Ok(p.clone()),
                )
                .unwrap();
                assert_eq!(completed.request_payload, expected);
                assert_eq!(completed.response, expected);
                assert!(!completed.protection.entries.is_empty());
            }
        }
    }
    #[test]
    fn framed_ipc_rejects_oversize_partial_and_stale_data() {
        assert!(read_frame(&mut &((FRAME_LIMIT as u32 + 1).to_le_bytes())[..]).is_err());
        assert!(read_frame(&mut &[1u8, 0, 0, 0][..]).is_err());
        assert!(read_frame(&mut &[1u8, 0][..]).is_err());
        let binding = Binding {
            run_id: "first".into(),
            sequence: 9,
            owner: 12,
            text: "text".into(),
        };
        let mut frame = Vec::new();
        send_frame(
            &mut frame,
            &Frame::TextReady {
                binding: binding.clone(),
            },
        )
        .unwrap();
        assert!(
            matches!(read_frame(&mut frame.as_slice()).unwrap(),Frame::TextReady{binding:b} if b==binding)
        );
    }
    #[test]
    fn source_preparation_preserves_frozen_rich_results() {
        let cases: Vec<crate::benchmark::Case> =
            serde_json::from_str(include_str!("../../bench/baseline-cases.json")).unwrap();
        for case in cases {
            let prepared = PreparedSource::new(&case.html, &case.text).rewrite(&case.expected, &[]);
            let baseline = crate::rich::rewrite(&case.html, &case.text, &case.expected, &[]);
            assert_eq!(
                serde_json::to_value(prepared).unwrap(),
                serde_json::to_value(baseline).unwrap(),
                "{}",
                case.id
            );
        }
        use base64::Engine;
        for line in include_str!("../tests/fixtures/csharp-formatting.jsonl").lines() {
            let case: Value = serde_json::from_str(line).unwrap();
            let html = case["source_html"].as_str().unwrap();
            let source = case["input"].as_str().unwrap();
            let output = case["output"].as_str().unwrap();
            let native = base64::engine::general_purpose::STANDARD
                .decode(case["native_base64"].as_str().unwrap())
                .unwrap();
            let prepared = PreparedSource::new(html, source).rewrite(output, &native);
            let baseline = crate::rich::rewrite(html, source, output, &native);
            assert_eq!(prepared.native, baseline.native);
            assert_eq!(
                serde_json::to_value(prepared).unwrap(),
                serde_json::to_value(baseline).unwrap()
            );
        }
        for html in ["invalid", "", "<p>unverified</p>"] {
            let prepared = PreparedSource::new(html, "original").rewrite("corrected", &[1, 2]);
            let baseline = crate::rich::rewrite(html, "original", "corrected", &[1, 2]);
            assert_eq!(
                serde_json::to_value(prepared).unwrap(),
                serde_json::to_value(baseline).unwrap()
            );
        }
    }
}
