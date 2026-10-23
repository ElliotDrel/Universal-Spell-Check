use serde::Serialize;
use std::time::Instant;

// Accumulate retries, preserve elapsed work on Result errors and panic unwinding,
// and leave stages that never ran as null. No I/O or JSON work inside the timer.
pub fn measure<T>(slot: &mut Option<f64>, work: impl FnOnce() -> T) -> T {
    struct Span<'a>(&'a mut Option<f64>, Instant);
    impl Drop for Span<'_> {
        fn drop(&mut self) {
            *self.0 = Some(self.0.unwrap_or(0.) + self.1.elapsed().as_secs_f64() * 1000.);
        }
    }
    let _span = Span(slot, Instant::now());
    work()
}

#[derive(Default, Serialize)]
pub struct ReadTimings {
    pub open_ms: Option<f64>,
    pub unicode_ms: Option<f64>,
    pub html_ms: Option<f64>,
    pub rtf_ms: Option<f64>,
    pub native_ms: Option<f64>,
    pub enumerate_ms: Option<f64>,
    pub decode_ms: Option<f64>,
}
#[derive(Default, Serialize)]
pub struct CaptureTimings {
    pub modifiers_ms: Option<f64>,
    pub copy_input_ms: Option<f64>,
    pub wait_ms: Option<f64>,
    pub read_ms: Option<f64>,
    pub copy_attempts: u32,
    pub read_attempts: u32,
    pub reads: ReadTimings,
}
#[derive(Default, Serialize)]
pub struct PasteTimings {
    pub write_ms: Option<f64>,
    pub readback_ms: Option<f64>,
    pub settle_ms: Option<f64>,
    pub modifiers_ms: Option<f64>,
    pub input_ms: Option<f64>,
}
#[derive(Default, Serialize)]
pub struct LocalTimings {
    pub dispatch_ms: Option<f64>,
    pub setup_ms: Option<f64>,
    pub backup_ms: Option<f64>,
    pub clipboard_ms: Option<f64>,
    pub history_exclude_ms: Option<f64>,
    pub prepare_ms: Option<f64>,
    pub request_ms: Option<f64>,
    pub replacements_ms: Option<f64>,
    pub rich_mapping_ms: Option<f64>,
    pub diagnostics_ms: Option<f64>,
    pub paste_ms: Option<f64>,
    pub capture: CaptureTimings,
    pub paste: PasteTimings,
}
impl LocalTimings {
    pub fn accounted_ms(&self) -> f64 {
        // Nested capture/paste/read fields and request phase aliases overlap.
        [
            self.dispatch_ms,
            self.setup_ms,
            self.backup_ms,
            self.clipboard_ms,
            self.history_exclude_ms,
            self.prepare_ms,
            self.request_ms,
            self.replacements_ms,
            self.rich_mapping_ms,
            self.diagnostics_ms,
            self.paste_ms,
        ]
        .into_iter()
        .flatten()
        .sum()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn failed_and_unreached_stages_are_distinct() {
        let mut timings = LocalTimings::default();
        let result = measure(&mut timings.backup_ms, || Err::<(), _>("busy"));
        assert_eq!(result, Err("busy"));
        let json = serde_json::to_value(&timings).unwrap();
        assert!(json["backup_ms"].as_f64().unwrap() >= 0.);
        assert!(json["clipboard_ms"].is_null());
        assert!(json["paste"]["input_ms"].is_null());
    }
    #[test]
    fn panic_keeps_elapsed_and_retries_accumulate() {
        let mut slot = None;
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            measure(&mut slot, || panic!("test"));
        }));
        let after_panic = slot.unwrap();
        measure(&mut slot, || ());
        assert!(after_panic >= 0.);
        assert!(slot.unwrap() >= after_panic);
    }
    #[test]
    fn accounting_does_not_double_count_nested_fields() {
        let mut timings = LocalTimings {
            dispatch_ms: Some(2.),
            clipboard_ms: Some(30.),
            paste_ms: Some(60.),
            ..Default::default()
        };
        timings.capture.read_ms = Some(20.);
        timings.capture.reads.html_ms = Some(10.);
        timings.paste.settle_ms = Some(50.);
        assert_eq!(timings.accounted_ms(), 92.);
    }
}
