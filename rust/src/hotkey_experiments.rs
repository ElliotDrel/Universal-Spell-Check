//! Benchmark-only release waiters. Notifications are hints, never key state.
use crate::{clipboard, store::wide};
use std::{
    cell::{Cell, RefCell},
    mem::{size_of, zeroed},
    ptr::{null, null_mut},
    sync::atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::*,
    System::{LibraryLoader::*, Threading::*},
    UI::{Input::KeyboardAndMouse::*, Input::*, WindowsAndMessaging::*},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Variant {
    Baseline,
    Poll1ms,
    Hook,
    RawInput,
}
impl Variant {
    pub fn name(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::Poll1ms => "poll-1ms",
            Self::Hook => "hook",
            Self::RawInput => "raw-input",
        }
    }
    fn parse(name: &str) -> Result<Self, String> {
        match name {
            "baseline" => Ok(Self::Baseline),
            "poll-1ms" => Ok(Self::Poll1ms),
            "hook" => Ok(Self::Hook),
            "raw-input" => Ok(Self::RawInput),
            _ => Err("--hotkey-release requires baseline|poll-1ms|hook|raw-input".into()),
        }
    }
    fn interval(self) -> Duration {
        Duration::from_millis(if self == Self::Poll1ms { 1 } else { 5 })
    }
}
static SELECTED: AtomicU8 = AtomicU8::new(0);
static OBSERVER_FAILURES: AtomicU64 = AtomicU64::new(0);
static TIMER_FAILURES: AtomicU64 = AtomicU64::new(0);
static WAIT_COUNT: AtomicU64 = AtomicU64::new(0);
static HOOK_WAKES: AtomicU64 = AtomicU64::new(0);
static RAW_WAKES: AtomicU64 = AtomicU64::new(0);
static RAW_OWNER: AtomicBool = AtomicBool::new(false);
thread_local! {
    static RESOURCES: RefCell<Option<Native>> = const { RefCell::new(None) };
    static HOOK_EVENT: Cell<HANDLE> = const { Cell::new(null_mut()) };
}

// Called only by the desktop benchmark, before its worker starts. The ordinary app
// cannot select a variant, even in a feature-enabled build.
pub fn configure(args: &[String]) -> Result<&'static str, String> {
    let value = match args.iter().position(|a| a == "--hotkey-release") {
        Some(i) => Variant::parse(args.get(i + 1).ok_or("Missing --hotkey-release value")?)?,
        None => Variant::Baseline,
    };
    SELECTED.store(value as u8, Ordering::Relaxed);
    Ok(value.name())
}
pub fn selected() -> Variant {
    match SELECTED.load(Ordering::Relaxed) {
        1 => Variant::Poll1ms,
        2 => Variant::Hook,
        3 => Variant::RawInput,
        _ => Variant::Baseline,
    }
}

trait Backend {
    fn elapsed(&self) -> Duration;
    fn held(&mut self) -> bool;
    fn wait(&mut self, remaining: Duration);
}
fn release<B: Backend>(backend: &mut B, timeout: Duration) -> Result<(), String> {
    loop {
        // Always re-read every modifier after pumping; an early hook/key-up, a
        // stale event, or a repress must never authorize copy/paste.
        if !backend.held() {
            return Ok(());
        }
        let Some(remaining) = timeout
            .checked_sub(backend.elapsed())
            .filter(|r| !r.is_zero())
        else {
            return Err("Release the hotkey keys before copying.".into());
        };
        backend.wait(remaining);
    }
}
const MODIFIERS: [u16; 11] = [
    VK_CONTROL,
    VK_LCONTROL,
    VK_RCONTROL,
    VK_MENU,
    VK_LMENU,
    VK_RMENU,
    VK_SHIFT,
    VK_LSHIFT,
    VK_RSHIFT,
    VK_LWIN,
    VK_RWIN,
];
fn any_held(mut state: impl FnMut(u16) -> bool) -> bool {
    MODIFIERS.into_iter().any(&mut state)
}

pub fn modifiers_released(timeout: Duration) -> Result<(), String> {
    let variant = selected();
    debug_assert_ne!(variant, Variant::Baseline);
    if !any_held(|vk| unsafe { GetAsyncKeyState(vk as i32) < 0 }) {
        return Ok(());
    }
    let start = Instant::now();
    RESOURCES.with(|cell| {
        let mut resources = cell.borrow_mut();
        let native = resources.get_or_insert_with(|| Native::new(variant));
        release(
            &mut Active {
                native,
                start,
                observer: Observer::new(variant),
            },
            timeout,
        )
    })
}
struct Active<'a> {
    native: &'a mut Native,
    start: Instant,
    observer: Observer,
}
impl Backend for Active<'_> {
    fn elapsed(&self) -> Duration {
        self.start.elapsed()
    }
    fn held(&mut self) -> bool {
        any_held(|vk| unsafe { GetAsyncKeyState(vk as i32) < 0 })
    }
    fn wait(&mut self, remaining: Duration) {
        self.native.wait(remaining, &self.observer);
    }
}

struct Native {
    timer: HANDLE,
    variant: Variant,
}
impl Native {
    fn new(variant: Variant) -> Self {
        let timer = unsafe {
            CreateWaitableTimerExW(
                null(),
                null(),
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_MODIFY_STATE | SYNCHRONIZATION_SYNCHRONIZE,
            )
        };
        if timer.is_null() {
            TIMER_FAILURES.fetch_add(1, Ordering::Relaxed);
        }
        Self { timer, variant }
    }
    fn wait(&mut self, remaining: Duration, observer: &Observer) {
        WAIT_COUNT.fetch_add(1, Ordering::Relaxed);
        let interval = remaining.min(self.variant.interval());
        wait_step(
            &mut NativeWait {
                native: self,
                observer,
            },
            interval,
        );
    }
}
// The same ordering/failure driver runs against native handles and scripted waits.
trait WaitBackend {
    fn available(&self) -> bool;
    fn arm(&mut self, interval: Duration) -> bool;
    fn signal_or_message(&mut self, interval: Duration) -> bool;
    fn disable(&mut self);
    fn pump(&mut self);
    fn fallback(&mut self, interval: Duration);
}
fn wait_step(backend: &mut impl WaitBackend, interval: Duration) {
    if backend.available() {
        if backend.arm(interval) && backend.signal_or_message(interval) {
            backend.pump();
            return;
        }
        backend.disable();
    }
    backend.fallback(interval);
}
struct NativeWait<'a> {
    native: &'a mut Native,
    observer: &'a Observer,
}
impl WaitBackend for NativeWait<'_> {
    fn available(&self) -> bool {
        !self.native.timer.is_null()
    }
    fn arm(&mut self, interval: Duration) -> bool {
        let due = -(interval.as_nanos().div_ceil(100).max(1) as i64);
        unsafe { SetWaitableTimer(self.native.timer, &due, 0, None, null(), 0) != 0 }
    }
    fn signal_or_message(&mut self, interval: Duration) -> bool {
        let handles = [self.native.timer, self.observer.event];
        let count = if !self.observer.hook.is_null() { 2 } else { 1 };
        // Finite watchdog even if a timer or observer notification is lost.
        unsafe {
            MsgWaitForMultipleObjectsEx(
                count,
                handles.as_ptr(),
                interval.as_millis().max(1) as u32 + 1,
                QS_ALLINPUT,
                MWMO_INPUTAVAILABLE,
            ) != WAIT_FAILED
        }
    }
    fn disable(&mut self) {
        unsafe {
            CancelWaitableTimer(self.native.timer);
            CloseHandle(self.native.timer);
        }
        self.native.timer = null_mut();
        TIMER_FAILURES.fetch_add(1, Ordering::Relaxed);
    }
    fn pump(&mut self) {
        clipboard::pump_messages();
    }
    fn fallback(&mut self, interval: Duration) {
        clipboard::pump_wait(interval.as_millis().max(1) as u32);
    }
}

impl Drop for Native {
    fn drop(&mut self) {
        unsafe {
            if !self.timer.is_null() {
                CancelWaitableTimer(self.timer);
                CloseHandle(self.timer);
            }
        }
    }
}
struct Observer {
    event: HANDLE,
    hook: HHOOK,
    window: HWND,
}
impl Observer {
    fn new(variant: Variant) -> Self {
        let mut observer = Self {
            event: null_mut(),
            hook: null_mut(),
            window: null_mut(),
        };
        if variant == Variant::Hook {
            observer.event = unsafe { CreateEventW(null(), 0, 0, null()) };
            if !observer.event.is_null() {
                HOOK_EVENT.set(observer.event);
                observer.hook = unsafe {
                    SetWindowsHookExW(
                        WH_KEYBOARD_LL,
                        Some(keyboard_hook),
                        GetModuleHandleW(null()),
                        0,
                    )
                };
                if observer.hook.is_null() {
                    HOOK_EVENT.set(null_mut());
                }
            }
            if observer.hook.is_null() {
                OBSERVER_FAILURES.fetch_add(1, Ordering::Relaxed);
            }
        } else if variant == Variant::RawInput {
            if RAW_OWNER
                .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            {
                observer.window = raw_window();
                if observer.window.is_null() {
                    RAW_OWNER.store(false, Ordering::Release);
                }
            }
            if observer.window.is_null() {
                OBSERVER_FAILURES.fetch_add(1, Ordering::Relaxed);
            }
        }
        observer
    }
}
impl Drop for Observer {
    fn drop(&mut self) {
        unsafe {
            if !self.hook.is_null() {
                UnhookWindowsHookEx(self.hook);
                HOOK_EVENT.set(null_mut());
            }
            if !self.window.is_null() {
                let remove = RAWINPUTDEVICE {
                    usUsagePage: 1,
                    usUsage: 6,
                    dwFlags: RIDEV_REMOVE,
                    hwndTarget: null_mut(),
                };
                RegisterRawInputDevices(&remove, 1, size_of::<RAWINPUTDEVICE>() as u32);
                DestroyWindow(self.window);
                RAW_OWNER.store(false, Ordering::Release);
            }
            if !self.event.is_null() {
                CloseHandle(self.event);
            }
        }
    }
}
pub fn diagnostics() -> serde_json::Value {
    serde_json::json!({"variant":selected().name(), "wait_iterations":WAIT_COUNT.load(Ordering::Relaxed), "observer_failures":OBSERVER_FAILURES.load(Ordering::Relaxed), "hook_notifications":HOOK_WAKES.load(Ordering::Relaxed), "raw_input_notifications":RAW_WAKES.load(Ordering::Relaxed), "timer_failures":TIMER_FAILURES.load(Ordering::Relaxed)})
}
unsafe extern "system" fn keyboard_hook(code: i32, w: WPARAM, l: LPARAM) -> LRESULT {
    if code >= 0 {
        HOOK_WAKES.fetch_add(1, Ordering::Relaxed);
        HOOK_EVENT.with(|event| {
            let handle = event.get();
            if !handle.is_null() {
                unsafe {
                    SetEvent(handle);
                }
            }
        });
    }
    // This callback precedes async-state updates. It only wakes the outer loop.
    unsafe { CallNextHookEx(null_mut(), code, w, l) }
}
unsafe extern "system" fn raw_proc(hwnd: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    if message == WM_INPUT {
        RAW_WAKES.fetch_add(1, Ordering::Relaxed);
    }
    // Dispatch itself is the wake; no key state is derived from raw make/break.
    // DefWindowProc also performs required foreground WM_INPUT cleanup.
    unsafe { DefWindowProcW(hwnd, message, w, l) }
}
fn raw_window() -> HWND {
    unsafe {
        // Refuse to replace a keyboard registration owned by any other component.
        let mut count = 0;
        if GetRegisteredRawInputDevices(null_mut(), &mut count, size_of::<RAWINPUTDEVICE>() as u32)
            == u32::MAX
        {
            return null_mut();
        }
        let mut devices = vec![zeroed::<RAWINPUTDEVICE>(); count as usize];
        if count > 0 {
            let read = GetRegisteredRawInputDevices(
                devices.as_mut_ptr(),
                &mut count,
                size_of::<RAWINPUTDEVICE>() as u32,
            );
            if read == u32::MAX
                || devices
                    .iter()
                    .take(read as usize)
                    .any(|d| d.usUsagePage == 1 && d.usUsage == 6)
            {
                return null_mut();
            }
        }
        let class = wide("USCExperimentalRawRelease");
        let instance = GetModuleHandleW(null());
        let wc = WNDCLASSW {
            lpfnWndProc: Some(raw_proc),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            ..zeroed()
        };
        if RegisterClassW(&wc) == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS {
            return null_mut();
        }
        let window = CreateWindowExW(
            0,
            class.as_ptr(),
            null(),
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
        if window.is_null() {
            return window;
        }
        let device = RAWINPUTDEVICE {
            usUsagePage: 1,
            usUsage: 6,
            dwFlags: RIDEV_INPUTSINK,
            hwndTarget: window,
        };
        if RegisterRawInputDevices(&device, 1, size_of::<RAWINPUTDEVICE>() as u32) == 0 {
            DestroyWindow(window);
            return null_mut();
        }
        window
    }
}

/// Bounded physical-input probe. It registers only the canonical benchmark chord,
/// and never creates settings, reads credentials, changes focus or sends input.
pub fn probe(args: &[String]) -> Result<(), String> {
    use crate::{benchmark::option, channel};
    use std::io::Write;
    if !args.iter().any(|a| a == "--allow-desktop") {
        return Err(
            "Physical hotkey probe requires --allow-desktop and an exclusive idle desktop window"
                .into(),
        );
    }
    let variant = configure(args)?;
    let seconds: u64 = option(args, "--seconds")
        .unwrap_or_else(|| "15".into())
        .parse()
        .map_err(|_| "Invalid --seconds")?;
    if !(1..=300).contains(&seconds) {
        return Err("--seconds must be 1..300".into());
    }
    let output =
        option(args, "--output").ok_or("Physical probe requires a fresh --output JSONL file")?;
    let mut file = std::fs::OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(output)
        .map_err(|e| e.to_string())?;
    let write = |file: &mut std::fs::File, value: serde_json::Value| -> Result<(), String> {
        writeln!(file, "{value}")
            .and_then(|_| file.flush())
            .map_err(|e| e.to_string())
    };
    unsafe {
        if RegisterHotKey(
            null_mut(),
            1,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            channel::BENCH_VK,
        ) == 0
        {
            return Err(format!(
                "Benchmark chord unavailable: {}",
                clipboard::error()
            ));
        }
    }
    struct Registration;
    impl Drop for Registration {
        fn drop(&mut self) {
            unsafe {
                UnregisterHotKey(null_mut(), 1);
            }
        }
    }
    let _registration = Registration;
    write(
        &mut file,
        serde_json::json!({"event":"ready", "variant":variant,"seconds":seconds,"hotkey_vk":channel::BENCH_VK,"trigger":"physical_registered_hotkey", "boundary":"WM_HOTKEY dispatch to authoritative all-modifiers-released; includes human hold, excludes physical delivery before dispatch and editor replacement", "app_version":channel::VERSION}),
    )?;
    let until = Instant::now() + Duration::from_secs(seconds);
    while Instant::now() < until {
        unsafe {
            MsgWaitForMultipleObjectsEx(0, null(), 50, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        }
        let mut msg = unsafe { zeroed::<MSG>() };
        while Instant::now() < until
            && unsafe { PeekMessageW(&mut msg, null_mut(), 0, 0, PM_REMOVE) } != 0
        {
            if msg.message == WM_QUIT {
                return Ok(());
            }
            if msg.message == WM_HOTKEY && msg.wParam == 1 {
                let start = Instant::now();
                let result = clipboard::modifiers_released(Duration::from_millis(1200));
                write(
                    &mut file,
                    serde_json::json!({"event":"release", "variant":variant, "elapsed_ms":start.elapsed().as_secs_f64()*1000.0,"success":result.is_ok(),"error":result.err(),"diagnostics":diagnostics()}),
                )?;
            } else {
                unsafe {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }
        }
    }
    write(
        &mut file,
        serde_json::json!({"event":"finished", "diagnostics":diagnostics()}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Script {
        elapsed: Duration,
        states: Vec<bool>,
        index: usize,
        waits: Vec<Duration>,
        advances: Vec<Duration>,
    }
    impl Backend for Script {
        fn elapsed(&self) -> Duration {
            self.elapsed
        }
        fn held(&mut self) -> bool {
            self.states[self.index.min(self.states.len() - 1)]
        }
        fn wait(&mut self, remaining: Duration) {
            self.waits.push(remaining);
            self.elapsed += self
                .advances
                .get(self.index)
                .copied()
                .unwrap_or(Duration::from_millis(5));
            self.index += 1;
        }
    }
    fn script(states: &[bool], advances: &[u64]) -> Script {
        Script {
            elapsed: Duration::ZERO,
            states: states.to_vec(),
            index: 0,
            waits: vec![],
            advances: advances
                .iter()
                .map(|ms| Duration::from_millis(*ms))
                .collect(),
        }
    }
    #[test]
    fn already_released_never_waits() {
        let mut s = script(&[false], &[]);
        assert!(release(&mut s, Duration::from_millis(1200)).is_ok());
        assert!(s.waits.is_empty());
    }
    #[test]
    fn early_hook_wake_and_repress_are_only_hints() {
        let mut s = script(&[true, true, true, false], &[0, 1, 5]);
        assert!(release(&mut s, Duration::from_millis(1200)).is_ok());
        assert_eq!(s.waits.len(), 3);
    }
    #[test]
    fn missed_notification_and_observer_failure_still_poll() {
        let mut s = script(&[true, true, false], &[5, 5]);
        assert!(release(&mut s, Duration::from_millis(1200)).is_ok());
        assert_eq!(s.elapsed, Duration::from_millis(10));
    }
    #[test]
    fn held_modifiers_fail_at_deadline_and_wait_is_bounded() {
        let mut s = script(&[true], &[1199, 1]);
        assert!(release(&mut s, Duration::from_millis(1200)).is_err());
        assert_eq!(
            s.waits,
            [Duration::from_millis(1200), Duration::from_millis(1)]
        );
    }
    #[test]
    fn every_left_right_modifier_is_authoritative() {
        for key in MODIFIERS {
            assert!(any_held(|vk| vk == key), "{key}");
        }
        assert!(!any_held(|_| false));
    }
    #[test]
    fn candidates_remain_independent() {
        assert_eq!(Variant::Poll1ms.interval(), Duration::from_millis(1));
        for v in [Variant::Hook, Variant::RawInput] {
            assert_eq!(v.interval(), Duration::from_millis(5));
        }
        for name in ["baseline", "poll-1ms", "hook", "raw-input"] {
            assert_eq!(Variant::parse(name).unwrap().name(), name);
        }
        assert!(Variant::parse("combined").is_err());
    }
    #[derive(Default)]
    struct WaitScript {
        available: bool,
        arm_ok: bool,
        wait_ok: bool,
        calls: Vec<&'static str>,
    }
    impl WaitBackend for WaitScript {
        fn available(&self) -> bool {
            self.available
        }
        fn arm(&mut self, _: Duration) -> bool {
            self.calls.push("arm");
            self.arm_ok
        }
        fn signal_or_message(&mut self, _: Duration) -> bool {
            self.calls.push("wait");
            self.wait_ok
        }
        fn disable(&mut self) {
            self.calls.push("disable");
            self.available = false;
        }
        fn pump(&mut self) {
            self.calls.push("pump");
        }
        fn fallback(&mut self, _: Duration) {
            self.calls.push("fallback");
        }
    }
    #[test]
    fn creation_arm_and_wait_failures_pump_fallback_without_reusing_failure() {
        for (available, arm_ok, wait_ok, expected) in [
            (false, true, true, vec!["fallback"]),
            (true, false, true, vec!["arm", "disable", "fallback"]),
            (
                true,
                true,
                false,
                vec!["arm", "wait", "disable", "fallback"],
            ),
        ] {
            let mut script = WaitScript {
                available,
                arm_ok,
                wait_ok,
                calls: vec![],
            };
            wait_step(&mut script, Duration::from_millis(1));
            assert_eq!(script.calls, expected);
            script.calls.clear();
            wait_step(&mut script, Duration::from_millis(1));
            assert_eq!(script.calls, ["fallback"]);
        }
    }
    #[test]
    fn every_successful_early_wake_pumps_before_authoritative_recheck() {
        let mut s = WaitScript {
            available: true,
            arm_ok: true,
            wait_ok: true,
            calls: vec![],
        };
        for _ in 0..2 {
            wait_step(&mut s, Duration::from_millis(1));
        }
        assert_eq!(s.calls, ["arm", "wait", "pump", "arm", "wait", "pump"]);
    }
    #[test]
    fn native_timer_reuses_handle_and_rearms_without_desktop_observer() {
        let mut native = Native::new(Variant::Poll1ms);
        assert!(
            !native.timer.is_null(),
            "High resolution timer unavailable on this Windows host"
        );
        let handle = native.timer;
        let observer = Observer {
            event: null_mut(),
            hook: null_mut(),
            window: null_mut(),
        };
        for _ in 0..16 {
            native.wait(Duration::from_millis(1), &observer);
            assert_eq!(native.timer, handle);
        }
    }
    #[test]
    fn native_arm_failure_disables_handle_and_keeps_pumping() {
        // An event is a valid kernel handle but cannot be armed as a timer.
        let mut native = Native {
            timer: unsafe { CreateEventW(null(), 0, 0, null()) },
            variant: Variant::Poll1ms,
        };
        assert!(!native.timer.is_null());
        let observer = Observer {
            event: null_mut(),
            hook: null_mut(),
            window: null_mut(),
        };
        native.wait(Duration::from_millis(1), &observer);
        assert!(native.timer.is_null());
        native.wait(Duration::from_millis(1), &observer);
    }
}
