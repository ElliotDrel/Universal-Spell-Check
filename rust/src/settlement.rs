//! Benchmark-only settlement candidates. Ordinary app entry points always select Baseline.
use crate::clipboard;
use serde::Serialize;
use std::{
    ptr::null,
    time::{Duration, Instant},
};
use windows_sys::Win32::{Foundation::*, System::Threading::*, UI::WindowsAndMessaging::*};

const MINIMUM: Duration = Duration::from_millis(50);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Variant {
    Baseline,
    PublicationDeadline,
    OverlapDiagnostics,
    ReuseTimer,
}
impl Variant {
    pub fn parse(value: Option<&str>) -> Result<Self, String> {
        match value.unwrap_or("baseline") {
            "baseline" => Ok(Self::Baseline),
            "publication-deadline" => Ok(Self::PublicationDeadline),
            "overlap-diagnostics" => Ok(Self::OverlapDiagnostics),
            "reuse-timer" => Ok(Self::ReuseTimer),
            _ => Err("Unknown --settlement-variant: use baseline, publication-deadline, overlap-diagnostics, or reuse-timer".into()),
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::PublicationDeadline => "publication-deadline",
            Self::OverlapDiagnostics => "overlap-diagnostics",
            Self::ReuseTimer => "reuse-timer",
        }
    }
    pub fn defers_diagnostics(self) -> bool {
        self == Self::OverlapDiagnostics
    }
}

struct Timer(HANDLE);
impl Timer {
    fn new() -> Self {
        Self(unsafe {
            CreateWaitableTimerExW(
                null(),
                null(),
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_MODIFY_STATE | SYNCHRONIZATION_SYNCHRONIZE,
            )
        })
    }
}
impl Drop for Timer {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                CancelWaitableTimer(self.0);
                CloseHandle(self.0);
            }
        }
    }
}

pub struct Settlement {
    variant: Variant,
    timer: Option<Timer>,
}
impl Settlement {
    pub fn new(variant: Variant) -> Self {
        Self {
            variant,
            timer: (variant == Variant::ReuseTimer).then(Timer::new),
        }
    }
    pub fn variant(&self) -> Variant {
        self.variant
    }
    pub fn wait(&mut self, published: Instant) -> Option<WaitReport> {
        if self.variant == Variant::Baseline {
            clipboard::settle_publication();
            return None;
        }
        // Reuse isolates handle lifetime only: readback still precedes a fresh 50 ms wait.
        let origin = if self.variant == Variant::ReuseTimer {
            Instant::now()
        } else {
            published
        };
        let fresh;
        let timer = if let Some(timer) = &self.timer {
            timer
        } else {
            fresh = Timer::new();
            &fresh
        };
        let mut native = NativeWait {
            origin,
            timer: timer.0,
        };
        Some(wait_deadline(&mut native, MINIMUM))
    }
}

#[derive(Default, Debug, Serialize)]
pub struct WaitReport {
    pub wakes: u32,
    pub timer_failures: u32,
    pub fallback_waits: u32,
}
trait WaitBackend {
    fn elapsed(&self) -> Duration;
    fn timer_wait(&mut self, remaining: Duration) -> bool;
    fn fallback_wait(&mut self, remaining: Duration);
    fn pump(&mut self);
}
fn wait_deadline(backend: &mut impl WaitBackend, deadline: Duration) -> WaitReport {
    let mut report = WaitReport::default();
    let mut timer_usable = true;
    while let Some(remaining) = deadline
        .checked_sub(backend.elapsed())
        .filter(|d| !d.is_zero())
    {
        if timer_usable {
            if backend.timer_wait(remaining) {
                report.wakes += 1;
                backend.pump();
                continue;
            }
            report.timer_failures += 1;
            timer_usable = false;
        }
        report.fallback_waits += 1;
        backend.fallback_wait(remaining);
        backend.pump();
    }
    report
}
struct NativeWait {
    origin: Instant,
    timer: HANDLE,
}
impl WaitBackend for NativeWait {
    fn elapsed(&self) -> Duration {
        self.origin.elapsed()
    }
    fn timer_wait(&mut self, remaining: Duration) -> bool {
        if self.timer.is_null() {
            return false;
        }
        let due = -(remaining.as_nanos().div_ceil(100).max(1) as i64);
        unsafe {
            SetWaitableTimer(self.timer, &due, 0, None, null(), 0) != 0
                && MsgWaitForMultipleObjectsEx(
                    1,
                    &self.timer,
                    INFINITE,
                    QS_ALLINPUT,
                    MWMO_INPUTAVAILABLE,
                ) != WAIT_FAILED
        }
    }
    fn fallback_wait(&mut self, remaining: Duration) {
        unsafe {
            MsgWaitForMultipleObjectsEx(
                0,
                null(),
                remaining.as_millis().clamp(1, 50) as u32,
                QS_ALLINPUT,
                MWMO_INPUTAVAILABLE,
            );
        }
    }
    fn pump(&mut self) {
        clipboard::pump_messages();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr::null_mut;
    struct Fake {
        elapsed: Duration,
        early_wakes: usize,
        failed: bool,
        pumps: usize,
    }
    impl WaitBackend for Fake {
        fn elapsed(&self) -> Duration {
            self.elapsed
        }
        fn timer_wait(&mut self, remaining: Duration) -> bool {
            if self.failed {
                return false;
            }
            if self.early_wakes > 0 {
                self.early_wakes -= 1;
                self.elapsed += Duration::from_micros(100);
            } else {
                self.elapsed += remaining;
            }
            true
        }
        fn fallback_wait(&mut self, remaining: Duration) {
            self.elapsed += remaining.min(Duration::from_millis(5));
        }
        fn pump(&mut self) {
            self.pumps += 1;
        }
    }
    #[test]
    fn overlap_work_and_early_wakes_cannot_shorten_publication_interval() {
        for work in [0, 1, 20, 49, 60] {
            for early_wakes in [0, 3] {
                for failed in [false, true] {
                    let mut fake = Fake {
                        elapsed: Duration::from_millis(work),
                        early_wakes,
                        failed,
                        pumps: 0,
                    };
                    let report = wait_deadline(&mut fake, MINIMUM);
                    assert_eq!(fake.elapsed, Duration::from_millis(work.max(50)));
                    assert_eq!(fake.pumps, (report.wakes + report.fallback_waits) as usize);
                    assert_eq!(report.timer_failures, u32::from(failed && work < 50));
                }
            }
        }
    }
    #[test]
    fn selectors_are_independent_and_unknown_values_fail_closed() {
        for name in [
            "baseline",
            "publication-deadline",
            "overlap-diagnostics",
            "reuse-timer",
        ] {
            let v = Variant::parse(Some(name)).unwrap();
            assert_eq!(v.name(), name);
            assert_eq!(v.defers_diagnostics(), name == "overlap-diagnostics");
        }
        assert_eq!(Variant::parse(None).unwrap(), Variant::Baseline);
        assert!(Variant::parse(Some("combined")).is_err());
    }
    #[test]
    fn native_all_candidates_keep_publication_deadline_after_work() {
        for variant in [
            Variant::Baseline,
            Variant::PublicationDeadline,
            Variant::OverlapDiagnostics,
            Variant::ReuseTimer,
        ] {
            let mut settlement = Settlement::new(variant);
            for work in [0, 20, 60] {
                let published = Instant::now();
                std::thread::sleep(Duration::from_millis(work));
                let wait_started = Instant::now();
                let _ = settlement.wait(published);
                assert!(published.elapsed() >= MINIMUM, "{}", variant.name());
                if matches!(variant, Variant::Baseline | Variant::ReuseTimer) {
                    assert!(wait_started.elapsed() >= MINIMUM, "{}", variant.name());
                }
            }
        }
    }
    #[test]
    fn native_reuse_rearms_and_drops_handle() {
        unsafe fn handles() -> u32 {
            let mut count = 0;
            assert_ne!(
                unsafe { GetProcessHandleCount(GetCurrentProcess(), &mut count) },
                0
            );
            count
        }
        let mut warmup = Settlement::new(Variant::ReuseTimer);
        let _ = warmup.wait(Instant::now());
        drop(warmup);
        let before = unsafe { handles() };
        let mut settlement = Settlement::new(Variant::ReuseTimer);
        assert!(
            !settlement.timer.as_ref().unwrap().0.is_null(),
            "high resolution timer unavailable"
        );
        let retained = settlement.timer.as_ref().unwrap().0;
        for _ in 0..6 {
            let start = Instant::now();
            let report = settlement.wait(start).unwrap();
            assert!(start.elapsed() >= MINIMUM);
            assert_eq!(report.timer_failures, 0);
            assert_eq!(settlement.timer.as_ref().unwrap().0, retained);
        }
        drop(settlement);
        assert!(unsafe { handles() } <= before + 1);
    }
    #[test]
    fn native_missing_invalid_and_unwaitable_timers_fall_back() {
        // NULL models creation failure, INVALID_HANDLE_VALUE models arm failure,
        // a timer duplicated without SYNCHRONIZE models wait failure after successful arm.
        let timer = Timer::new();
        assert!(!timer.0.is_null());
        let mut duplicate = null_mut();
        assert_ne!(
            unsafe {
                DuplicateHandle(
                    GetCurrentProcess(),
                    timer.0,
                    GetCurrentProcess(),
                    &mut duplicate,
                    TIMER_MODIFY_STATE,
                    0,
                    0,
                )
            },
            0
        );
        let modify_only = Timer(duplicate);
        for handle in [null_mut(), INVALID_HANDLE_VALUE, modify_only.0] {
            let origin = Instant::now();
            let report = wait_deadline(
                &mut NativeWait {
                    origin,
                    timer: handle,
                },
                MINIMUM,
            );
            assert!(origin.elapsed() >= MINIMUM);
            assert_eq!(report.timer_failures, 1);
            assert!(report.fallback_waits > 0);
        }
    }
    #[test]
    fn native_wait_pumps_messages() {
        use std::sync::atomic::{AtomicUsize, Ordering};
        static PUMPED: AtomicUsize = AtomicUsize::new(0);
        unsafe extern "system" fn tick(_: HWND, _: u32, _: usize, _: u32) {
            PUMPED.fetch_add(1, Ordering::Relaxed);
        }
        let id = unsafe { SetTimer(null_mut(), 0, 1, Some(tick)) };
        assert_ne!(id, 0);
        let mut settlement = Settlement::new(Variant::PublicationDeadline);
        let start = Instant::now();
        let _ = settlement.wait(start);
        unsafe {
            KillTimer(null_mut(), id);
        }
        assert!(start.elapsed() >= MINIMUM);
        assert!(PUMPED.load(Ordering::Relaxed) > 0);
    }
}
