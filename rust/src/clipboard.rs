use crate::{
    docs_native,
    store::wide,
    timing::{CaptureTimings, ReadTimings, measure},
};
use std::{
    mem::{size_of, zeroed},
    ptr::{null, null_mut},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::*,
    System::{DataExchange::*, LibraryLoader::*, Memory::*, Ole::*, Threading::*},
    UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
};
pub fn error() -> String {
    std::io::Error::last_os_error().to_string()
}
#[derive(Clone, Default)]
pub struct Clipboard {
    pub text: String,
    pub html: String,
    pub rtf: String,
    pub native: Vec<u8>,
    pub formats: String,
    pub sequence: u32,
    pub html_status: &'static str,
    pub rtf_status: &'static str,
    pub native_status: &'static str,
}
struct Open;
impl Drop for Open {
    fn drop(&mut self) {
        unsafe {
            CloseClipboard();
        }
    }
}
pub(crate) fn pump_wait(ms: u32) {
    unsafe {
        MsgWaitForMultipleObjectsEx(0, null(), ms, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        pump_messages();
    }
}
pub(crate) fn pump_messages() {
    unsafe {
        let mut msg = zeroed();
        while PeekMessageW(&mut msg, null_mut(), 0, 0, PM_REMOVE) != 0 {
            if msg.message == WM_QUIT {
                PostQuitMessage(msg.wParam as i32);
                break;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
}
fn open(hwnd: HWND, budget: Duration) -> Result<Open, String> {
    let start = Instant::now();
    let mut delay = 1;
    loop {
        if unsafe { OpenClipboard(hwnd) } != 0 {
            return Ok(Open);
        }
        if start.elapsed() >= budget {
            let owner = unsafe { GetOpenClipboardWindow() };
            return Err(format!(
                "Clipboard busy; holder={} {}",
                owner as usize,
                error()
            ));
        }
        pump_wait(delay);
        delay = (delay * 2).min(16);
    }
}
struct Buffer(HGLOBAL);
impl Drop for Buffer {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                GlobalFree(self.0);
            }
        }
    }
}
impl Buffer {
    fn new(bytes: &[u8]) -> Result<Self, String> {
        unsafe {
            let memory = Self(GlobalAlloc(GMEM_MOVEABLE, bytes.len().max(1)));
            if memory.0.is_null() {
                return Err(error());
            }
            let data = GlobalLock(memory.0);
            if data.is_null() {
                return Err(error());
            }
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), data.cast(), bytes.len());
            GlobalUnlock(memory.0);
            Ok(memory)
        }
    }
    fn transfer(mut self, format: u32) -> Result<(), String> {
        unsafe {
            if SetClipboardData(format, self.0).is_null() {
                return Err(error());
            }
            self.0 = null_mut();
            Ok(())
        }
    }
}
pub fn format(name: &str) -> u32 {
    unsafe { RegisterClipboardFormatW(wide(name).as_ptr()) }
}
fn bytes(format: u32, limit: usize) -> Result<Vec<u8>, String> {
    unsafe {
        if IsClipboardFormatAvailable(format) == 0 {
            return Ok(Vec::new());
        }
        let handle = GetClipboardData(format);
        if handle.is_null() {
            return Err(error());
        }
        let len = GlobalSize(handle);
        if len > limit {
            return Err("Clipboard format exceeds size limit".into());
        }
        let data = GlobalLock(handle);
        if data.is_null() {
            return Err(error());
        }
        let result = std::slice::from_raw_parts(data.cast::<u8>(), len).to_vec();
        GlobalUnlock(handle);
        Ok(result)
    }
}
fn optional_format(result: Result<Vec<u8>, String>) -> (Vec<u8>, &'static str) {
    match result {
        Ok(data) if data.is_empty() => (data, "absent"),
        Ok(data) => (data, "ok"),
        Err(error) if error == "Clipboard format exceeds size limit" => (Vec::new(), "too_large"),
        Err(_) => (Vec::new(), "read_failed"),
    }
}
fn utf8(bytes: Vec<u8>) -> String {
    String::from_utf8_lossy(bytes.split(|c| *c == 0).next().unwrap_or_default()).into_owned()
}
pub fn read(hwnd: HWND) -> Result<Clipboard, String> {
    read_with_budget(hwnd, Duration::from_millis(320))
}
pub fn read_immediate(hwnd: HWND) -> Result<Clipboard, String> {
    read_with_budget(hwnd, Duration::ZERO)
}
fn read_with_budget(hwnd: HWND, budget: Duration) -> Result<Clipboard, String> {
    read_measured(hwnd, budget, &mut ReadTimings::default())
}
fn read_measured(
    hwnd: HWND,
    budget: Duration,
    timings: &mut ReadTimings,
) -> Result<Clipboard, String> {
    let _open = measure(&mut timings.open_ms, || open(hwnd, budget))?;
    let sequence = unsafe { GetClipboardSequenceNumber() };
    let text = measure(&mut timings.unicode_ms, || -> Result<String, String> {
        let raw = bytes(13, 16 * 1024 * 1024)?;
        let units: Vec<u16> = raw
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .take_while(|u| *u != 0)
            .collect();
        Ok(String::from_utf16_lossy(&units))
    })?;
    // Unicode is required; unavailable ancillary formats must not discard usable text.
    let (html, html_status) = measure(&mut timings.html_ms, || {
        optional_format(bytes(format("HTML Format"), 16 * 1024 * 1024))
    });
    let (rtf, rtf_status) = measure(&mut timings.rtf_ms, || {
        optional_format(bytes(format("Rich Text Format"), 16 * 1024 * 1024))
    });
    let (native, native_status) = measure(&mut timings.native_ms, || {
        optional_format(bytes(format(docs_native::FORMAT), 1024 * 1024))
    });
    let (html, rtf) = measure(&mut timings.decode_ms, || (utf8(html), utf8(rtf)));
    let formats = measure(&mut timings.enumerate_ms, || {
        let mut formats = Vec::new();
        unsafe {
            let mut f = 0;
            loop {
                f = EnumClipboardFormats(f);
                if f == 0 {
                    break;
                }
                let mut name = [0u16; 256];
                let len = GetClipboardFormatNameW(f, name.as_mut_ptr(), 256);
                formats.push(if len > 0 {
                    String::from_utf16_lossy(&name[..len as usize])
                } else {
                    match f {
                        13 => "UnicodeText".into(),
                        1 => "Text".into(),
                        7 => "OEMText".into(),
                        16 => "Locale".into(),
                        _ => format!("#{f}"),
                    }
                });
            }
        }
        formats
    });
    if unsafe { GetClipboardSequenceNumber() } != sequence {
        return Err("Clipboard changed while capturing formats".into());
    }
    Ok(Clipboard {
        text,
        html,
        rtf,
        native,
        formats: formats.join(","),
        sequence,
        html_status,
        rtf_status,
        native_status,
    })
}
pub fn write(
    hwnd: HWND,
    text: &str,
    html: &str,
    native: Option<&[u8]>,
    exclude_history: bool,
) -> Result<u32, String> {
    // Allocate everything before EmptyClipboard. Each successful SetClipboardData transfers ownership.
    let unicode: Vec<u8> = text
        .encode_utf16()
        .chain(Some(0))
        .flat_map(u16::to_le_bytes)
        .collect();
    let mut prepared = Vec::new();
    if let Some(native) = native {
        prepared.push((format(docs_native::FORMAT), Buffer::new(native)?));
    }
    if !html.is_empty() {
        let mut raw = html.as_bytes().to_vec();
        raw.push(0);
        prepared.push((format("HTML Format"), Buffer::new(&raw)?));
    }
    prepared.push((13, Buffer::new(&unicode)?));
    if exclude_history {
        for name in ["CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"] {
            prepared.push((format(name), Buffer::new(&0u32.to_le_bytes())?));
        }
    }
    let _open = open(hwnd, Duration::from_millis(320))?;
    if unsafe { EmptyClipboard() } == 0 {
        return Err(error());
    }
    for (f, memory) in prepared {
        memory.transfer(f)?;
    }
    Ok(unsafe { GetClipboardSequenceNumber() })
}
struct SettlementTimer(HANDLE);
impl Drop for SettlementTimer {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { CloseHandle(self.0) };
        }
    }
}
pub fn settle_publication() {
    // Zero-delay publication failed real editor checks with correct bytes and selection.
    // Keep at least 50 ms, without the coarse message-wait timeout's extra timer tick.
    let until = Instant::now() + Duration::from_millis(50);
    let timer = SettlementTimer(unsafe {
        CreateWaitableTimerExW(
            null(),
            null(),
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TIMER_MODIFY_STATE | SYNCHRONIZATION_SYNCHRONIZE,
        )
    });
    settle_until(until, timer.0);
}
fn settle_until(until: Instant, mut timer: HANDLE) {
    while let Some(remaining) = until.checked_duration_since(Instant::now()) {
        if !timer.is_null() {
            // Relative 100-ns units, rounded up. The monotonic deadline remains authoritative
            // after early message wakes or timer signals; neither may shorten settlement.
            let due = -(remaining.as_nanos().div_ceil(100).max(1) as i64);
            unsafe {
                if SetWaitableTimer(timer, &due, 0, None, null(), 0) != 0 {
                    let result = MsgWaitForMultipleObjectsEx(
                        1,
                        &timer,
                        INFINITE,
                        QS_ALLINPUT,
                        MWMO_INPUTAVAILABLE,
                    );
                    if result != WAIT_FAILED {
                        pump_messages();
                        continue;
                    }
                }
            }
            // Unavailable timers or a failed native wait retain the original safe path.
            timer = null_mut();
        }
        pump_wait(remaining.as_millis().clamp(1, 50) as u32);
    }
}
pub fn modifiers_released(timeout: Duration) -> Result<(), String> {
    #[cfg(feature = "hotkey-experiments")]
    if crate::hotkey_experiments::selected() != crate::hotkey_experiments::Variant::Baseline {
        return crate::hotkey_experiments::modifiers_released(timeout);
    }
    let start = Instant::now();
    loop {
        let held = unsafe {
            [VK_CONTROL, VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN]
                .iter()
                .any(|v| GetAsyncKeyState(*v as i32) < 0)
        };
        if !held {
            return Ok(());
        }
        if start.elapsed() >= timeout {
            return Err("Release the hotkey keys before copying.".into());
        }
        pump_wait(5);
    }
}
pub fn chord(vk: u16) -> Result<(), String> {
    unsafe {
        let control_held = GetAsyncKeyState(VK_CONTROL as i32) < 0;
        let mut inputs = Vec::new();
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
        if !control_held {
            inputs.push(key(VK_CONTROL, 0));
        }
        inputs.push(key(vk, 0));
        inputs.push(key(vk, KEYEVENTF_KEYUP));
        if !control_held {
            inputs.push(key(VK_CONTROL, KEYEVENTF_KEYUP));
        }
        let inserted = SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            size_of::<INPUT>() as i32,
        );
        if inserted != inputs.len() as u32 {
            let cleanup = [key(vk, KEYEVENTF_KEYUP), key(VK_CONTROL, KEYEVENTF_KEYUP)];
            SendInput(
                if control_held { 1 } else { 2 },
                cleanup.as_ptr(),
                size_of::<INPUT>() as i32,
            );
            return Err(format!(
                "Keyboard input was blocked or partly inserted ({inserted}/{}); {}",
                inputs.len(),
                error()
            ));
        }
        Ok(())
    }
}
pub fn capture(hwnd: HWND, timings: &mut CaptureTimings) -> Result<Clipboard, String> {
    measure(&mut timings.modifiers_ms, || {
        modifiers_released(Duration::from_millis(1200))
    })?;
    for _ in 0..2 {
        let before = unsafe { GetClipboardSequenceNumber() };
        timings.copy_attempts += 1;
        measure(&mut timings.copy_input_ms, || chord(0x43))?;
        let start = Instant::now();
        while start.elapsed() < Duration::from_millis(700) {
            if unsafe { GetClipboardSequenceNumber() } != before
                && let Ok(data) = {
                    timings.read_attempts += 1;
                    measure(&mut timings.read_ms, || {
                        read_measured(hwnd, Duration::from_millis(320), &mut timings.reads)
                    })
                }
                && !data.text.trim().is_empty()
            {
                return Ok(data);
            }
            measure(&mut timings.wait_ms, || pump_wait(25));
        }
    }
    Err("The selected app did not copy any text.".into())
}
pub struct WorkerWindow(pub HWND);
impl WorkerWindow {
    pub fn new() -> Result<Self, String> {
        unsafe {
            let instance = GetModuleHandleW(null());
            let class = wide("USC.Rust.ClipboardWorker");
            let wc = WNDCLASSW {
                lpfnWndProc: Some(DefWindowProcW),
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
            Ok(Self(hwnd))
        }
    }
}
impl Drop for WorkerWindow {
    fn drop(&mut self) {
        unsafe {
            RemoveClipboardFormatListener(self.0);
            DestroyWindow(self.0);
        }
    }
}
// Own a snapshot rather than retaining OLE's potentially forwarding IDataObject.
struct SavedFormat {
    format: u32,
    handle: HANDLE,
}
impl Drop for SavedFormat {
    fn drop(&mut self) {
        unsafe {
            if self.handle.is_null() {
                return;
            }
            match self.format {
                2 | 9 | 0x82 => {
                    windows_sys::Win32::Graphics::Gdi::DeleteObject(self.handle);
                }
                14 | 0x8e => {
                    windows_sys::Win32::Graphics::Gdi::DeleteEnhMetaFile(self.handle);
                }
                3 | 0x83 => {
                    let data = GlobalLock(self.handle);
                    if !data.is_null() {
                        let meta = &*(data
                            as *const windows_sys::Win32::System::DataExchange::METAFILEPICT);
                        windows_sys::Win32::Graphics::Gdi::DeleteMetaFile(meta.hMF);
                        GlobalUnlock(self.handle);
                    }
                    GlobalFree(self.handle);
                }
                _ => {
                    GlobalFree(self.handle);
                }
            }
        }
    }
}
pub struct Backup {
    formats: Vec<SavedFormat>,
    owner: HWND,
    sequence: u32,
    armed: bool,
}
impl Backup {
    pub fn capture(owner: HWND) -> Result<Self, String> {
        let _open = open(owner, Duration::from_millis(320))?;
        let sequence = unsafe { GetClipboardSequenceNumber() };
        let mut formats = Vec::new();
        let mut format = 0;
        let mut allocated = 0usize;
        unsafe {
            loop {
                format = EnumClipboardFormats(format);
                if format == 0 {
                    break;
                }
                if formats.len() >= 128 {
                    return Err(
                        "Clipboard backup has too many formats; selection was left unchanged."
                            .into(),
                    );
                }
                if format == 0x80 || (0x200..=0x3ff).contains(&format) {
                    return Err("Clipboard uses an unsupported owner-managed format; selection was left unchanged.".into());
                }
                let source = GetClipboardData(format);
                if source.is_null() {
                    return Err("Clipboard backup could not render a source format; selection was left unchanged.".into());
                }
                if !matches!(format, 2 | 3 | 9 | 14 | 0x82 | 0x83 | 0x8e) {
                    allocated = allocated.saturating_add(GlobalSize(source));
                    if allocated > 64 * 1024 * 1024 {
                        return Err(
                            "Clipboard backup exceeds 64 MiB; selection was left unchanged.".into(),
                        );
                    }
                }
                let handle = if matches!(format, 14 | 0x8e) {
                    windows_sys::Win32::Graphics::Gdi::CopyEnhMetaFileW(source, null())
                } else {
                    OleDuplicateData(source, format as u16, GMEM_MOVEABLE)
                };
                if handle.is_null() {
                    return Err(
                        "Clipboard backup could not be duplicated; selection was left unchanged."
                            .into(),
                    );
                }
                formats.push(SavedFormat { format, handle });
            }
        }
        Ok(Self {
            formats,
            owner,
            sequence,
            armed: true,
        })
    }
    pub fn disarm(&mut self) {
        self.armed = false;
    }
    pub fn restore(&mut self) -> bool {
        self.armed = false;
        if unsafe { GetClipboardSequenceNumber() } == self.sequence {
            return true;
        }
        let Ok(_open) = open(self.owner, Duration::from_millis(320)) else {
            return false;
        };
        if unsafe { EmptyClipboard() } == 0 {
            return false;
        }
        let mut success = true;
        for format in &mut self.formats {
            unsafe {
                if SetClipboardData(format.format, format.handle).is_null() {
                    success = false;
                } else {
                    format.handle = null_mut();
                }
            }
        }
        success
    }
}
impl Drop for Backup {
    fn drop(&mut self) {
        if self.armed {
            self.restore();
        }
    }
}
pub fn initialize_sta() -> Result<(), String> {
    unsafe {
        if OleInitialize(null()) < 0 {
            return Err("Cannot initialize clipboard apartment".into());
        }
    }
    Ok(())
}
pub fn foreground() -> (usize, u32, String) {
    unsafe {
        let hwnd = GetForegroundWindow();
        let mut pid = 0;
        GetWindowThreadProcessId(hwnd, &mut pid);
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        let mut name = String::new();
        if !handle.is_null() {
            let mut buffer = [0u16; 32768];
            let mut len = buffer.len() as u32;
            if QueryFullProcessImageNameW(handle, 0, buffer.as_mut_ptr(), &mut len) != 0 {
                name = std::path::Path::new(&String::from_utf16_lossy(&buffer[..len as usize]))
                    .file_stem()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into_owned();
            }
            CloseHandle(handle);
        }
        (hwnd as usize, pid, name)
    }
}

#[cfg(test)]
mod ancillary_format_tests {
    use super::optional_format;
    #[test]
    fn ancillary_failures_preserve_plain_fallback_and_report_the_reason() {
        let (bytes, status) = optional_format(Err("Clipboard format exceeds size limit".into()));
        assert!(bytes.is_empty());
        assert_eq!(status, "too_large");
        let (bytes, status) = optional_format(Err("Producer failed to render format".into()));
        assert!(bytes.is_empty());
        assert_eq!(status, "read_failed");
        assert_eq!(optional_format(Ok(Vec::new())).1, "absent");
        assert_eq!(
            optional_format(Ok(b"usable HTML".to_vec())),
            (b"usable HTML".to_vec(), "ok")
        );
    }
}

#[cfg(test)]
mod settlement_tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    static MESSAGES: AtomicU32 = AtomicU32::new(0);

    unsafe extern "system" fn tick(_: HWND, _: u32, _: usize, _: u32) {
        MESSAGES.fetch_add(1, Ordering::Relaxed);
    }

    #[test]
    fn settlement_preserves_minimum_and_pumps_messages() {
        let id = unsafe { SetTimer(null_mut(), 0, 10, Some(tick)) };
        assert_ne!(id, 0);
        for _ in 0..3 {
            let start = Instant::now();
            settle_publication();
            assert!(start.elapsed() >= Duration::from_millis(50));
        }
        unsafe { KillTimer(null_mut(), id) };
        assert!(MESSAGES.load(Ordering::Relaxed) > 0);
    }

    #[test]
    fn unavailable_timer_retains_minimum() {
        let start = Instant::now();
        settle_until(start + Duration::from_millis(50), null_mut());
        assert!(start.elapsed() >= Duration::from_millis(50));
    }
}
