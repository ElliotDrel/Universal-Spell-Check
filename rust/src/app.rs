use crate::{
    channel,
    dashboard::Dashboard,
    logging::Logger,
    platform,
    runtime::{Bus, UiEvent, Worker},
    store::{Store, wide},
    style,
    update::{Trigger, Updater},
};
use native_windows_gui as nwg;
use std::{
    cell::RefCell,
    ptr::null,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    time::Instant,
};
use windows_sys::Win32::{
    Foundation::*,
    UI::{Input::KeyboardAndMouse::*, WindowsAndMessaging::*},
};
pub fn run(
    store: Store,
    start_dashboard: bool,
    start_settings: bool,
    startup_smoke: bool,
    restart_notice: bool,
) -> Result<(), Box<dyn std::error::Error>> {
    let _instance = match platform::Mutex::instance()? {
        Some(m) => m,
        None => return Ok(()),
    };
    nwg::init()?;
    crate::clipboard::initialize_sta()?;
    style::initialize_fonts();
    nwg::Font::set_global_family("Instrument Sans")?;
    let icon = style::icon()?;
    let mut window = nwg::Window::default();
    nwg::Window::builder()
        .title(&channel::hotkey_title())
        .size((0, 0))
        .flags(nwg::WindowFlags::WINDOW)
        .build(&mut window)?;
    let hwnd = window.handle.hwnd().unwrap();
    let mut tray = nwg::TrayNotification::default();
    nwg::TrayNotification::builder()
        .parent(&window)
        .icon(Some(&icon))
        .tip(Some(channel::DISPLAY_NAME))
        .build(&mut tray)?;
    let mut menu = nwg::Menu::default();
    nwg::Menu::builder()
        .parent(&window)
        .popup(true)
        .build(&mut menu)?;
    fn item(menu: &nwg::Menu, title: &str) -> Result<nwg::MenuItem, nwg::NwgError> {
        let mut item = Default::default();
        nwg::MenuItem::builder()
            .parent(menu)
            .text(title)
            .build(&mut item)?;
        Ok(item)
    }
    let dashboard_item = item(&menu, "Open Dashboard")?;
    let settings_item = item(&menu, "Settings")?;
    let logs_item = item(&menu, "Open logs folder")?;
    let check_item = item(&menu, "Check for updates")?;
    let restart_item = item(&menu, "Restart to update")?;
    let version_item = item(
        &menu,
        &format!("Version {} ({})", channel::VERSION, channel::CHANNEL),
    )?;
    version_item.set_enabled(false);
    let quit = item(&menu, "Quit")?;
    let mut notice = nwg::Notice::default();
    nwg::Notice::builder().parent(&window).build(&mut notice)?;
    let (tx, rx) = mpsc::channel();
    let bus = Bus {
        sender: tx,
        notice: notice.sender(),
        observe_trials: false,
    };
    let logger = Logger::new(store.log_directory.clone());
    let migration = platform::migrate(&store);
    logger.event(if migration.is_ok() {
        "data_migration_completed"
    } else {
        "data_migration_failed"
    });
    if !startup_smoke {
        let _ = platform::first_run(&store);
    }
    let worker = Worker::new(store.clone(), logger.clone(), bus.clone(), hwnd as usize)?;
    let updater = Updater::new(store.clone(), logger.clone(), bus.clone());
    let dashboard = Dashboard::new(
        store.clone(),
        worker.clone(),
        updater.clone(),
        hwnd as usize,
    );
    let mut overlay = nwg::Window::default();
    nwg::Window::builder()
        .title("Spell check progress")
        .size((330, 45))
        .flags(nwg::WindowFlags::POPUP)
        .ex_flags(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST)
        .build(&mut overlay)?;
    let mut overlay_text = nwg::Label::default();
    nwg::Label::builder()
        .parent(&overlay)
        .text("")
        .position((12, 12))
        .size((310, 27))
        .build(&mut overlay_text)?;
    let phase = Rc::new(RefCell::new((String::new(), Instant::now())));
    let quitting = Arc::new(AtomicBool::new(false));
    let restarting = Arc::new(AtomicBool::new(false));
    let startup_checking = Arc::new(AtomicBool::new(!channel::IS_DEV && !startup_smoke));
    struct Ui {
        window: nwg::Window,
        tray: nwg::TrayNotification,
        menu: nwg::Menu,
        dashboard_item: nwg::MenuItem,
        settings_item: nwg::MenuItem,
        logs_item: nwg::MenuItem,
        check_item: nwg::MenuItem,
        restart_item: nwg::MenuItem,
        _version: nwg::MenuItem,
        quit: nwg::MenuItem,
        notice: nwg::Notice,
        overlay: nwg::Window,
        overlay_text: nwg::Label,
    }
    let ui = Rc::new(Ui {
        window,
        tray,
        menu,
        dashboard_item,
        settings_item,
        logs_item,
        check_item,
        restart_item,
        _version: version_item,
        quit,
        notice,
        overlay,
        overlay_text,
    });
    let weak = Rc::downgrade(&ui);
    let event_worker = worker.clone();
    let event_dashboard = dashboard.clone();
    let event_updater = updater.clone();
    let log_directory = store.log_directory.clone();
    let phase_events = phase.clone();
    let event_quitting = quitting.clone();
    let event_restarting = restarting.clone();
    let event_checking = startup_checking.clone();
    let handler = nwg::full_bind_event_handler(&ui.window.handle, move |event, _, handle| {
        let Some(ui) = weak.upgrade() else {
            return;
        };
        match event {
            nwg::Event::OnContextMenu => {
                if handle == ui.tray.handle {
                    let (x, y) = nwg::GlobalCursor::position();
                    ui.menu.popup(x, y);
                }
            }
            nwg::Event::OnMousePress(nwg::MousePressEvent::MousePressLeftUp) => {
                if handle == ui.tray.handle {
                    event_dashboard.open(false);
                }
            }
            nwg::Event::OnMenuItemSelected => {
                if handle == ui.dashboard_item.handle {
                    event_dashboard.open(false);
                } else if handle == ui.settings_item.handle {
                    event_dashboard.open(true);
                } else if handle == ui.logs_item.handle {
                    platform::open(&log_directory);
                } else if handle == ui.check_item.handle {
                    event_updater.check(Trigger::Manual);
                } else if handle == ui.restart_item.handle {
                    event_restarting.store(true, Ordering::Release);
                    if !event_worker.busy.load(Ordering::Acquire) {
                        event_updater.check(Trigger::Restart);
                    }
                } else if handle == ui.quit.handle {
                    event_quitting.store(true, Ordering::Release);
                    if !event_worker.busy.load(Ordering::Acquire) {
                        nwg::stop_thread_dispatch();
                    }
                }
            }
            nwg::Event::OnNotice if handle == ui.notice.handle => {
                while let Ok(event) = rx.try_recv() {
                    match event {
                        UiEvent::Notify(title, text) => ui.tray.show(
                            &text,
                            Some(&title),
                            Some(nwg::TrayNotificationFlags::ERROR_ICON),
                            None,
                        ),
                        UiEvent::Settings => event_dashboard.open(true),
                        UiEvent::Phase(text) => {
                            let mut phase = phase_events.borrow_mut();
                            if text.is_empty() {
                                ui.overlay.set_visible(false);
                                unsafe {
                                    KillTimer(ui.window.handle.hwnd().unwrap().cast(), 1);
                                }
                            } else {
                                if phase.0.is_empty() {
                                    phase.1 = Instant::now();
                                }
                                phase.0 = text.into();
                                ui.overlay_text.set_text(&format!(
                                    "{text}     {:.1}s",
                                    phase.1.elapsed().as_secs_f64()
                                ));
                                unsafe {
                                    let mut area = std::mem::zeroed::<RECT>();
                                    SystemParametersInfoW(
                                        SPI_GETWORKAREA,
                                        0,
                                        (&mut area as *mut RECT).cast(),
                                        0,
                                    );
                                    SetWindowPos(
                                        ui.overlay.handle.hwnd().unwrap().cast(),
                                        HWND_TOPMOST,
                                        (area.left + area.right - 330) / 2,
                                        area.bottom - 65,
                                        330,
                                        45,
                                        SWP_NOACTIVATE | SWP_SHOWWINDOW,
                                    );
                                    SetTimer(ui.window.handle.hwnd().unwrap().cast(), 1, 100, None);
                                }
                            }
                            if text.is_empty() {
                                phase.0.clear();
                            }
                        }
                        UiEvent::Finished => {
                            if event_quitting.load(Ordering::Acquire) {
                                nwg::stop_thread_dispatch();
                            } else if event_restarting.load(Ordering::Acquire) {
                                event_updater.check(Trigger::Restart);
                            }
                        }
                        UiEvent::ReadyToExit => {
                            event_quitting.store(true, Ordering::Release);
                            if !event_worker.busy.load(Ordering::Acquire) {
                                nwg::stop_thread_dispatch();
                            }
                        }
                        UiEvent::StartupChecked => {
                            event_checking.store(false, Ordering::Release);
                        }
                        UiEvent::Trial(_) | UiEvent::Log(_, _) => {}
                        UiEvent::Update(text) => {
                            ui.tray.set_tip(
                                &format!("{} — {text}", channel::DISPLAY_NAME)
                                    .chars()
                                    .take(120)
                                    .collect::<String>(),
                            );
                            event_dashboard.changed();
                            if !event_updater.busy.load(Ordering::Acquire)
                                && !event_updater.restart_pending()
                            {
                                event_restarting.store(false, Ordering::Release);
                            }
                        }
                    }
                }
            }
            _ => {}
        }
    });
    let weak = Rc::downgrade(&ui);
    let raw_worker = worker.clone();
    let raw_logger = logger.clone();
    let raw_phase = phase.clone();
    let raw_restart = restarting.clone();
    let raw_check = startup_checking.clone();
    let raw_updater = updater.clone();
    let update_message =
        unsafe { RegisterWindowMessageW(wide(&channel::update_message()).as_ptr()) };
    let raw = nwg::bind_raw_event_handler(&ui.window.handle, 910, move |_, msg, w, _| {
        let ui = weak.upgrade()?;
        if msg == WM_HOTKEY && w == 1 {
            if raw_restart.load(Ordering::Acquire) || raw_check.load(Ordering::Acquire) {
                raw_logger.event("guard_rejected reason=updating");
            } else {
                raw_worker.trigger(&raw_logger);
            }
            return Some(0);
        }
        if msg == update_message {
            if channel::IS_DEV {
                return Some(0);
            }
            if !raw_restart.swap(true, Ordering::AcqRel) {
                ui.tray.show(
                    "Restarting Universal Spell Check and checking for updates...",
                    Some("Restarting"),
                    Some(nwg::TrayNotificationFlags::INFO_ICON),
                    None,
                );
                if !raw_worker.busy.load(Ordering::Acquire) {
                    raw_updater.check(Trigger::Restart);
                }
            }
            return Some(1);
        }
        if msg == WM_TIMER && w == 1 {
            let phase = raw_phase.borrow();
            ui.overlay_text.set_text(&format!(
                "{}     {:.1}s",
                phase.0,
                phase.1.elapsed().as_secs_f64()
            ));
            return Some(0);
        }
        None
    })?;
    unsafe {
        if RegisterHotKey(
            hwnd.cast(),
            1,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            channel::HOTKEY_VK,
        ) == 0
        {
            return Err(format!("Cannot register hotkey: {}", crate::clipboard::error()).into());
        }
    }
    if restart_notice {
        ui.tray.show(
            "Restarting Universal Spell Check and checking for updates...",
            Some("Restarting"),
            Some(nwg::TrayNotificationFlags::INFO_ICON),
            None,
        );
    }
    logger.event("started implementation=rust");
    if let Some(path) =
        std::env::args().find_map(|a| a.strip_prefix("--ready-file=").map(str::to_owned))
    {
        std::fs::write(path, "ready")?;
    }
    if !startup_smoke {
        updater.check(Trigger::Launch);
    } else {
        bus.send(UiEvent::ReadyToExit);
    }
    if start_dashboard {
        dashboard.open(start_settings);
    }
    nwg::dispatch_thread_events();
    unsafe {
        UnregisterHotKey(hwnd.cast(), 1);
        KillTimer(hwnd.cast(), 1);
    }
    dashboard.close();
    worker.stop();
    nwg::unbind_raw_event_handler(&raw)?;
    nwg::unbind_event_handler(&handler);
    logger.event("stopped");
    logger.flush();
    Ok(())
}
pub fn forward_restart() -> Result<bool, String> {
    if channel::IS_DEV {
        return Err("Dev does not auto-update; rebuild and relaunch it.".into());
    }
    unsafe {
        let hwnd = FindWindowW(null(), wide(&channel::hotkey_title()).as_ptr());
        if hwnd.is_null() {
            return Ok(false);
        }
        let message = RegisterWindowMessageW(wide(&channel::update_message()).as_ptr());
        let mut accepted = 0;
        let result =
            SendMessageTimeoutW(hwnd, message, 0, 0, SMTO_ABORTIFHUNG, 2000, &mut accepted);
        if result == 0 || accepted != 1 {
            return Err("The running app did not acknowledge the restart request.".into());
        }
        Ok(true)
    }
}
