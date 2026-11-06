use crate::core::Replacements;
use serde_json::{Value, json};
use std::{
    sync::mpsc::{self, SyncSender, TrySendError},
    thread::{self, JoinHandle},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Variant {
    Baseline,
    Lean,
    CachedReady,
    OwnedWorker,
}
impl Variant {
    pub fn from_environment() -> Result<Self, String> {
        Self::parse(
            std::env::var("USC_DELIVERY_VARIANT")
                .ok()
                .as_deref()
                .unwrap_or("baseline"),
        )
    }
    pub fn parse(name: &str) -> Result<Self, String> {
        match name {
            "baseline" => Ok(Self::Baseline),
            "lean" => Ok(Self::Lean),
            "cached-ready" => Ok(Self::CachedReady),
            "owned-worker" => Ok(Self::OwnedWorker),
            _ => Err(format!("Unknown USC_DELIVERY_VARIANT: {name}")),
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::Lean => "lean",
            Self::CachedReady => "cached-ready",
            Self::OwnedWorker => "owned-worker",
        }
    }
    pub fn lean(self, developer: bool, trials: bool) -> bool {
        self == Self::Lean && !developer && !trials
    }
}

// Cache only successful parses. A failed read/parse clears the active rules and cache.
#[derive(Default)]
pub struct ReplacementCache {
    bytes: Option<Vec<u8>>,
    pub parsed: Replacements,
}
impl ReplacementCache {
    pub fn refresh(&mut self, bytes: Result<Vec<u8>, std::io::Error>) -> Result<bool, String> {
        let bytes = match bytes {
            Ok(b) => b,
            Err(e) => {
                self.clear();
                return Err(e.to_string());
            }
        };
        if self.bytes.as_ref() == Some(&bytes) {
            return Ok(false);
        }
        match Replacements::parse(&bytes) {
            Ok(parsed) => {
                self.parsed = parsed;
                self.bytes = Some(bytes);
                Ok(true)
            }
            Err(e) => {
                self.clear();
                Err(e)
            }
        }
    }
    fn clear(&mut self) {
        self.bytes = None;
        self.parsed = Replacements::default();
    }
}

const CLIPPED: &[&str] = &[
    "clipboard_html",
    "clipboard_rtf",
    "paste_text",
    "paste_html",
    "clipboard_chromium_mime_base64",
];
pub const INLINE_UNITS: usize = 512 * 1024;
fn prefix(text: &str, units: usize) -> (&str, usize) {
    let mut count = 0;
    let mut end = 0;
    let mut total = 0;
    for (offset, c) in text.char_indices() {
        total += c.len_utf16();
        if count + c.len_utf16() <= units && end == offset {
            count += c.len_utf16();
            end = offset + c.len_utf8();
        }
    }
    (&text[..end], total)
}
// Build bounded fields from references: never clone full HTML/native base64 and then clip.
pub fn compact(detail: &Value) -> Value {
    let mut out = serde_json::Map::new();
    if let Some(fields) = detail.as_object() {
        for (name, value) in fields {
            if CLIPPED.contains(&name.as_str())
                && let Some(text) = value.as_str()
            {
                let (text, observed_total) = prefix(text, INLINE_UNITS);
                let total = detail
                    .get(format!("{name}_chars"))
                    .and_then(Value::as_u64)
                    .map_or(observed_total, |n| n as usize);
                out.insert(name.clone(), json!(text));
                out.insert(format!("{name}_chars"), json!(total));
                out.insert(format!("{name}_truncated"), json!(total > INLINE_UNITS));
            } else if name == "rich_text" && value.is_object() {
                let mut rich = serde_json::Map::new();
                for (key, value) in value.as_object().unwrap() {
                    if key != "text" && key != "html" {
                        rich.insert(key.clone(), value.clone());
                    }
                }
                rich.insert(
                    "source_html_present".into(),
                    json!(
                        detail["clipboard_html"]
                            .as_str()
                            .is_some_and(|s| !s.is_empty())
                            || detail["source_html_present"] == true
                    ),
                );
                out.insert(name.clone(), Value::Object(rich));
            } else if name == "clipboard_readback" {
                out.insert(name.clone(), json!({"status":value["status"],"boundary":value["boundary"],"sequence":value["sequence"]}));
            } else {
                out.insert(name.clone(), value.clone());
            }
        }
    }
    Value::Object(out)
}

pub fn record_bounded(detail: &mut Value, name: &str, text: &str) {
    let (text, total) = prefix(text, INLINE_UNITS);
    detail[name] = json!(text);
    detail[format!("{name}_chars")] = json!(total);
    detail[format!("{name}_truncated")] = json!(total > INLINE_UNITS);
}
pub fn compact_owned(mut detail: Value) -> Value {
    let source_html = detail["clipboard_html"]
        .as_str()
        .is_some_and(|s| !s.is_empty())
        || detail["source_html_present"] == true;
    for name in CLIPPED {
        if let Some(text) = detail[*name].as_str() {
            let (text, observed) = prefix(text, INLINE_UNITS);
            let end = text.len();
            let total = detail
                .get(format!("{name}_chars"))
                .and_then(Value::as_u64)
                .map_or(observed, |n| n as usize);
            if let Some(Value::String(text)) = detail.get_mut(*name) {
                text.truncate(end);
            }
            detail[format!("{name}_chars")] = json!(total);
            detail[format!("{name}_truncated")] = json!(total > INLINE_UNITS);
        }
    }
    if let Some(rich) = detail.get_mut("rich_text").and_then(Value::as_object_mut) {
        rich.remove("text");
        rich.remove("html");
        rich.insert("source_html_present".into(), json!(source_html));
    }
    if let Some(readback) = detail.get("clipboard_readback") {
        detail["clipboard_readback"] = json!({"status":readback["status"],"boundary":readback["boundary"],"sequence":readback["sequence"]});
    }
    detail
}

pub type Job = Box<dyn FnOnce() + Send + 'static>;
pub const MAX_JOB_BYTES: usize = 64 * 1024 * 1024;
// Bounds admission by estimated owned JSON allocation as well as queued job count.
pub fn owned_bytes(value: &Value) -> usize {
    std::mem::size_of::<Value>()
        + match value {
            Value::String(s) => s.capacity(),
            Value::Array(a) => a.iter().map(owned_bytes).sum(),
            Value::Object(o) => o.iter().map(|(k, v)| k.capacity() + owned_bytes(v)).sum(),
            _ => 0,
        }
}
pub struct Finalizer {
    sender: Option<SyncSender<Job>>,
    join: Option<JoinHandle<()>>,
}
impl Finalizer {
    pub fn new() -> Result<Self, String> {
        let (sender, receiver) = mpsc::sync_channel::<Job>(1);
        let join = thread::Builder::new()
            .name("diagnostic-finalizer".into())
            .spawn(move || {
                while let Ok(job) = receiver.recv() {
                    // Each submitted job reports its own failure; keep later jobs alive after a panic.
                    let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(job));
                }
            })
            .map_err(|e| e.to_string())?;
        Ok(Self {
            sender: Some(sender),
            join: Some(join),
        })
    }
    pub fn submit(&self, bytes: usize, job: Job) -> Result<(), (&'static str, Job)> {
        if bytes > MAX_JOB_BYTES {
            return Err(("oversize", job));
        }
        let Some(sender) = &self.sender else {
            return Err(("stopped", job));
        };
        sender.try_send(job).map_err(|e| match e {
            TrySendError::Full(job) => ("full", job),
            TrySendError::Disconnected(job) => ("disconnected", job),
        })
    }
}
impl Drop for Finalizer {
    fn drop(&mut self) {
        // Disconnect, drain accepted jobs, then join. No clipboard/COM work is submitted.
        self.sender.take();
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

// The original clone-then-clip algorithm, retained to isolate offloading from compaction.
pub fn baseline_inline(detail: &Value) -> Value {
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
    inline
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        io,
        sync::{
            Arc,
            atomic::{AtomicUsize, Ordering},
        },
        time::Duration,
    };

    fn detail() -> Value {
        json!({"run_id":"synthetic", "status":"success", "model":"offline", "input_text":"teh", "output_text":"the", "paste_text":"the", "clipboard_html":"<b>teh</b>", "clipboard_rtf":"rtf", "paste_html":"<b>the</b>", "clipboard_chromium_mime_base64":"AA==", "rich_text":{"text":"the","html":"<b>the</b>","mode":"aligned_html","reason":"","native_docs_status":"verified"}, "clipboard_readback":{"status":"ok","boundary":"before_input","sequence":12,"text":"private full payload"}, "timings":{"total_ms":50.0}, "timing_boundary":"accepted_trigger_to_input_return", "request_payload":"full request", "raw_response":"full response", "raw_ai_output":"the", "error_message":"retained"})
    }
    #[test]
    fn compact_matches_baseline_required_fields_and_unicode_boundary() {
        for text in [
            "".into(),
            "A😀é".into(),
            format!("{}😀tail", "x".repeat(INLINE_UNITS - 1)),
            "😀".repeat(INLINE_UNITS),
        ] {
            let mut d = detail();
            for name in CLIPPED {
                d[*name] = json!(text);
            }
            assert_eq!(compact(&d), baseline_inline(&d));
            assert_eq!(compact_owned(d.clone()), baseline_inline(&d));
            assert_eq!(d["rich_text"]["text"], "the"); // evidence/trial source untouched
        }
    }
    #[test]
    fn developer_trial_and_evidence_admission_combinations() {
        for variant in [
            Variant::Baseline,
            Variant::Lean,
            Variant::CachedReady,
            Variant::OwnedWorker,
        ] {
            for developer in [false, true] {
                for trial in [false, true] {
                    for evidence_admitted in [false, true] {
                        let lean = variant.lean(developer, trial);
                        assert_eq!(lean, variant == Variant::Lean && !developer && !trial);
                        let mut full = detail();
                        full["developer_logging_enabled"] = json!(developer);
                        if developer {
                            full["developer_evidence"] = json!({"status":if evidence_admitted {"pending"} else {"skipped_busy"}});
                        }
                        let before = full.clone();
                        let inline = if variant == Variant::Lean {
                            compact(&full)
                        } else {
                            baseline_inline(&full)
                        };
                        for key in [
                            "run_id",
                            "status",
                            "model",
                            "input_text",
                            "output_text",
                            "paste_text",
                            "timings",
                            "timing_boundary",
                            "error_message",
                        ] {
                            assert_eq!(inline[key], full[key], "{variant:?} {key}");
                        }
                        assert_eq!(full, before);
                        assert!(inline["rich_text"].get("text").is_none());
                        assert!(inline["clipboard_readback"].get("text").is_none());
                        if developer {
                            assert_eq!(inline["developer_evidence"], full["developer_evidence"]);
                        }
                    }
                }
            }
        }
    }
    #[test]
    fn lean_owned_detail_keeps_allocations_and_original_clip_counts() {
        let mut d = detail();
        let large = format!("{}😀tail", "x".repeat(INLINE_UNITS - 1));
        record_bounded(&mut d, "paste_text", &large);
        let ptr = d["input_text"].as_str().unwrap().as_ptr();
        let compact = compact_owned(d);
        assert_eq!(compact["input_text"].as_str().unwrap().as_ptr(), ptr);
        assert_eq!(compact["paste_text_chars"], large.encode_utf16().count());
        assert_eq!(compact["paste_text_truncated"], true);
        assert_eq!(
            compact["paste_text"].as_str().unwrap().len(),
            INLINE_UNITS - 1
        );
    }
    #[test]
    fn lean_source_html_metadata_survives_without_diagnostic_html() {
        let d = json!({"source_html_present":true,"rich_text":{"source_html_present":true,"mode":"aligned_html"}});
        assert_eq!(compact(&d)["rich_text"]["source_html_present"], true);
    }
    #[test]
    fn cache_exact_bytes_changed_same_size_and_failure_clear_rules() {
        let mut cache = ReplacementCache::default();
        let first = br#"{"the":["teh"]}"#.to_vec();
        let other = br#"{"ten":["teh"]}"#.to_vec();
        assert_eq!(first.len(), other.len());
        assert!(cache.refresh(Ok(first.clone())).unwrap());
        let ptr = cache.bytes.as_ref().unwrap().as_ptr();
        assert!(!cache.refresh(Ok(first.clone())).unwrap());
        assert_eq!(ptr, cache.bytes.as_ref().unwrap().as_ptr());
        assert_eq!(cache.parsed.apply("teh").0, "the");
        assert!(cache.refresh(Ok(other)).unwrap());
        assert_eq!(cache.parsed.apply("teh").0, "ten");
        for bad in [
            b"[bad".to_vec(),
            b"{}x".to_vec(),
            b"{\"the\":1}".to_vec(),
            vec![255],
        ] {
            assert!(cache.refresh(Ok(bad.clone())).is_err());
            assert!(cache.bytes.is_none());
            assert_eq!(cache.parsed.apply("teh").0, "teh");
            assert!(cache.refresh(Ok(bad)).is_err());
            assert!(cache.refresh(Ok(first.clone())).unwrap());
        }
        assert!(
            cache
                .refresh(Err(io::Error::from(io::ErrorKind::NotFound)))
                .is_err()
        );
        assert_eq!(cache.parsed.apply("teh").0, "teh");
        assert!(cache.refresh(Ok(first.clone())).unwrap());
        let mut whitespace = first;
        whitespace.push(b' ');
        assert!(cache.refresh(Ok(whitespace)).unwrap());
    }
    #[test]
    fn queue_owns_jobs_bounds_overflow_and_drains_on_shutdown() {
        let worker = Finalizer::new().unwrap();
        let (entered_tx, entered_rx) = mpsc::channel();
        let (release_tx, release_rx) = mpsc::channel();
        let count = Arc::new(AtomicUsize::new(0));
        let c = count.clone();
        assert!(
            worker
                .submit(
                    1,
                    Box::new(move || {
                        entered_tx.send(()).unwrap();
                        release_rx.recv().unwrap();
                        c.fetch_add(1, Ordering::SeqCst);
                    })
                )
                .is_ok()
        );
        entered_rx.recv_timeout(Duration::from_secs(3)).unwrap();
        let owned = String::from("owned after producer returns");
        let c = count.clone();
        assert!(
            worker
                .submit(
                    owned.len(),
                    Box::new(move || {
                        assert_eq!(owned, "owned after producer returns");
                        c.fetch_add(1, Ordering::SeqCst);
                    })
                )
                .is_ok()
        );
        let c = count.clone();
        let (reason, fallback) = worker
            .submit(
                1,
                Box::new(move || {
                    c.fetch_add(1, Ordering::SeqCst);
                }),
            )
            .err()
            .unwrap();
        assert_eq!(reason, "full");
        fallback();
        assert_eq!(count.load(Ordering::SeqCst), 1);
        release_tx.send(()).unwrap();
        drop(worker);
        assert_eq!(count.load(Ordering::SeqCst), 3);
    }
    #[test]
    fn queue_oversize_disconnected_and_stopped_return_owned_job() {
        let worker = Finalizer::new().unwrap();
        let (reason, job) = worker
            .submit(MAX_JOB_BYTES + 1, Box::new(|| {}))
            .err()
            .unwrap();
        assert_eq!(reason, "oversize");
        job();
        drop(worker);
        let (tx, rx) = mpsc::sync_channel(1);
        drop(rx);
        let worker = Finalizer {
            sender: Some(tx),
            join: None,
        };
        let (reason, job) = worker.submit(0, Box::new(|| {})).err().unwrap();
        assert_eq!(reason, "disconnected");
        job();
        let worker = Finalizer {
            sender: None,
            join: None,
        };
        let (reason, job) = worker.submit(0, Box::new(|| {})).err().unwrap();
        assert_eq!(reason, "stopped");
        job();
    }
    #[test]
    fn worker_survives_panicked_job_and_preserves_following_jobs() {
        let worker = Finalizer::new().unwrap();
        let (tx, rx) = mpsc::channel();
        assert!(
            worker
                .submit(
                    0,
                    Box::new(move || {
                        tx.send(()).unwrap();
                        panic!("injected diagnostic failure");
                    })
                )
                .is_ok()
        );
        rx.recv_timeout(Duration::from_secs(3)).unwrap();
        let (tx, rx) = mpsc::channel();
        assert!(
            worker
                .submit(
                    0,
                    Box::new(move || {
                        tx.send(()).unwrap();
                    })
                )
                .is_ok()
        );
        rx.recv_timeout(Duration::from_secs(3)).unwrap();
        drop(worker);
    }
    #[test]
    fn selector_rejects_misspelling_and_arms_are_exclusive() {
        assert!(Variant::parse("lean,cached-ready").is_err());
        for name in ["baseline", "lean", "cached-ready", "owned-worker"] {
            assert_eq!(Variant::parse(name).unwrap().name(), name);
        }
    }
}
