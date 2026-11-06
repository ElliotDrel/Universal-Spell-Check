//! Explicit desktop-benchmark arms. Ordinary application startup never selects one.
use super::*;
use std::{cell::RefCell, sync::OnceLock};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Variant {
    #[default]
    Baseline,
    Combined,
    Fused,
    Prepared,
}
impl Variant {
    pub fn parse(value: &str) -> Result<Self, String> {
        match value {
            "baseline" => Ok(Self::Baseline),
            "combined" => Ok(Self::Combined),
            "fused" => Ok(Self::Fused),
            "prepared" => Ok(Self::Prepared),
            _ => Err("--history-variant requires baseline, combined, fused or prepared".into()),
        }
    }
    pub fn name(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::Combined => "combined",
            Self::Fused => "fused",
            Self::Prepared => "prepared",
        }
    }
}
static SELECTED: OnceLock<Variant> = OnceLock::new();
pub fn select(value: &str) -> Result<(), String> {
    SELECTED
        .set(Variant::parse(value)?)
        .map_err(|_| "History experiment already configured".into())
}
pub fn selected() -> Variant {
    SELECTED.get().copied().unwrap_or_default()
}
const TWO: &[&str] = &["CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];
const ONE: &[&str] = &["ExcludeClipboardContentFromMonitorProcessing"];
fn policy_names(variant: Variant) -> &'static [&'static str] {
    if variant == Variant::Combined {
        ONE
    } else {
        TWO
    }
}
fn prepare_policy<T>(
    names: &[&str],
    mut register: impl FnMut(&str) -> u32,
    mut allocate: impl FnMut(&[u8]) -> Result<T, String>,
) -> Result<Vec<(u32, T)>, String> {
    names
        .iter()
        .map(|name| {
            let id = register(name);
            if id == 0 {
                return Err("Cannot register history policy format".into());
            }
            // The combined marker accepts any nonnull data; DWORD zero also suits both legacy markers.
            Ok((id, allocate(&0u32.to_le_bytes())?))
        })
        .collect()
}
#[derive(Default)]
struct IdlePolicy {
    ids: Option<Vec<u32>>,
    buffers: Option<Vec<(u32, Buffer)>>,
}
impl IdlePolicy {
    fn replenish_buffers(&mut self) {
        self.buffers = self.ids.as_ref().and_then(|ids| {
            ids.iter()
                .map(|id| Ok((*id, Buffer::new(&0u32.to_le_bytes())?)))
                .collect::<Result<_, String>>()
                .ok()
        });
    }
}
thread_local! {
    static IDLE: RefCell<IdlePolicy> = RefCell::new(IdlePolicy::default());
}
/// Called on the correction thread before readiness, including after every run/panic.
pub fn replenish() {
    if selected() != Variant::Prepared {
        return;
    }
    IDLE.with_borrow_mut(|idle| {
        if idle.buffers.is_some() {
            return;
        }
        if idle.ids.is_none() {
            let ids: Vec<_> = TWO.iter().map(|name| format(name)).collect();
            if ids.contains(&0) {
                return;
            }
            idle.ids = Some(ids);
        }
        idle.replenish_buffers();
    });
}
fn policy(variant: Variant) -> Result<Vec<(u32, Buffer)>, String> {
    if variant == Variant::Prepared {
        let prepared = IDLE.with_borrow_mut(|idle| idle.buffers.take());
        if let Some(prepared) = prepared {
            return Ok(prepared);
        }
        // Reuse successful registrations even when idle allocation failed; handles are always fresh.
        if let Some(ids) = IDLE.with_borrow(|idle| idle.ids.clone()) {
            return ids
                .into_iter()
                .map(|id| Ok((id, Buffer::new(&0u32.to_le_bytes())?)))
                .collect();
        }
    }
    prepare_policy(policy_names(variant), format, Buffer::new)
}
fn prepare(text: &str, variant: Variant) -> Result<Vec<(u32, Buffer)>, String> {
    let unicode: Vec<u8> = text
        .encode_utf16()
        .chain(Some(0))
        .flat_map(u16::to_le_bytes)
        .collect();
    let text = Buffer::new(&unicode)?;
    let mut prepared = policy(variant)?;
    prepared.insert(0, (13, text));
    Ok(prepared)
}
// Used by the real publisher and failure-injection fixtures. Consumes every handle exactly once.
fn publish<T>(
    prepared: Vec<(u32, T)>,
    empty: impl FnOnce() -> Result<(), String>,
    mut transfer: impl FnMut(u32, T) -> Result<(), String>,
) -> Result<(), String> {
    if !prepared.iter().any(|(format, _)| *format == 13) {
        return Err("History exclusion requires Unicode content".into());
    }
    empty()?;
    for (format, memory) in prepared {
        transfer(format, memory)?;
    }
    Ok(())
}
fn publish_open(prepared: Vec<(u32, Buffer)>) -> Result<(), String> {
    publish(
        prepared,
        || {
            if unsafe { EmptyClipboard() } == 0 {
                Err(error())
            } else {
                Ok(())
            }
        },
        |format, memory| memory.transfer(format),
    )
}
pub fn exclude(hwnd: HWND, text: &str) -> bool {
    if selected() == Variant::Baseline {
        return write(hwnd, text, "", None, true).is_ok();
    }
    if hwnd.is_null() {
        return false;
    }
    let Ok(prepared) = prepare(text, selected()) else {
        return false;
    };
    let Ok(_open) = open(hwnd, Duration::from_millis(320)) else {
        return false;
    };
    publish_open(prepared).is_ok()
}
pub fn capture(
    hwnd: HWND,
    timings: &mut CaptureTimings,
) -> Result<(Clipboard, Option<bool>), String> {
    if selected() != Variant::Fused {
        return super::capture(hwnd, timings).map(|data| (data, None));
    }
    if hwnd.is_null() {
        return Err("Fused capture requires a clipboard owner window".into());
    }
    let mut excluded = None;
    let data = capture_using(hwnd, timings, |hwnd, reads| {
        let _open = measure(&mut reads.open_ms, || {
            open(hwnd, Duration::from_millis(320))
        })?;
        let (data, result) = read_and_exclude(
            || read_open(reads),
            |data| prepare(&data.text, Variant::Fused),
            publish_open,
        )?;
        excluded = result;
        Ok(data)
    })?;
    Ok((data, excluded))
}

// Reading and validation must finish before any destructive publication preparation/transfer.
fn read_and_exclude<T>(
    read: impl FnOnce() -> Result<Clipboard, String>,
    prepare: impl FnOnce(&Clipboard) -> Result<T, String>,
    publish: impl FnOnce(T) -> Result<(), String>,
) -> Result<(Clipboard, Option<bool>), String> {
    let data = read()?;
    let excluded = if data.text.trim().is_empty() {
        None
    } else {
        Some(prepare(&data).and_then(publish).is_ok())
    };
    Ok((data, excluded))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::Cell, rc::Rc};
    #[test]
    fn arms_are_independent_and_selector_is_strict() {
        assert_eq!(Variant::parse("baseline").unwrap(), Variant::Baseline);
        assert_eq!(policy_names(Variant::Combined), ONE);
        assert_eq!(policy_names(Variant::Fused), TWO);
        assert_eq!(policy_names(Variant::Prepared), TWO);
        assert!(Variant::parse("combined+fused").is_err());
    }
    #[test]
    fn real_policy_builder_prepares_exact_nonnull_payloads() {
        for variant in [Variant::Combined, Variant::Fused, Variant::Prepared] {
            let mut names = Vec::new();
            let prepared = prepare_policy(
                policy_names(variant),
                |name| {
                    names.push(name.to_owned());
                    0xc000 + names.len() as u32
                },
                |bytes| Ok(bytes.to_vec()),
            )
            .unwrap();
            assert_eq!(names, policy_names(variant));
            assert!(prepared.iter().all(|(_, bytes)| bytes == &[0, 0, 0, 0]));
            assert_eq!(
                prepared.len(),
                if variant == Variant::Combined { 1 } else { 2 }
            );
        }
    }
    struct Owned(Rc<Cell<usize>>);
    impl Drop for Owned {
        fn drop(&mut self) {
            self.0.set(self.0.get() + 1);
        }
    }
    #[test]
    fn allocation_and_registration_failure_do_not_publish_and_drop_partial_handles() {
        let drops = Rc::new(Cell::new(0));
        let mut allocation = 0;
        assert!(
            prepare_policy(
                TWO,
                |_| 0xc001,
                |_| {
                    allocation += 1;
                    if allocation == 2 {
                        Err("allocation failed".into())
                    } else {
                        Ok(Owned(drops.clone()))
                    }
                }
            )
            .is_err()
        );
        assert_eq!(drops.get(), 1);
        assert!(prepare_policy::<()>(TWO, |_| 0, |_| panic!("must not allocate")).is_err());
    }
    #[test]
    fn publication_failures_release_only_untransferred_handles() {
        for fail_at in 0..=3 {
            let drops = Rc::new(Cell::new(0));
            let handles = [13, 0xc001, 0xc002]
                .into_iter()
                .map(|id| (id, Owned(drops.clone())))
                .collect();
            let mut transferred = Vec::new();
            let mut count = 0;
            let result = publish(
                handles,
                || {
                    if fail_at == 0 {
                        Err("empty failed".into())
                    } else {
                        Ok(())
                    }
                },
                |_, handle| {
                    count += 1;
                    if count == fail_at {
                        return Err("set failed".into());
                    }
                    transferred.push(handle);
                    Ok(())
                },
            );
            assert!(result.is_err());
            assert_eq!(drops.get() + transferred.len(), 3);
            drop(transferred);
            assert_eq!(drops.get(), 3);
        }
    }
    #[test]
    fn policy_only_clipboard_is_rejected_before_empty() {
        assert!(
            publish(
                vec![(0xc001, ())],
                || panic!("must not empty"),
                |_, _| Ok(())
            )
            .is_err()
        );
        let mut order = Vec::new();
        publish(
            vec![(13, ()), (0xc001, ()), (0xc002, ())],
            || Ok(()),
            |id, _| {
                order.push(id);
                Ok(())
            },
        )
        .unwrap();
        assert_eq!(order, [13, 0xc001, 0xc002]);
    }
    #[test]
    fn fused_capture_owns_all_flavors_before_publication_and_keeps_policy_best_effort() {
        for fail in [false, true] {
            let order = RefCell::new(Vec::new());
            let (captured, excluded) = read_and_exclude(
                || {
                    order.borrow_mut().push("read_all");
                    Ok(Clipboard {
                        text: "synthetic Ω😀".into(),
                        html: "<b>synthetic</b>".into(),
                        native: vec![1, 2, 3],
                        rtf: "rtf".into(),
                        sequence: 7,
                        html_status: "ok",
                        native_status: "ok",
                        ..Default::default()
                    })
                },
                |data| {
                    assert_eq!(data.html, "<b>synthetic</b>");
                    assert_eq!(data.native, [1, 2, 3]);
                    order.borrow_mut().push("allocate_all");
                    if fail {
                        Err("allocation failed".into())
                    } else {
                        Ok(())
                    }
                },
                |_| {
                    order.borrow_mut().push("empty_and_publish");
                    Ok(())
                },
            )
            .unwrap();
            assert_eq!(captured.text, "synthetic Ω😀");
            assert_eq!(captured.sequence, 7);
            assert_eq!(excluded, Some(!fail));
            assert_eq!(
                order.into_inner(),
                if fail {
                    vec!["read_all", "allocate_all"]
                } else {
                    vec!["read_all", "allocate_all", "empty_and_publish"]
                }
            );
        }
        assert!(
            read_and_exclude::<()>(
                || Err("sequence changed".into()),
                |_| panic!("must not prepare"),
                |_| panic!("must not publish")
            )
            .is_err()
        );
        let (_, excluded) = read_and_exclude::<()>(
            || Ok(Clipboard::default()),
            |_| panic!("empty text must not prepare"),
            |_| panic!("must not publish"),
        )
        .unwrap();
        assert_eq!(excluded, None);
    }
    #[test]
    fn native_policy_allocations_are_owned_without_clipboard_access() {
        let prepared = prepare_policy(TWO, |_| 0xc001, Buffer::new).unwrap();
        assert_eq!(prepared.len(), 2);
        assert_ne!(prepared[0].1.0, prepared[1].1.0);
        for (_, buffer) in &prepared {
            unsafe {
                assert!(GlobalSize(buffer.0) >= 4);
                let data = GlobalLock(buffer.0);
                assert!(!data.is_null());
                assert_eq!(
                    std::slice::from_raw_parts(data.cast::<u8>(), 4),
                    [0, 0, 0, 0]
                );
                GlobalUnlock(buffer.0);
            }
        }
        drop(prepared);
    }
    #[test]
    fn idle_cache_consumes_handles_once_and_replenishes_fresh_with_same_ids() {
        let mut idle = IdlePolicy {
            ids: Some(vec![0xc001, 0xc002]),
            buffers: None,
        };
        idle.replenish_buffers();
        let first = idle.buffers.take().unwrap();
        assert!(idle.buffers.take().is_none());
        idle.replenish_buffers();
        let second = idle.buffers.take().unwrap();
        assert_eq!(
            first.iter().map(|(id, _)| *id).collect::<Vec<_>>(),
            second.iter().map(|(id, _)| *id).collect::<Vec<_>>()
        );
        for (_, old) in &first {
            assert!(second.iter().all(|(_, new)| old.0 != new.0));
        }
    }
}
