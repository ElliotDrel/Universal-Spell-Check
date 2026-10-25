use crate::{
    channel,
    logging::Logger,
    store::{Store, wide},
};
use serde_json::{Value, json};
use std::{
    path::Path,
    ptr::{null, null_mut},
    sync::{
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver},
    },
    thread,
    time::Duration,
};
use windows_sys::Win32::{
    Foundation::*, Graphics::Gdi::*, System::Threading::*, UI::WindowsAndMessaging::*,
};
static IMAGE_BUSY: AtomicBool = AtomicBool::new(false);
static UIA_BUSY: AtomicBool = AtomicBool::new(false);
static ACTIVE: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
fn now() -> String {
    time::OffsetDateTime::now_utc()
        .format(&time::format_description::well_known::Rfc3339)
        .unwrap_or_default()
}
struct EvidenceSlot;
impl Drop for EvidenceSlot {
    fn drop(&mut self) {
        ACTIVE.fetch_sub(1, Ordering::Release);
    }
}
pub struct Evidence {
    before: Option<Context>,
    _slot: EvidenceSlot,
    pub source: (usize, u32, String),
}
struct Context {
    phase: &'static str,
    requested: String,
    target: (usize, u32, String),
    image: Option<Receiver<Result<Vec<u8>, String>>>,
    uia: Option<Receiver<Value>>,
}
impl Evidence {
    pub fn begin(target: (usize, u32, String)) -> Option<Self> {
        if ACTIVE.fetch_add(1, Ordering::AcqRel) >= 2 {
            ACTIVE.fetch_sub(1, Ordering::Release);
            return None;
        }
        let slot = EvidenceSlot;
        Some(Self {
            before: Some(context("before-source", target.clone(), false)),
            _slot: slot,
            source: target,
        })
    }
    pub fn finish(self, store: Store, logger: Logger, detail: Value, paste: (usize, u32, String)) {
        self.prepare_finish(store, logger, paste)(detail);
    }
    pub fn prepare_finish(
        mut self,
        store: Store,
        logger: Logger,
        paste: (usize, u32, String),
    ) -> impl FnOnce(Value) + Send + 'static {
        // Freeze target and launch after-capture on the correction thread, before offloading JSON.
        let after = context("after-paste", paste.clone(), true);
        let before = self.before.take().unwrap();
        move |detail| {
            let _ = thread::Builder::new()
                .name("evidence-save".into())
                .spawn(move || {
                    let result = save_inner(
                        &store,
                        &logger,
                        &detail,
                        &self.source,
                        &paste,
                        vec![before, after],
                    );
                    logger.data(
                        "developer_evidence_completed",
                        json!({"run_id":detail["run_id"],"reference":result}),
                    );
                    drop(self);
                });
        }
    }
}
fn context(phase: &'static str, target: (usize, u32, String), after: bool) -> Context {
    let requested = now();
    let image = {
        let (tx, rx) = mpsc::sync_channel(1);
        let hwnd = target.0;
        let started = thread::Builder::new()
            .name("window-image".into())
            .spawn(move || {
                if after {
                    thread::sleep(Duration::from_millis(600));
                }
                if IMAGE_BUSY.swap(true, Ordering::AcqRel) {
                    let _ = tx.send(Err("busy".into()));
                    return;
                }
                let result = std::panic::catch_unwind(|| screenshot(hwnd))
                    .unwrap_or_else(|_| Err("image_capture_failed".into()));
                let _ = tx.send(result);
                IMAGE_BUSY.store(false, Ordering::Release);
            });
        if started.is_ok() { Some(rx) } else { None }
    };
    let uia = {
        let (tx, rx) = mpsc::sync_channel(1);
        let started = thread::Builder::new()
            .name("window-accessibility".into())
            .spawn(move || {
                if after {
                    thread::sleep(Duration::from_millis(600));
                }
                if UIA_BUSY.swap(true, Ordering::AcqRel) {
                    let _ = tx.send(json!({"status":"busy"}));
                    return;
                }
                let result = std::panic::catch_unwind(accessibility)
                    .unwrap_or_else(|_| json!({"status":"failed"}));
                let _ = tx.send(result);
                UIA_BUSY.store(false, Ordering::Release);
            });
        if started.is_ok() { Some(rx) } else { None }
    };
    Context {
        phase,
        requested,
        target,
        image,
        uia,
    }
}
fn accessibility() -> Value {
    use windows::{
        Win32::{System::Com::*, UI::Accessibility::*},
        core::Interface,
    };
    unsafe {
        if CoInitializeEx(None, COINIT_MULTITHREADED).is_err() {
            return json!({"status":"failed","error_type":"com_init"});
        }
        let result = (|| -> windows::core::Result<Value> {
            let automation: IUIAutomation =
                CoCreateInstance(&CUIAutomation, None, CLSCTX_INPROC_SERVER)?;
            if let Ok(bounded) = automation.cast::<IUIAutomation2>() {
                let _ = bounded.SetConnectionTimeout(500);
                let _ = bounded.SetTransactionTimeout(500);
            }
            let focused = automation.GetFocusedElement()?;
            if focused.CurrentIsPassword()?.as_bool() {
                return Ok(json!({"status":"password_control"}));
            }
            let name = focused.CurrentName()?.to_string();
            let automation_id = focused.CurrentAutomationId()?.to_string();
            let mut result = json!({"status":"ok","captured_at":now(),"name":name.chars().take(1024).collect::<String>(),"automation_id":automation_id.chars().take(1024).collect::<String>(),"text_status":"unsupported","selection_status":"unsupported","selection_offsets":"not_captured"});
            if let Ok(pattern) =
                focused.GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId)
            {
                if let Ok(range) = pattern.DocumentRange()
                    && let Ok(text) = range.GetText(32769)
                {
                    let text = text.to_string();
                    result["text"] = json!(text.chars().take(32768).collect::<String>());
                    result["text_status"] = json!(if text.encode_utf16().count() > 32768 {
                        "truncated"
                    } else {
                        "available"
                    });
                }
                if let Ok(selection) = pattern.GetSelection() {
                    let len = selection.Length()?;
                    let mut ranges = Vec::new();
                    for i in 0..len.min(8) {
                        ranges.push(selection.GetElement(i)?.GetText(8193)?.to_string());
                    }
                    result["selection"] = json!(ranges);
                    result["selection_range_count"] = json!(len);
                    result["selection_status"] = json!(if len > 8
                        || ranges.iter().any(|r| r.encode_utf16().count() > 8192)
                    {
                        "truncated"
                    } else {
                        "available"
                    });
                }
            }
            let _ = focused.cast::<IUIAutomationElement>();
            Ok(result)
        })();
        CoUninitialize();
        result.unwrap_or_else(|_| json!({"status":"failed","error_type":"uia_provider"}))
    }
}
fn screenshot(hwnd: usize) -> Result<Vec<u8>, String> {
    unsafe {
        let mut rect = std::mem::zeroed();
        if GetWindowRect(hwnd as _, &mut rect) == 0 {
            return Err("window_unavailable".into());
        }
        let left = rect.left.max(GetSystemMetrics(SM_XVIRTUALSCREEN));
        let top = rect.top.max(GetSystemMetrics(SM_YVIRTUALSCREEN));
        let right = rect
            .right
            .min(GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN));
        let bottom = rect
            .bottom
            .min(GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        let width = right - left;
        let height = bottom - top;
        if width <= 0 || height <= 0 || width as i64 * height as i64 > 16_000_000 {
            return Err("image_bounds_limit".into());
        }
        let screen = GetDC(null_mut());
        if screen.is_null() {
            return Err("screen_dc".into());
        }
        let memory = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, width, height);
        if memory.is_null() || bitmap.is_null() {
            if !memory.is_null() {
                DeleteDC(memory);
            }
            if !bitmap.is_null() {
                DeleteObject(bitmap);
            }
            ReleaseDC(null_mut(), screen);
            return Err("image_allocation".into());
        }
        let previous = SelectObject(memory, bitmap);
        let copied = BitBlt(
            memory,
            0,
            0,
            width,
            height,
            screen,
            left,
            top,
            SRCCOPY | CAPTUREBLT,
        );
        SelectObject(memory, previous);
        let mut info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..std::mem::zeroed()
            },
            ..std::mem::zeroed()
        };
        let mut bgra = vec![0u8; width as usize * height as usize * 4];
        let lines = if copied != 0 {
            GetDIBits(
                memory,
                bitmap,
                0,
                height as u32,
                bgra.as_mut_ptr().cast(),
                &mut info,
                DIB_RGB_COLORS,
            )
        } else {
            0
        };
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(null_mut(), screen);
        if lines != height {
            return Err("screen_capture_failed".into());
        }
        let mut rgb = Vec::with_capacity(width as usize * height as usize * 3);
        for pixel in bgra.as_chunks::<4>().0 {
            rgb.extend_from_slice(&[pixel[2], pixel[1], pixel[0]]);
        }
        let mut png = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut png, width as u32, height as u32);
            encoder.set_color(png::ColorType::Rgb);
            encoder.set_depth(png::BitDepth::Eight);
            let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
            writer.write_image_data(&rgb).map_err(|e| e.to_string())?;
        }
        Ok(png)
    }
}
fn quota_write(
    root: &Path,
    id: &str,
    name: &str,
    bytes: &[u8],
    logger: &Logger,
    warned: &mut bool,
) -> Value {
    if bytes.len() > 16 * 1024 * 1024 {
        return json!({"status":"size_limit","complete":false});
    }
    unsafe {
        let mutex = CreateMutexW(null(), 0, wide(channel::EVIDENCE_MUTEX).as_ptr());
        if mutex.is_null() {
            return json!({"status":"storage_busy"});
        }
        let status = WaitForSingleObject(mutex, 15_000);
        if status != WAIT_OBJECT_0 && status != WAIT_ABANDONED {
            CloseHandle(mutex);
            return json!({"status":"storage_busy"});
        }
        let result = crate::evidence_storage::write_locked(
            root,
            id,
            name,
            bytes,
            crate::evidence_storage::STORE_LIMIT,
        );
        ReleaseMutex(mutex);
        CloseHandle(mutex);
        match result {
            Ok((value, rotation)) => {
                if rotation.bytes > 0 {
                    logger.data(
                        "developer_evidence_pruned",
                        json!({"runs":rotation.runs,"bytes":rotation.bytes}),
                    );
                }
                if (rotation.blocked_runs > 0 || value["status"] == "storage_limit") && !*warned {
                    logger.data("developer_evidence_storage_pressure", json!({"run_id":id,"status":value["status"],"blocked_runs":rotation.blocked_runs,"stored_bytes":value["stored_bytes"],"required_bytes":value["required_bytes"]}));
                    *warned = true;
                }
                value
            }
            Err(error) => {
                if !*warned {
                    logger.data(
                        "developer_evidence_storage_failed",
                        json!({"run_id":id,"error":error.to_string()}),
                    );
                    *warned = true;
                }
                json!({"status":"failed","complete":false})
            }
        }
    }
}
fn save_inner(
    store: &Store,
    logger: &Logger,
    detail: &Value,
    source: &(usize, u32, String),
    paste: &(usize, u32, String),
    contexts: Vec<Context>,
) -> Value {
    let id = detail["run_id"].as_str().unwrap_or("unknown");
    let root = store.log_directory.join("developer-evidence");
    let mut payloads = serde_json::Map::new();
    let mut storage_warned = false;
    let mut detail = detail.clone();
    if let Some(readback) = detail["clipboard_readback"].as_object().cloned() {
        for (key, value) in readback {
            if key.starts_with("readback_") {
                detail[key] = value;
            }
        }
    }
    for name in [
        "input_text",
        "clipboard_html",
        "clipboard_rtf",
        "clipboard_formats",
        "raw_ai_output",
        "output_text",
        "paste_text",
        "paste_html",
        "clipboard_chromium_mime_base64",
        "paste_chromium_mime_base64",
        "readback_text",
        "readback_html",
        "readback_rtf",
        "readback_formats",
        "readback_chromium_mime_base64",
    ] {
        let result = match detail[name].as_str() {
            Some(text) => {
                let mut v = quota_write(
                    &root,
                    id,
                    &format!("{name}.txt"),
                    text.as_bytes(),
                    logger,
                    &mut storage_warned,
                );
                v["chars"] = json!(text.encode_utf16().count());
                v
            }
            None => json!({"status":"unavailable","complete":false}),
        };
        payloads.insert(name.into(), result);
    }
    let mut captured = Vec::new();
    for context in contexts {
        let image = match context.image {
            Some(rx) => match rx.recv_timeout(Duration::from_millis(1500)) {
                Ok(Ok(bytes)) => quota_write(
                    &root,
                    id,
                    &format!("{}.png", context.phase),
                    &bytes,
                    logger,
                    &mut storage_warned,
                ),
                Ok(Err(e)) => json!({"status":e}),
                Err(_) => json!({"status":"timeout"}),
            },
            None => json!({"status":"busy"}),
        };
        let uia = context
            .uia
            .and_then(|rx| rx.recv_timeout(Duration::from_millis(750)).ok())
            .unwrap_or_else(|| json!({"status":"busy_or_timeout"}));
        captured.push(json!({"phase":context.phase,"requested_at":context.requested,"target_hwnd":context.target.0,"image":image,"accessibility":uia}));
    }
    let manifest = json!({"schema_version":1,"run_id":id,"channel":channel::CHANNEL,"app_version":channel::VERSION,"created_at":now(),"source_target":{"hwnd":source.0,"pid":source.1,"process":source.2},"paste_target":{"hwnd":paste.0,"pid":paste.1,"process":paste.2},"payloads":payloads,"contexts":captured,"clipboard_readback_boundary":detail["clipboard_readback"]["boundary"],"capture_contract":"Native screen pixels and bounded UIA; no rendering-completion claim"});
    let bytes = serde_json::to_vec(&manifest).unwrap_or_default();
    let result = quota_write(
        &root,
        id,
        "manifest.json",
        &bytes,
        logger,
        &mut storage_warned,
    );
    json!({"schema_version":1,"manifest_path":format!("developer-evidence/{id}/manifest.json"),"status":result["status"]})
}
// Verify before injecting input: a reader after SendInput can lock out the target's paste.
// Diagnostics never wait for clipboard access and never open it in a post-input worker.
pub fn readback_before_input(owner: usize, sequence: u32) -> Value {
    use base64::Engine;
    let mut value = match crate::clipboard::read_immediate(owner as _) {
        Ok(data) if data.sequence == sequence => json!({
            "status":"ok", "captured_at":now(), "sequence":sequence,
            "readback_text":data.text, "readback_html":data.html, "readback_rtf":data.rtf,
            "readback_formats":data.formats,
            "readback_chromium_mime_base64":base64::engine::general_purpose::STANDARD.encode(data.native)
        }),
        Ok(_) => json!({"status":"source_changed"}),
        Err(_) => json!({"status":"busy_or_unavailable"}),
    };
    value["boundary"] = json!("after_clipboard_write_before_input");
    value
}
