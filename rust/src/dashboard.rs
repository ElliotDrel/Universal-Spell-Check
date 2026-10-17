use crate::{
    activity::{self, Entry, Reader, Stats},
    clipboard,
    diff::{Kind, diff},
    platform,
    runtime::Worker,
    store::{Store, mask},
    style,
    update::{Trigger, Updater},
};
use native_windows_gui as nwg;
use std::{
    cell::{Cell, RefCell},
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, Sender},
    },
    thread,
};
use windows_sys::Win32::UI::WindowsAndMessaging::*;
const CREAM: [u8; 3] = [245, 241, 234];
fn show(handle: &nwg::ControlHandle, visible: bool) {
    if let Some(h) = handle.hwnd() {
        unsafe {
            ShowWindow(h.cast(), if visible { SW_SHOWNA } else { SW_HIDE });
        }
    }
}
fn button(
    parent: impl Into<nwg::ControlHandle>,
    text: &str,
    x: i32,
    y: i32,
    width: i32,
) -> Result<nwg::Button, nwg::NwgError> {
    let mut c = Default::default();
    nwg::Button::builder()
        .parent(parent)
        .text(text)
        .position((x, y))
        .size((width, 32))
        .build(&mut c)?;
    Ok(c)
}
fn label(
    parent: impl Into<nwg::ControlHandle>,
    text: &str,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    font: &nwg::Font,
) -> Result<nwg::Label, nwg::NwgError> {
    let mut c = Default::default();
    nwg::Label::builder()
        .parent(parent)
        .text(text)
        .position((x, y))
        .size((width, height))
        .font(Some(font))
        .background_color(Some(CREAM))
        .build(&mut c)?;
    Ok(c)
}
enum FeedRequest {
    Refresh,
    More,
    Stop,
}
struct FeedReply {
    entries: Vec<Entry>,
    stats: Option<Stats>,
    generation: u64,
    page: bool,
}
#[derive(Clone)]
pub struct Dashboard {
    hwnd: Arc<std::sync::atomic::AtomicUsize>,
    open: Arc<AtomicBool>,
    store: Store,
    worker: Worker,
    updater: Updater,
    main_window: usize,
}
impl Dashboard {
    pub fn new(store: Store, worker: Worker, updater: Updater, main_window: usize) -> Self {
        Self {
            hwnd: Arc::new(std::sync::atomic::AtomicUsize::new(0)),
            open: Arc::new(AtomicBool::new(false)),
            store,
            worker,
            updater,
            main_window,
        }
    }
    pub fn open(&self, settings: bool) {
        let existing = self.hwnd.load(Ordering::Acquire);
        if existing != 0 {
            unsafe {
                PostMessageW(existing as _, WM_APP + 13, settings as usize, 0);
                ShowWindow(existing as _, SW_RESTORE);
                SetForegroundWindow(existing as _);
            }
            return;
        }
        if self.open.swap(true, Ordering::AcqRel) {
            return;
        }
        let state = self.clone();
        thread::Builder::new()
            .name("dashboard".into())
            .spawn(move || {
                let result = run_dashboard(&state, settings);
                if let Err(e) = result {
                    eprintln!("Dashboard failed: {e}");
                }
                state.hwnd.store(0, Ordering::Release);
                state.open.store(false, Ordering::Release);
            })
            .expect("dashboard thread");
    }
    pub fn changed(&self) {
        let hwnd = self.hwnd.load(Ordering::Acquire);
        if hwnd != 0 {
            unsafe {
                PostMessageW(hwnd as _, WM_APP + 14, 0, 0);
            }
        }
    }
    pub fn close(&self) {
        let hwnd = self.hwnd.load(Ordering::Acquire);
        if hwnd != 0 {
            unsafe {
                PostMessageW(hwnd as _, WM_CLOSE, 0, 0);
            }
        }
    }
}
struct Row {
    entry: Entry,
    day: Option<nwg::Label>,
    parent: nwg::ControlHandle,
    secondary: Option<nwg::RichTextBox>,
    body_handlers: Vec<nwg::RawEventHandler>,
    metadata: nwg::Label,
    body: nwg::RichTextBox,
    copy: nwg::Button,
    timing: nwg::Button,
    original: nwg::Button,
    side: nwg::Button,
    original_shown: bool,
    side_shown: bool,
    timing_shown: bool,
    base_height: i32,
    height: i32,
    y: i32,
}
struct Ui {
    window: nwg::Window,
    home: nwg::Button,
    settings_button: nwg::Button,
    help: nwg::Button,
    title: nwg::Label,
    _brand: nwg::Label,
    _version: nwg::Label,
    stats_label: nwg::Label,
    refresh: nwg::Button,
    panel: nwg::Frame,
    feed_notice: nwg::Notice,
    feed_rx: Receiver<FeedReply>,
    feed_tx: Sender<FeedRequest>,
    rows: Vec<Row>,
    scroll: i32,
    total_height: i32,
    loading: bool,
    exhausted: bool,
    generation: u64,
    model: nwg::ComboBox<String>,
    developer: nwg::CheckBox,
    startup: nwg::CheckBox,
    keys: nwg::ComboBox<String>,
    key_ids: Vec<String>,
    key_name: nwg::TextInput,
    key_value: nwg::TextInput,
    add_key: nwg::Button,
    select_key: nwg::Button,
    remove_key: nwg::Button,
    edit_replacements: nwg::Button,
    logs: nwg::Button,
    check_updates: nwg::Button,
    restart: nwg::Button,
    update_label: nwg::Label,
    status: nwg::Label,
    settings_labels: Vec<nwg::Label>,
    settings_page: bool,
    main_window: usize,
    _serif: nwg::Font,
    body_font: nwg::Font,
    mono: nwg::Font,
    _icon: nwg::Icon,
    store: Store,
    worker: Worker,
    updater: Updater,
}
impl Ui {
    fn set_page(&mut self, settings: bool) {
        self.settings_page = settings;
        self.title
            .set_text(if settings { "Settings" } else { "Activity" });
        for h in [
            &self.stats_label.handle,
            &self.refresh.handle,
            &self.panel.handle,
        ] {
            show(h, !settings);
        }
        for h in self.settings_handles() {
            show(&h, settings);
        }
        if settings {
            self.reload_keys();
        }
        self.layout();
    }
    fn settings_handles(&self) -> Vec<nwg::ControlHandle> {
        let mut handles = vec![
            self.model.handle,
            self.developer.handle,
            self.startup.handle,
            self.keys.handle,
            self.key_name.handle,
            self.key_value.handle,
            self.add_key.handle,
            self.select_key.handle,
            self.remove_key.handle,
            self.edit_replacements.handle,
            self.logs.handle,
            self.check_updates.handle,
            self.restart.handle,
            self.update_label.handle,
            self.status.handle,
        ];
        handles.extend(self.settings_labels.iter().map(|l| l.handle));
        handles
    }
    fn error(&self, error: String) {
        self.status.set_text(&error);
    }
    fn reload_keys(&mut self) {
        match self.store.load_keys() {
            Ok(keys) => {
                self.key_ids = keys.keys.iter().map(|k| k.id.clone()).collect();
                self.keys.set_collection(
                    keys.keys
                        .iter()
                        .map(|k| {
                            format!(
                                "{} — {}{}",
                                k.name,
                                mask(&k.value),
                                if Some(&k.id) == keys.active_key_id.as_ref() {
                                    " (active)"
                                } else {
                                    ""
                                }
                            )
                        })
                        .collect(),
                );
                let selected = self
                    .key_ids
                    .iter()
                    .position(|id| Some(id) == keys.active_key_id.as_ref());
                self.keys.set_selection(selected);
            }
            Err(_) => self.error(
                "The encrypted API-key file could not be read and was left unchanged.".into(),
            ),
        }
    }
    fn save_preferences(&self) {
        let result = (|| {
            let mut settings = self.store.load_settings()?;
            settings.model = self
                .model
                .selection_string()
                .unwrap_or_else(|| "gpt-4.1".into());
            settings.developer_logging =
                self.developer.check_state() == nwg::CheckBoxState::Checked;
            let startup = self.startup.check_state() == nwg::CheckBoxState::Checked;
            platform::startup(startup)?;
            settings.start_on_login = startup;
            self.store.save_settings(&settings)?;
            Ok::<_, String>(())
        })();
        match result {
            Ok(()) => {
                self.worker.reload();
                self.status
                    .set_text("Saved. Changes apply to the next correction.");
            }
            Err(e) => self.error(e),
        }
    }
    fn key_action(&mut self, action: u8) {
        let result = (|| {
            let mut keys = self.store.load_keys()?;
            if action == 0 {
                keys.add(&self.key_name.text(), &self.key_value.text())?;
            } else {
                let id = self
                    .keys
                    .selection()
                    .and_then(|i| self.key_ids.get(i))
                    .ok_or("Select a key first")?
                    .clone();
                if action == 1 {
                    keys.active_key_id = Some(id);
                } else {
                    keys.keys.retain(|k| k.id != id);
                    if keys.active_key_id.as_deref() == Some(&id) {
                        keys.active_key_id = keys.keys.first().map(|k| k.id.clone());
                    }
                }
            }
            self.store.save_keys(&keys)?;
            Ok::<_, String>(())
        })();
        match result {
            Ok(()) => {
                self.key_value.set_text("");
                self.key_name.set_text("");
                self.worker.reload();
                self.reload_keys();
                self.status.set_text("Encrypted key collection saved.");
            }
            Err(e) => self.error(e),
        }
    }
    fn refresh(&mut self) {
        self.rows.clear();
        self.scroll = 0;
        self.total_height = 0;
        self.loading = true;
        self.exhausted = false;
        self.generation += 1;
        let _ = self.feed_tx.send(FeedRequest::Refresh);
    }
    fn receive(&mut self) {
        while let Ok(reply) = self.feed_rx.try_recv() {
            if reply.generation != self.generation {
                continue;
            }
            if let Some(stats) = reply.stats {
                self.stats_label.set_text(&format!(
                    "{} CHECKS          {} CORRECTIONS          {} ACCURACY          {} DAY STREAK",
                    stats.checks,
                    stats.corrections,
                    if stats.checks == 0 {
                        "—".into()
                    } else {
                        format!(
                            "{:.0}%",
                            100. * stats.corrections as f64 / stats.checks as f64
                        )
                    },
                    stats.streak
                ));
            }
            if !reply.page {
                continue;
            }
            self.loading = false;
            if reply.entries.len() < 30 {
                self.exhausted = true;
            }
            for entry in reply.entries {
                if let Ok(row) = self.make_row(entry) {
                    self.total_height += row.height;
                    self.rows.push(row);
                }
            }
            self.layout();
        }
    }
    fn make_row(&self, entry: Entry) -> Result<Row, nwg::NwgError> {
        let metadata = label(
            &self.panel,
            &format!(
                "{}\n{}",
                entry.timestamp.get(11..16).unwrap_or(""),
                entry.data["model"].as_str().unwrap_or("")
            ),
            0,
            0,
            100,
            45,
            &self.mono,
        )?;
        let mut body = nwg::RichTextBox::default();
        nwg::RichTextBox::builder()
            .parent(&self.panel)
            .position((110, 0))
            .size((560, 100))
            .readonly(true)
            .font(Some(&self.body_font))
            .flags(
                nwg::RichTextBoxFlags::VISIBLE
                    | nwg::RichTextBoxFlags::VSCROLL
                    | nwg::RichTextBoxFlags::AUTOVSCROLL,
            )
            .build(&mut body)?;
        body.set_background_color(CREAM);
        let date = entry.timestamp.get(..10).unwrap_or("");
        let previous = self.rows.last().and_then(|r| r.entry.timestamp.get(..10));
        let day = if previous != Some(date) {
            Some(label(&self.panel, date, 0, 0, 700, 26, &self.body_font)?)
        } else {
            None
        };
        let text = entry.data["output_text"].as_str().unwrap_or("");
        let lines = text
            .lines()
            .map(|l| 1 + l.chars().count() / 65)
            .sum::<usize>()
            .clamp(2, 100);
        let height = (lines as i32 * 21 + 48).max(90) + if day.is_some() { 32 } else { 0 };
        let mut row = Row {
            entry,
            day,
            parent: self.panel.handle,
            secondary: None,
            body_handlers: Vec::new(),
            metadata,
            body,
            copy: button(&self.panel, "Copy", 110, 0, 60)?,
            timing: button(&self.panel, "Timing", 180, 0, 65)?,
            original: button(&self.panel, "See original", 255, 0, 100)?,
            side: button(&self.panel, "Side by side", 365, 0, 100)?,
            original_shown: false,
            side_shown: false,
            timing_shown: false,
            base_height: height,
            height,
            y: self.total_height,
        };
        row.body_handlers
            .push(body_handler(&row.body.handle, row.parent, row.copy.handle)?);
        render_row(&mut row);
        Ok(row)
    }
    fn reflow(&mut self) {
        let mut y = 0;
        for row in &mut self.rows {
            row.y = y;
            let timing = if row.timing_shown {
                row.entry.data["timings"]
                    .as_object()
                    .map_or(0, |m| (m.len() as i32 + 2) * 21)
            } else {
                0
            };
            row.height = row.base_height + timing;
            y += row.height;
        }
        self.total_height = y;
        self.scroll = self.scroll.min((y - self.panel.size().1 as i32).max(0));
        self.layout();
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        let sx = ((w as f32 - 304.) / 790.).clamp(0.65, 1.);
        let sy = ((h as f32 - 24.) / 682.).clamp(0.72, 1.);
        let x = |value: i32| 272 + ((value - 272) as f32 * sx) as i32;
        let y = |value: i32| (value as f32 * sy) as i32;
        let width_of = |value: i32| (value as f32 * sx) as u32;
        macro_rules! place {
            ($control:ident,$left:expr,$top:expr,$width:expr,$height:expr) => {
                self.$control.set_position(x($left), y($top));
                self.$control.set_size(width_of($width), $height);
            };
        }
        place!(model, 700, 160, 270, 100);
        place!(keys, 272, 258, 500, 150);
        place!(key_name, 460, 310, 500, 30);
        place!(key_value, 460, 360, 500, 30);
        place!(add_key, 820, 410, 140, 32);
        place!(select_key, 790, 255, 90, 32);
        place!(remove_key, 890, 255, 90, 32);
        place!(developer, 272, 500, 245, 28);
        place!(startup, 560, 500, 230, 28);
        place!(edit_replacements, 272, 550, 165, 32);
        place!(logs, 450, 550, 110, 32);
        place!(check_updates, 575, 550, 165, 32);
        place!(restart, 755, 550, 190, 32);
        place!(update_label, 272, 596, 720, 25);
        place!(status, 272, 632, 750, 45);
        for (index, (left, top, width)) in [
            (272, 100, 400),
            (272, 165, 400),
            (272, 225, 720),
            (272, 315, 160),
            (272, 365, 160),
            (272, 465, 720),
            (720, 100, 200),
        ]
        .into_iter()
        .enumerate()
        {
            if let Some(label) = self.settings_labels.get(index) {
                label.set_position(x(left), y(top));
                label.set_size(width_of(width), 25);
            }
        }
        self.help.set_position(24, h.saturating_sub(130) as i32);
        self.refresh.set_position(w.saturating_sub(144) as i32, 36);
        self._version.set_position(24, h.saturating_sub(85) as i32);
        let width = w.saturating_sub(304);
        let height = h.saturating_sub(210);
        self.panel.set_size(width, height);
        let info = SCROLLINFO {
            cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
            fMask: SIF_RANGE | SIF_PAGE | SIF_POS,
            nMin: 0,
            nMax: self.total_height.saturating_sub(1),
            nPage: height,
            nPos: self.scroll,
            nTrackPos: 0,
        };
        unsafe {
            windows_sys::Win32::UI::Controls::SetScrollInfo(
                self.panel.handle.hwnd().unwrap().cast(),
                SB_VERT,
                &info,
                1,
            );
        }
        for row in &self.rows {
            let y = row.y - self.scroll;
            let visible = y + row.height > 0 && y < height as i32;
            for handle in [
                &row.metadata.handle,
                &row.body.handle,
                &row.copy.handle,
                &row.timing.handle,
                &row.original.handle,
                &row.side.handle,
            ] {
                show(handle, visible && !self.settings_page);
            }
            if let Some(day) = &row.day {
                show(&day.handle, visible && !self.settings_page);
                day.set_position(0, y);
            }
            let offset = if row.day.is_some() { 32 } else { 0 };
            let split = row.side_shown && !row.original_shown;
            if let Some(second) = &row.secondary {
                show(&second.handle, visible && !self.settings_page && split);
            }
            if !visible {
                continue;
            }
            row.metadata.set_position(0, y + offset);
            row.body.set_position(110, y + offset);
            let full_width = width.saturating_sub(120);
            let body_width = if split {
                full_width.saturating_sub(12) / 2
            } else {
                full_width
            };
            let body_height = row.height.saturating_sub(45 + offset) as u32;
            row.body.set_size(body_width, body_height);
            if let Some(second) = &row.secondary {
                second.set_position(110 + body_width as i32 + 12, y + offset);
                second.set_size(body_width, body_height);
            }
            let button_y = y + row.height - 39;
            row.copy.set_position(110, button_y);
            row.timing.set_position(180, button_y);
            row.original.set_position(255, button_y);
            row.side.set_position(365, button_y);
        }
    }
    fn scroll_by(&mut self, delta: i32) {
        let viewport = self.panel.size().1 as i32;
        self.scroll = (self.scroll + delta).clamp(0, (self.total_height - viewport).max(0));
        self.layout();
        if !self.loading && !self.exhausted && self.scroll + viewport >= self.total_height - 120 {
            self.loading = true;
            let _ = self.feed_tx.send(FeedRequest::More);
        }
    }
    fn row_click(&mut self, hwnd: usize) -> Option<CopyJob> {
        for row in &mut self.rows {
            let is =
                |handle: &nwg::ControlHandle| handle.hwnd().is_some_and(|h| h as usize == hwnd);
            if is(&row.copy.handle) {
                let text = if row.original_shown {
                    row.entry.data["input_text"].as_str()
                } else {
                    row.entry.data["output_text"].as_str()
                }
                .unwrap_or("")
                .to_owned();
                return Some(CopyJob {
                    owner: self.window.handle.hwnd().unwrap() as usize,
                    button: hwnd,
                    text,
                });
            }
            if is(&row.original.handle) {
                row.original_shown = !row.original_shown;
                render_row(row);
                self.reflow();
                return None;
            }
            if is(&row.side.handle) {
                row.side_shown = !row.side_shown;
                row.original_shown = false;
                render_row(row);
                self.reflow();
                return None;
            }
            if is(&row.timing.handle) {
                row.timing_shown = !row.timing_shown;
                render_row(row);
                self.reflow();
                return None;
            }
        }
        None
    }
}
fn render_row(row: &mut Row) {
    row.copy.set_text("Copy");
    reset_text(&row.body);
    let input_preview = preview(row.entry.data["input_text"].as_str().unwrap_or(""));
    let output_preview = preview(row.entry.data["output_text"].as_str().unwrap_or(""));
    let input = input_preview.as_str();
    let output = output_preview.as_str();
    row.original.set_text(if row.original_shown {
        "See corrected"
    } else {
        "See original"
    });
    if row.original_shown {
        append_text(&row.body, "ORIGINAL TEXT - NOT SPELL-CHECKED\r\n");
        append_text(&row.body, input);
    } else if row.side_shown {
        if row.secondary.is_none() {
            let mut second = nwg::RichTextBox::default();
            if nwg::RichTextBox::builder()
                .parent(row.parent)
                .readonly(true)
                .size((300, 100))
                .flags(
                    nwg::RichTextBoxFlags::VISIBLE
                        | nwg::RichTextBoxFlags::VSCROLL
                        | nwg::RichTextBoxFlags::AUTOVSCROLL,
                )
                .build(&mut second)
                .is_ok()
            {
                second.set_background_color(CREAM);
                if let Ok(handler) = body_handler(&second.handle, row.parent, row.copy.handle) {
                    row.body_handlers.push(handler);
                }
                row.secondary = Some(second);
            }
        }
        append_text(&row.body, "ORIGINAL\r\n");
        if let Some(second) = &row.secondary {
            reset_text(second);
            append_text(second, "CORRECTED\r\n");
            for edit in diff(
                &input.encode_utf16().collect::<Vec<_>>(),
                &output.encode_utf16().collect::<Vec<_>>(),
            ) {
                let text = String::from_utf16_lossy(&edit.text);
                if edit.kind != Kind::Insert {
                    append_segment(&row.body, &text, edit.kind);
                }
                if edit.kind != Kind::Delete {
                    append_segment(second, &text, edit.kind);
                }
            }
        }
    } else {
        for edit in diff(
            &input.encode_utf16().collect::<Vec<_>>(),
            &output.encode_utf16().collect::<Vec<_>>(),
        ) {
            append_segment(&row.body, &String::from_utf16_lossy(&edit.text), edit.kind);
        }
    }
    if row.timing_shown
        && let Some(timings) = row.entry.data["timings"].as_object()
    {
        append_text(&row.body, "\r\n\r\nTIMING\r\n");
        for (k, v) in timings {
            if !v.is_null() {
                append_text(&row.body, &format!("{k}: {v} ms\r\n"));
            }
        }
    }
    row.body.set_selection(0..0);
    row.body.scroll(0);
}
fn run_dashboard(
    state: &Dashboard,
    start_settings: bool,
) -> Result<(), Box<dyn std::error::Error>> {
    nwg::init()?;
    clipboard::initialize_sta()?;
    style::initialize_fonts();
    nwg::Font::set_global_family("Instrument Sans")?;
    let serif = style::font("Instrument Serif", 32)?;
    let body_font = style::font("Instrument Sans", 15)?;
    let mono = style::font("JetBrains Mono", 12)?;
    let icon = style::icon()?;
    let mut window = nwg::Window::default();
    nwg::Window::builder()
        .title(crate::channel::DISPLAY_NAME)
        .size((1100, 720))
        .position((160, 120))
        .flags(nwg::WindowFlags::MAIN_WINDOW | nwg::WindowFlags::VISIBLE)
        .icon(Some(&icon))
        .build(&mut window)?;
    let home = button(&window, "Home", 24, 90, 190)?;
    let settings_button = button(&window, "Settings", 24, 134, 190)?;
    let help = button(&window, "Help", 24, 560, 190)?;
    let title = label(&window, "Activity", 272, 28, 430, 48, &serif)?;
    let brand = label(
        &window,
        "UNIVERSAL\nSPELL CHECK",
        24,
        26,
        205,
        48,
        &body_font,
    )?;
    let version = label(
        &window,
        &format!(
            "Version {}\n{}",
            crate::channel::VERSION,
            crate::channel::CHANNEL
        ),
        24,
        610,
        200,
        55,
        &mono,
    )?;
    let stats_label = label(
        &window,
        "Loading activity...",
        272,
        100,
        790,
        60,
        &body_font,
    )?;
    let refresh = button(&window, "Refresh", 940, 36, 112)?;
    let mut panel = nwg::Frame::default();
    nwg::Frame::builder()
        .parent(&window)
        .position((272, 180))
        .size((790, 510))
        .build(&mut panel)?;
    unsafe {
        let hwnd = panel.handle.hwnd().unwrap();
        let style = GetWindowLongPtrW(hwnd.cast(), GWL_STYLE);
        SetWindowLongPtrW(hwnd.cast(), GWL_STYLE, style | WS_VSCROLL as isize);
        SetWindowPos(
            hwnd.cast(),
            std::ptr::null_mut(),
            0,
            0,
            0,
            0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
        );
    }
    let mut feed_notice = nwg::Notice::default();
    nwg::Notice::builder()
        .parent(&window)
        .build(&mut feed_notice)?;
    let notice = feed_notice.sender();
    let (request_tx, request_rx) = mpsc::channel();
    let (reply_tx, reply_rx) = mpsc::channel();
    let directory = state.store.log_directory.clone();
    let stats_busy = Arc::new(AtomicBool::new(false));
    let stats_generation = Arc::new(std::sync::atomic::AtomicU64::new(0));
    thread::Builder::new()
        .name("activity-reader".into())
        .spawn(move || {
            let mut reader = Reader::new(&directory);
            let mut generation = 0;
            while let Ok(request) = request_rx.recv() {
                let refresh = match request {
                    FeedRequest::Stop => break,
                    FeedRequest::Refresh => {
                        generation += 1;
                        stats_generation.store(generation, Ordering::Release);
                        reader = Reader::new(&directory);
                        true
                    }
                    FeedRequest::More => false,
                };
                let entries = reader.page();
                if reply_tx
                    .send(FeedReply {
                        entries,
                        stats: None,
                        generation,
                        page: true,
                    })
                    .is_err()
                {
                    break;
                }
                notice.notice();
                if refresh && !stats_busy.swap(true, Ordering::AcqRel) {
                    let directory = directory.clone();
                    let sender = reply_tx.clone();
                    let busy = stats_busy.clone();
                    let epoch = stats_generation.clone();
                    let notice = notice;
                    let started = thread::Builder::new()
                        .name("activity-statistics".into())
                        .spawn(move || {
                            let stats = activity::stats(&directory);
                            let _ = sender.send(FeedReply {
                                entries: Vec::new(),
                                stats: Some(stats),
                                generation: epoch.load(Ordering::Acquire),
                                page: false,
                            });
                            notice.notice();
                            busy.store(false, Ordering::Release);
                        });
                    if started.is_err() {
                        stats_busy.store(false, Ordering::Release);
                    }
                }
            }
        })?;
    let labels = [
        ("Hotkey", 100),
        ("Model", 165),
        ("API keys — encrypted for this Windows user", 225),
        ("New key name", 315),
        ("New API key", 365),
        ("Logs and startup", 465),
    ];
    let mut settings_labels = Vec::new();
    for (text, y) in labels {
        settings_labels.push(label(&window, text, 272, y, 720, 25, &body_font)?);
    }
    let hotkey = format!(
        "Ctrl + Alt + {}",
        char::from_u32(crate::channel::HOTKEY_VK).unwrap()
    );
    settings_labels.push(label(&window, &hotkey, 720, 100, 200, 25, &mono)?);
    let saved = state.store.load_settings().unwrap_or_default();
    let mut model = nwg::ComboBox::default();
    nwg::ComboBox::builder()
        .parent(&window)
        .position((700, 160))
        .size((270, 100))
        .collection(vec!["gpt-4.1".into(), "gpt-5.4-mini".into()])
        .build(&mut model)?;
    model.set_selection(Some(if saved.model == "gpt-5.4-mini" { 1 } else { 0 }));
    let mut keys = nwg::ComboBox::default();
    nwg::ComboBox::builder()
        .parent(&window)
        .position((272, 258))
        .size((500, 150))
        .build(&mut keys)?;
    let select_key = button(&window, "Use key", 790, 255, 90)?;
    let remove_key = button(&window, "Remove", 890, 255, 90)?;
    let mut key_name = nwg::TextInput::default();
    nwg::TextInput::builder()
        .parent(&window)
        .position((460, 310))
        .size((500, 30))
        .limit(64)
        .build(&mut key_name)?;
    let mut key_value = nwg::TextInput::default();
    nwg::TextInput::builder()
        .parent(&window)
        .position((460, 360))
        .size((500, 30))
        .password(Some('•'))
        .limit(4096)
        .build(&mut key_value)?;
    let add_key = button(&window, "Save new key", 820, 410, 140)?;
    let mut developer = nwg::CheckBox::default();
    nwg::CheckBox::builder()
        .parent(&window)
        .text("Developer logging")
        .position((272, 500))
        .size((245, 28))
        .check_state(if saved.developer_logging {
            nwg::CheckBoxState::Checked
        } else {
            nwg::CheckBoxState::Unchecked
        })
        .build(&mut developer)?;
    let mut startup = nwg::CheckBox::default();
    nwg::CheckBox::builder()
        .parent(&window)
        .text("Start on login")
        .position((560, 500))
        .size((230, 28))
        .enabled(!crate::channel::IS_DEV)
        .check_state(if platform::startup_registered() {
            nwg::CheckBoxState::Checked
        } else {
            nwg::CheckBoxState::Unchecked
        })
        .build(&mut startup)?;
    let edit_replacements = button(&window, "Edit replacements", 272, 550, 165)?;
    let logs = button(&window, "Open logs", 450, 550, 110)?;
    let check_updates = button(&window, "Check for updates", 575, 550, 165)?;
    let restart = button(&window, "Restart to update", 755, 550, 190)?;
    let update_label = label(
        &window,
        &state.updater.state.lock().unwrap(),
        272,
        596,
        720,
        25,
        &body_font,
    )?;
    let status = label(&window, "", 272, 632, 750, 45, &body_font)?;
    let ui = Rc::new(RefCell::new(Ui {
        window,
        home,
        settings_button,
        help,
        title,
        _brand: brand,
        _version: version,
        stats_label,
        refresh,
        panel,
        feed_notice,
        feed_rx: reply_rx,
        feed_tx: request_tx,
        rows: Vec::new(),
        scroll: 0,
        total_height: 0,
        loading: false,
        exhausted: false,
        generation: 0,
        model,
        developer,
        startup,
        keys,
        key_ids: Vec::new(),
        key_name,
        key_value,
        add_key,
        select_key,
        remove_key,
        edit_replacements,
        logs,
        check_updates,
        restart,
        update_label,
        status,
        settings_labels,
        settings_page: false,
        main_window: state.main_window,
        _serif: serif,
        body_font,
        mono,
        _icon: icon,
        store: state.store.clone(),
        worker: state.worker.clone(),
        updater: state.updater.clone(),
    }));
    let parent_hwnd = ui.borrow().window.handle.hwnd().unwrap() as usize;
    let pending_page = Rc::new(Cell::new(None::<bool>));
    let pending_update = Rc::new(Cell::new(false));
    let weak = Rc::downgrade(&ui);
    let handler =
        nwg::full_bind_event_handler(&ui.borrow().window.handle, move |event, _data, handle| {
            if event == nwg::Event::OnWindowClose {
                nwg::stop_thread_dispatch();
                return;
            }
            let Some(ui) = weak.upgrade() else {
                return;
            };
            let Ok(mut view) = ui.try_borrow_mut() else {
                if event == nwg::Event::OnNotice {
                    unsafe {
                        SetTimer(parent_hwnd as _, 99, 1, None);
                    }
                }
                return;
            };
            let mut open_path = None;
            match event {
                nwg::Event::OnNotice => {
                    if handle == view.feed_notice.handle {
                        view.receive();
                    }
                }
                nwg::Event::OnResize => view.layout(),
                nwg::Event::OnComboxBoxSelection => {
                    if handle == view.model.handle {
                        view.save_preferences();
                    }
                }
                nwg::Event::OnButtonClick => {
                    if handle == view.home.handle {
                        view.set_page(false);
                    } else if handle == view.settings_button.handle {
                        view.set_page(true);
                    } else if handle == view.refresh.handle {
                        view.refresh();
                    } else if handle == view.add_key.handle {
                        view.key_action(0);
                    } else if handle == view.select_key.handle {
                        view.key_action(1);
                    } else if handle == view.remove_key.handle {
                        view.key_action(2);
                    } else if handle == view.developer.handle || handle == view.startup.handle {
                        view.save_preferences();
                    } else if handle == view.edit_replacements.handle {
                        open_path = Some(view.store.replacements_path.clone());
                    } else if handle == view.help.handle {
                        open_path = Some(std::path::PathBuf::from(
                            "https://github.com/ElliotDrel/Universal-Spell-Check",
                        ));
                    } else if handle == view.logs.handle {
                        open_path = Some(view.store.log_directory.clone());
                    } else if handle == view.check_updates.handle {
                        view.updater.check(Trigger::Manual);
                    } else if handle == view.restart.handle {
                        unsafe {
                            PostMessageW(
                                view.main_window as _,
                                RegisterWindowMessageW(
                                    crate::store::wide(&crate::channel::update_message()).as_ptr(),
                                ),
                                0,
                                0,
                            );
                        }
                    }
                }
                _ => {}
            }
            drop(view);
            if let Some(path) = open_path {
                platform::open(path);
            }
        });
    let weak = Rc::downgrade(&ui);
    let pending_page_handler = pending_page.clone();
    let pending_update_handler = pending_update.clone();
    let raw = nwg::bind_raw_event_handler(
        &ui.borrow().window.handle,
        crate::style::raw_handler_id(900),
        move |hwnd, msg, w, l| {
            if msg == WM_ERASEBKGND {
                return Some(style::paint_background(hwnd.cast(), w as _));
            }
            if msg == WM_CTLCOLORSTATIC {
                return Some(style::text_background(w as _));
            }
            if msg == WM_DESTROY {
                unsafe {
                    PostQuitMessage(0);
                }
                return None;
            }
            let ui = weak.upgrade()?;
            if msg == WM_APP + 13 {
                pending_page_handler.set(Some(w != 0));
                unsafe {
                    SetTimer(hwnd.cast(), 99, 1, None);
                }
                return Some(0);
            }
            if msg == WM_APP + 14 {
                pending_update_handler.set(true);
                unsafe {
                    SetTimer(hwnd.cast(), 99, 1, None);
                }
                return Some(0);
            }
            if msg == WM_TIMER && w == 99 {
                if let Ok(mut view) = ui.try_borrow_mut() {
                    unsafe {
                        KillTimer(hwnd.cast(), 99);
                    }
                    view.receive();
                    if let Some(settings) = pending_page_handler.take() {
                        view.set_page(settings);
                    }
                    if pending_update_handler.replace(false) {
                        view.update_label.set_text(
                            &view.updater.state.lock().unwrap_or_else(|p| p.into_inner()),
                        );
                    }
                }
                return Some(0);
            }
            if msg == WM_TIMER && w == 98 {
                if let Ok(view) = ui.try_borrow() {
                    for row in &view.rows {
                        row.copy.set_text("Copy");
                    }
                    unsafe {
                        KillTimer(hwnd.cast(), 98);
                    }
                }
                return Some(0);
            }
            if msg == WM_COMMAND && (w >> 16) == 0 {
                click_row(&ui, l as usize);
            }
            if msg == WM_GETMINMAXINFO {
                unsafe {
                    let info = &mut *(l as *mut MINMAXINFO);
                    let scale = style::dpi_scale();
                    info.ptMinTrackSize.x = (900.0 * scale).round() as i32;
                    info.ptMinTrackSize.y = (600.0 * scale).round() as i32;
                }
            }
            None
        },
    )?;
    let weak = Rc::downgrade(&ui);
    let panel_raw = nwg::bind_raw_event_handler(
        &ui.borrow().panel.handle,
        crate::style::raw_handler_id(901),
        move |hwnd, msg, w, l| {
            if msg == WM_ERASEBKGND {
                return Some(style::paint_background(hwnd.cast(), w as _));
            }
            if msg == WM_CTLCOLORSTATIC {
                return Some(style::text_background(w as _));
            }
            let ui = weak.upgrade()?;
            if msg == WM_MOUSEWHEEL {
                if let Ok(mut view) = ui.try_borrow_mut() {
                    view.scroll_by(-((w >> 16) as i16 as i32));
                }
                return Some(0);
            }
            if msg == WM_VSCROLL {
                if let Ok(mut view) = ui.try_borrow_mut() {
                    let page = view.panel.size().1 as i32;
                    let delta = match (w & 0xffff) as i32 {
                        SB_LINEUP => -40,
                        SB_LINEDOWN => 40,
                        SB_PAGEUP => -page,
                        SB_PAGEDOWN => page,
                        SB_TOP => -view.scroll,
                        SB_BOTTOM => view.total_height,
                        SB_THUMBPOSITION | SB_THUMBTRACK => {
                            let mut info = SCROLLINFO {
                                cbSize: std::mem::size_of::<SCROLLINFO>() as u32,
                                fMask: SIF_TRACKPOS,
                                ..Default::default()
                            };
                            unsafe {
                                GetScrollInfo(hwnd.cast(), SB_VERT, &mut info);
                            }
                            info.nTrackPos - view.scroll
                        }
                        _ => 0,
                    };
                    view.scroll_by(delta);
                }
                return Some(0);
            }
            if msg == WM_COMMAND && (w >> 16) == 0 {
                click_row(&ui, l as usize);
            }
            None
        },
    )?;
    state.hwnd.store(
        ui.borrow().window.handle.hwnd().unwrap() as usize,
        Ordering::Release,
    );
    ui.borrow_mut().set_page(start_settings);
    ui.borrow_mut().refresh();
    nwg::dispatch_thread_events();
    let _ = ui.borrow().feed_tx.send(FeedRequest::Stop);
    nwg::unbind_event_handler(&handler);
    nwg::unbind_raw_event_handler(&raw)?;
    nwg::unbind_raw_event_handler(&panel_raw)?;
    Ok(())
}

impl Drop for Row {
    fn drop(&mut self) {
        for handler in self.body_handlers.drain(..) {
            let _ = nwg::unbind_raw_event_handler(&handler);
        }
    }
}
fn preview(text: &str) -> String {
    let mut shown = String::new();
    let mut lines = 0;
    let mut utf16_units = 0;
    for c in text.chars() {
        if utf16_units + c.len_utf16() > 32000 || lines >= 100 {
            shown.push_str("\n… Long entry; Copy includes the full text.");
            break;
        }
        shown.push(c);
        utf16_units += c.len_utf16();
        if c == '\n' {
            lines += 1;
        }
    }
    shown
}

fn body_handler(
    body: &nwg::ControlHandle,
    parent: nwg::ControlHandle,
    copy: nwg::ControlHandle,
) -> Result<nwg::RawEventHandler, nwg::NwgError> {
    let parent = parent.hwnd().unwrap() as usize;
    let copy = copy.hwnd().unwrap() as usize;
    nwg::bind_raw_event_handler(
        body,
        crate::style::raw_handler_id(930),
        move |_, message, w, l| {
            if message == WM_MOUSEWHEEL {
                unsafe {
                    PostMessageW(parent as _, message, w, l);
                }
                return Some(0);
            }
            if message == WM_LBUTTONUP {
                unsafe {
                    PostMessageW(parent as _, WM_COMMAND, 0, copy as isize);
                }
            }
            None
        },
    )
}

struct CopyJob {
    owner: usize,
    button: usize,
    text: String,
}
fn click_row(ui: &Rc<RefCell<Ui>>, button: usize) {
    let job = {
        let Ok(mut view) = ui.try_borrow_mut() else {
            return;
        };
        view.row_click(button)
    };
    let Some(job) = job else {
        return;
    };
    let result = clipboard::write(job.owner as _, &job.text, "", None, false);
    if unsafe { IsWindow(job.owner as _) } == 0 {
        return;
    }
    if let Ok(view) = ui.try_borrow()
        && let Some(row) = view.rows.iter().find(|r| {
            r.copy
                .handle
                .hwnd()
                .is_some_and(|h| h as usize == job.button)
        })
    {
        row.copy
            .set_text(if result.is_ok() { "Copied" } else { "Retry" });
        unsafe {
            SetTimer(job.owner as _, 98, 1500, None);
        }
    }
}
fn rich_length(body: &nwg::RichTextBox) -> u32 {
    #[repr(C)]
    struct Length {
        flags: u32,
        codepage: u32,
    }
    let request = Length {
        flags: 0x2 | 0x8,
        codepage: 1200,
    };
    unsafe {
        SendMessageW(
            body.handle.hwnd().unwrap().cast(),
            0x045f,
            (&request as *const Length) as usize,
            0,
        )
        .max(0) as u32
    }
}

fn reset_text(body: &nwg::RichTextBox) {
    body.clear();
    body.set_selection(0..0);
    body.set_char_format(&nwg::CharFormat {
        effects: Some(nwg::CharEffects::empty()),
        text_color: Some([26, 26, 26]),
        font_face_name: Some("Instrument Sans".into()),
        height: Some(225),
        ..Default::default()
    });
}
fn append_segment(body: &nwg::RichTextBox, text: &str, kind: Kind) {
    let from = rich_length(body);
    append_text(body, text);
    let to = rich_length(body);
    body.set_selection(from..to);
    body.set_char_format(&nwg::CharFormat {
        effects: Some(if kind == Kind::Delete {
            nwg::CharEffects::STRIKEOUT
        } else {
            nwg::CharEffects::empty()
        }),
        text_color: Some(match kind {
            Kind::Equal => [26, 26, 26],
            Kind::Delete => [184, 69, 59],
            Kind::Insert => [63, 107, 38],
        }),
        ..Default::default()
    });
}

fn append_text(body: &nwg::RichTextBox, text: &str) {
    let end = rich_length(body);
    let text = crate::store::wide(text);
    let hwnd = body.handle.hwnd().unwrap().cast();
    body.set_selection(end..end);
    unsafe {
        SendMessageW(hwnd, 0x00CF, 0, 0); // EM_SETREADONLY
        SendMessageW(hwnd, 0x00C2, 0, text.as_ptr() as isize); // EM_REPLACESEL preserves earlier formatting.
        SendMessageW(hwnd, 0x00CF, 1, 0);
    }
}
