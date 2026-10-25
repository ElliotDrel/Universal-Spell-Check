//! Native experiments only; normal application entrypoints never select these paths.
use super::*;
use std::cell::Cell;

const MAX_BYTES: usize = 64 * 1024 * 1024;
const MAX_FORMATS: usize = 128;

fn special(format: u32) -> bool {
    matches!(format, 2 | 3 | 9 | 14 | 0x82 | 0x83 | 0x8e)
}
fn supported(format: u32) -> bool {
    format != 0x80 && !(0x200..=0x3ff).contains(&format)
}
fn duplicate(format: u32, source: HANDLE) -> Result<SavedFormat, String> {
    let handle = unsafe {
        if matches!(format, 14 | 0x8e) {
            windows_sys::Win32::Graphics::Gdi::CopyEnhMetaFileW(source, null())
        } else {
            OleDuplicateData(source, format as u16, GMEM_MOVEABLE)
        }
    };
    if handle.is_null() {
        return Err("Could not duplicate complete clipboard snapshot".into());
    }
    Ok(SavedFormat { format, handle })
}
fn copy_global(source: HANDLE) -> Result<Vec<u8>, String> {
    unsafe {
        SetLastError(0);
        let len = GlobalSize(source);
        if len == 0 && GetLastError() != 0 {
            return Err("Clipboard format is not readable HGLOBAL data".into());
        }
        if len > MAX_BYTES {
            return Err("Clipboard snapshot exceeds 64 MiB".into());
        }
        if len == 0 {
            return Err("Zero-sized clipboard HGLOBAL cannot be restored exactly".into());
        }
        let data = GlobalLock(source);
        if data.is_null() {
            return Err("Cannot lock clipboard source".into());
        }
        let result = std::slice::from_raw_parts(data.cast::<u8>(), len).to_vec();
        GlobalUnlock(source);
        Ok(result)
    }
}

enum OwnedFormat {
    Bytes(u32, Vec<u8>),
    Special(SavedFormat),
}
/// Recovery handles are allocated only when restoring, before the clipboard is emptied.
struct OwnedBackup {
    formats: Vec<OwnedFormat>,
}
impl OwnedBackup {
    fn capture(hwnd: HWND) -> Result<Self, String> {
        let _open = open(hwnd, Duration::from_millis(320))?;
        let sequence = unsafe { GetClipboardSequenceNumber() };
        let mut formats = Vec::new();
        let mut used = 0usize;
        let mut format = 0;
        loop {
            unsafe { SetLastError(0) };
            format = unsafe { EnumClipboardFormats(format) };
            if format == 0 {
                if unsafe { GetLastError() } != 0 {
                    return Err("Incomplete clipboard format enumeration".into());
                }
                break;
            }
            if formats.len() >= MAX_FORMATS || !supported(format) {
                return Err("Clipboard snapshot has unsupported or excessive formats".into());
            }
            let source = unsafe { GetClipboardData(format) };
            if source.is_null() {
                return Err("Cannot render complete clipboard snapshot".into());
            }
            formats.push(if special(format) {
                OwnedFormat::Special(duplicate(format, source)?)
            } else {
                let data = copy_global(source)?;
                used = used.saturating_add(data.len());
                if used > MAX_BYTES {
                    return Err("Clipboard snapshot exceeds 64 MiB".into());
                }
                OwnedFormat::Bytes(format, data)
            });
        }
        if unsafe { GetClipboardSequenceNumber() } != sequence {
            return Err("Clipboard changed during snapshot".into());
        }
        Ok(Self { formats })
    }
    fn prepare(&self) -> Result<Vec<SavedFormat>, String> {
        self.prepare_with(Buffer::new)
    }
    fn prepare_with(
        &self,
        mut allocate: impl FnMut(&[u8]) -> Result<Buffer, String>,
    ) -> Result<Vec<SavedFormat>, String> {
        self.formats
            .iter()
            .map(|entry| match entry {
                OwnedFormat::Bytes(format, data) => {
                    let mut buffer = allocate(data)?;
                    let result = SavedFormat {
                        format: *format,
                        handle: buffer.0,
                    };
                    buffer.0 = null_mut();
                    Ok(result)
                }
                OwnedFormat::Special(saved) => duplicate(saved.format, saved.handle),
            })
            .collect()
    }
    fn restore(&self, hwnd: HWND) -> Result<(), String> {
        // Even a failed preparation leaves the current clipboard untouched.
        let mut prepared = self.prepare()?;
        let _open = open(hwnd, Duration::from_millis(320))?;
        if unsafe { EmptyClipboard() } == 0 {
            return Err(error());
        }
        let mut failed = false;
        for entry in &mut prepared {
            if unsafe { SetClipboardData(entry.format, entry.handle) }.is_null() {
                failed = true;
            } else {
                entry.handle = null_mut();
            }
        }
        if failed {
            Err("Clipboard restore publication was incomplete".into())
        } else {
            Ok(())
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Identity {
    sequence: u32,
    producer: usize,
}
impl Identity {
    fn current() -> Self {
        Self {
            sequence: unsafe { GetClipboardSequenceNumber() },
            producer: unsafe { GetClipboardOwner() } as usize,
        }
    }
    fn cacheable(self) -> bool {
        self.sequence != 0 && self.producer != 0
    }
}
/// Single-slot cache owns duplicates, never a producer handle or forwarding IDataObject.
#[derive(Default)]
struct SnapshotCache {
    slot: Option<(Identity, Backup)>,
}
impl SnapshotCache {
    fn invalidate(&mut self) {
        self.slot = None;
    }
    fn refresh_idle(&mut self, hwnd: HWND) -> Result<(), String> {
        self.invalidate();
        let before = Identity::current();
        let mut backup = Backup::capture(hwnd)?;
        backup.disarm();
        let after = Identity::current();
        if !before.cacheable() || before != after || backup.sequence != after.sequence {
            return Err("Clipboard changed while refreshing idle snapshot".into());
        }
        // Baseline does not bound native GDI allocation size. Keep those complete snapshots
        // on the synchronous fallback path instead of pretending the cache has a byte bound.
        if backup.formats.iter().any(|entry| special(entry.format)) {
            return Ok(());
        }
        self.slot = Some((after, backup));
        Ok(())
    }
    fn take(&mut self, hwnd: HWND) -> Result<(Backup, bool), String> {
        // Validate while open; check before returning and never reuse a consumed entry.
        {
            let _open = open(hwnd, Duration::from_millis(320))?;
            let current = Identity::current();
            if self
                .slot
                .as_ref()
                .is_some_and(|(identity, _)| *identity == current && current.cacheable())
            {
                let (_, mut backup) = self.slot.take().unwrap();
                backup.armed = true;
                return Ok((backup, true));
            }
            self.invalidate();
        }
        Ok((Backup::capture(hwnd)?, false))
    }
}

thread_local! { static UPDATES: Cell<u64> = const { Cell::new(0) }; }
unsafe extern "system" fn notification_proc(
    hwnd: HWND,
    message: u32,
    w: WPARAM,
    l: LPARAM,
) -> LRESULT {
    if message == WM_CLIPBOARDUPDATE {
        UPDATES.with(|updates| updates.set(updates.get().wrapping_add(1)));
        return 0;
    }
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
fn notification_window() -> Result<WorkerWindow, String> {
    unsafe {
        let instance = GetModuleHandleW(null());
        let class = wide("USC.ClipboardExperiment.Notifications");
        let wc = WNDCLASSW {
            lpfnWndProc: Some(notification_proc),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            ..zeroed()
        };
        RegisterClassW(&wc);
        let hwnd = CreateWindowExW(
            0,
            class.as_ptr(),
            class.as_ptr(),
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            null_mut(),
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(error());
        }
        if AddClipboardFormatListener(hwnd) == 0 {
            DestroyWindow(hwnd);
            return Err(error());
        }
        Ok(WorkerWindow(hwnd))
    }
}
/// Notifications only wake validation. Sequence, full format reads and nonempty text remain required.
fn notification_read(hwnd: HWND, before: u32, timeout: Duration) -> Result<Clipboard, String> {
    let until = Instant::now() + timeout;
    let mut generation = UPDATES.with(Cell::get);
    let mut retry_at = Instant::now();
    loop {
        let now = Instant::now();
        let next = UPDATES.with(Cell::get);
        let notified = next != generation;
        generation = next;
        if (notified || now >= retry_at) && unsafe { GetClipboardSequenceNumber() } != before {
            if let Ok(data) = read(hwnd)
                && !data.text.trim().is_empty()
            {
                return Ok(data);
            }
            retry_at = Instant::now() + Duration::from_millis(25);
        }
        let Some(remaining) = until.checked_duration_since(Instant::now()) else {
            return Err("Clipboard readiness deadline elapsed".into());
        };
        // Any notification wakes validation; a bounded watchdog also retries delayed rendering.
        pump_wait(remaining.as_millis().clamp(1, 25) as u32);
    }
}

thread_local! { static TIMER_ERROR: std::cell::RefCell<Option<String>> = const { std::cell::RefCell::new(None) }; }
unsafe extern "system" fn publish_tick(hwnd: HWND, _: u32, timer: usize, _: u32) {
    unsafe {
        KillTimer(hwnd, timer);
    }
    if let Err(error) = write(
        hwnd,
        "Clipboard experiment: caf\u{e9} \u{1f600}",
        "<b>Clipboard experiment</b>",
        None,
        true,
    ) {
        TIMER_ERROR.with(|slot| *slot.borrow_mut() = Some(error));
    }
}
struct PublicationTimer(HWND, usize);
impl Drop for PublicationTimer {
    fn drop(&mut self) {
        unsafe {
            KillTimer(self.0, self.1);
        }
    }
}

fn roundtrip(
    variant: &str,
    hwnd: HWND,
    runs: usize,
    attempts: &mut Vec<serde_json::Value>,
) -> Result<serde_json::Value, String> {
    let mut samples = Vec::new();
    let mut cache = SnapshotCache::default();
    let mut hits = 0;
    let mut capture_samples = Vec::new();
    for index in 0..runs {
        attempts.push(serde_json::json!({"attempt": index + 1, "status": "started"}));
        let expected = "Clipboard experiment: caf\u{e9} \u{1f600}";
        write(hwnd, expected, "<b>Clipboard experiment</b>", None, true)?;
        let original = OwnedBackup::capture(hwnd)?;
        if variant == "cache" {
            cache.refresh_idle(hwnd)?;
        }
        let start = Instant::now();
        match variant {
            "baseline" => {
                let mut saved = Backup::capture(hwnd)?;
                capture_samples.push(start.elapsed().as_secs_f64() * 1000.0);
                write(hwnd, "temporary", "", None, true)?;
                if !saved.restore() {
                    return Err("Baseline restore failed".into());
                }
            }
            "owned" => {
                let saved = OwnedBackup::capture(hwnd)?;
                capture_samples.push(start.elapsed().as_secs_f64() * 1000.0);
                write(hwnd, "temporary", "", None, true)?;
                saved.restore(hwnd)?;
            }
            "cache" => {
                let (mut saved, hit) = cache.take(hwnd)?;
                hits += usize::from(hit);
                capture_samples.push(start.elapsed().as_secs_f64() * 1000.0);
                cache.invalidate(); // Every self-publication invalidates cached identity.
                write(hwnd, "temporary", "", None, true)?;
                if !saved.restore() {
                    return Err("Cached restore failed".into());
                }
            }
            "notification" => {
                let before = unsafe { GetClipboardSequenceNumber() };
                TIMER_ERROR.with(|slot| *slot.borrow_mut() = None);
                let id = unsafe { SetTimer(hwnd, 1, 10, Some(publish_tick)) };
                if id == 0 {
                    return Err(error());
                }
                let _timer = PublicationTimer(hwnd, id);
                let data = notification_read(hwnd, before, Duration::from_millis(700))?;
                if let Some(error) = TIMER_ERROR.with(|slot| slot.borrow_mut().take()) {
                    return Err(error);
                }
                if data.text != expected {
                    return Err("Notification text mismatch".into());
                }
            }
            _ => return Err("Variant must be baseline, owned, cache, or notification".into()),
        }
        samples.push(start.elapsed().as_secs_f64() * 1000.0);
        if variant == "cache" {
            cache.refresh_idle(hwnd)?;
            write(hwnd, expected, "<b>Clipboard experiment</b>", None, true)?;
            let (mut miss, hit) = cache.take(hwnd)?;
            miss.disarm();
            if hit {
                return Err("Equal text with a new sequence incorrectly hit cache".into());
            }
        }
        let actual = OwnedBackup::capture(hwnd)?;
        // Compare every byte-based format and format order; special handles are tested separately.
        if !same_bytes(&original, &actual) {
            return Err("Snapshot format/order/bytes mismatch".into());
        }
        attempts[index] = serde_json::json!({"attempt":index + 1,"status":"passed","roundtrip_ms":samples[index]});
    }
    Ok(
        serde_json::json!({"variant":variant,"runs":runs,"samples_ms":samples,"cache_hits":hits,"capture_samples_ms":capture_samples,
        "scope":"synthetic native snapshot roundtrip; includes temporary publication and restore; idle cache refresh excluded",
        "editor_acceptance":"pending","speedup_claim":false}),
    )
}
fn same_bytes(a: &OwnedBackup, b: &OwnedBackup) -> bool {
    a.formats.len() == b.formats.len()
        && a.formats.iter().zip(&b.formats).all(|(a, b)| match (a, b) {
            (OwnedFormat::Bytes(af, ab), OwnedFormat::Bytes(bf, bb)) => af == bf && ab == bb,
            (OwnedFormat::Special(a), OwnedFormat::Special(b)) => a.format == b.format,
            _ => false,
        })
}

pub fn run(args: &[String]) -> Result<(), String> {
    let variant = args
        .get(2)
        .ok_or("Usage: --clipboard-experiment baseline|owned|cache|notification --allow-desktop")?;
    if !["baseline", "owned", "cache", "notification"].contains(&variant.as_str()) {
        return Err("Unknown clipboard experiment".into());
    }
    if !args.iter().any(|arg| arg == "--allow-desktop") {
        return Err(
            "Clipboard mutation requires --allow-desktop and an exclusive desktop slot".into(),
        );
    }
    initialize_sta()?;
    let window = notification_window()?;
    // Refuse any writes unless a complete original snapshot can be owned first.
    let identity = Identity::current();
    let original = OwnedBackup::capture(window.0)?;
    let prepared = original.prepare()?;
    if Identity::current() != identity {
        return Err("Clipboard changed before experiment; no mutation performed".into());
    }
    // Original recovery must not depend on allocating during an experiment failure.
    let mut recovery = Backup {
        formats: prepared,
        owner: window.0,
        sequence: identity.sequence,
        armed: true,
    };
    let mut attempts = Vec::new();
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        roundtrip(variant, window.0, 20, &mut attempts)
    }))
    .map_err(|_| "Experiment panicked".to_string())
    .and_then(|result| result);
    let restored = if recovery.restore() {
        Ok(())
    } else {
        Err("Original clipboard restore failed".to_string())
    };
    let restored = restored.and_then(|_| {
        let actual = OwnedBackup::capture(window.0)?;
        if same_bytes(&original, &actual) {
            Ok(())
        } else {
            Err("Original clipboard restore verification failed".to_string())
        }
    });
    let success = result.is_ok() && restored.is_ok();
    println!(
        "{}",
        serde_json::json!({"schema":1,"variant":variant,
        "app_version":env!("CARGO_PKG_VERSION"),"debug_assertions":cfg!(debug_assertions),
        "success":success,"attempts":attempts,"result":result.as_ref().ok(),
        "error":result.as_ref().err(),"original_restore_error":restored.as_ref().err()})
    );
    restored?;
    result.map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn owned_bytes_preserve_order_binary_and_inconsistent_encodings() {
        let snapshot = OwnedBackup {
            formats: vec![
                OwnedFormat::Bytes(13, vec![0x41, 0, 0, 0]),
                OwnedFormat::Bytes(1, b"different\0".to_vec()),
                OwnedFormat::Bytes(0xc001, vec![0, 255, 0, 3]),
            ],
        };
        let prepared = snapshot.prepare().unwrap();
        for (saved, owned) in prepared.iter().zip(&snapshot.formats) {
            let OwnedFormat::Bytes(format, bytes) = owned else {
                panic!()
            };
            assert_eq!(saved.format, *format);
            assert_eq!(copy_global(saved.handle).unwrap(), *bytes);
        }
    }
    #[test]
    fn cache_identity_uses_owner_and_sequence_not_text() {
        let a = Identity {
            sequence: 1,
            producer: 10,
        };
        assert!(a != Identity { sequence: 2, ..a });
        assert!(a != Identity { producer: 11, ..a });
        assert!(!Identity { sequence: 0, ..a }.cacheable());
        assert!(!Identity { producer: 0, ..a }.cacheable());
        let mut cache = SnapshotCache::default();
        cache.invalidate();
        assert!(cache.slot.is_none());
    }
    #[test]
    fn unsupported_owner_formats_fail_closed_and_special_handles_stay_special() {
        for format in [0x80, 0x200, 0x2ff, 0x3ff] {
            assert!(!supported(format));
        }
        for format in [2, 3, 9, 14, 0x82, 0x83, 0x8e] {
            assert!(special(format));
        }
        assert!(supported(0xc001));
        assert!(!special(13));
    }
    #[test]
    fn notifications_are_hints_and_do_not_touch_clipboard() {
        let before = UPDATES.with(Cell::get);
        unsafe {
            notification_proc(null_mut(), WM_CLIPBOARDUPDATE, 0, 0);
        }
        assert_eq!(UPDATES.with(Cell::get), before.wrapping_add(1));
    }
    #[test]
    fn allocation_failure_refuses_preparation_before_any_publication() {
        let snapshot = OwnedBackup {
            formats: vec![
                OwnedFormat::Bytes(13, vec![65, 0, 0, 0]),
                OwnedFormat::Bytes(1, b"A\0".to_vec()),
            ],
        };
        let mut count = 0;
        let result = snapshot.prepare_with(|data| {
            count += 1;
            if count == 2 {
                Err("injected allocation failure".into())
            } else {
                Buffer::new(data)
            }
        });
        assert!(result.is_err());
        assert_eq!(count, 2);
        assert_eq!(snapshot.formats.len(), 2);
    }
    #[test]
    fn gdi_bitmap_snapshot_owns_independent_handle() {
        use windows_sys::Win32::Graphics::Gdi::*;
        let source = unsafe { CreateBitmap(2, 2, 1, 1, null()) };
        assert!(!source.is_null());
        let saved = duplicate(2, source).unwrap();
        assert_ne!(saved.handle, source);
        unsafe {
            DeleteObject(source);
        }
        let mut bitmap: BITMAP = unsafe { zeroed() };
        assert_ne!(
            unsafe {
                GetObjectW(
                    saved.handle,
                    size_of::<BITMAP>() as i32,
                    (&mut bitmap as *mut BITMAP).cast(),
                )
            },
            0
        );
        assert_eq!((bitmap.bmWidth, bitmap.bmHeight), (2, 2));
    }
}
