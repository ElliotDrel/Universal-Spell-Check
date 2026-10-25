#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
#[cfg(feature = "acceptance")]
mod acceptance;
mod activity;
mod app;
mod benchmark;
mod channel;
mod cli;
mod clipboard;
mod core;
mod dashboard;
mod desktop_bench;
mod diff;
mod docs_native;
mod evidence;
mod evidence_storage;
mod http;
mod logging;
mod platform;
mod request_probe;
mod rich;
mod rich_links;
mod runtime;
mod store;
mod style;
mod terminal;
mod timing;
mod update;
fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.iter().any(|arg| arg == "--history-variant")
        && (!cfg!(feature = "history-experiments")
            || args.get(1).is_none_or(|arg| arg != "--bench-desktop"))
    {
        eprintln!("--history-variant is available only in a history-experiments desktop benchmark");
        std::process::exit(2);
    }
    #[cfg(feature = "acceptance")]
    if let Err(error) = acceptance::validate() {
        eprintln!("Acceptance isolation rejected: {error}");
        std::process::exit(1);
    }
    let application_mode = !args.get(1).is_some_and(|arg| {
        matches!(
            arg.as_str(),
            "--transform-json"
                | "--rich-json"
                | "--bench-desktop"
                | "--bench-compute"
                | "--bench-request"
        )
    });
    let result = if args.get(1).is_some_and(|s| s == "--transform-json") {
        cli::transform_stdin(args.get(2).map_or("../replacements.json", String::as_str))
    } else if args.get(1).is_some_and(|s| s == "--rich-json") {
        cli::rich_stdin()
    } else if args.get(1).is_some_and(|s| s == "--bench-desktop") {
        desktop_bench::run(&args)
    } else if args.get(1).is_some_and(|s| s == "--bench-request") {
        request_probe::run(&args)
    } else if args.get(1).is_some_and(|s| s == "--bench-compute") {
        benchmark::compute(&args)
    } else {
        velopack::VelopackApp::build()
            .set_auto_apply_on_startup(false)
            .run();
        (|| -> Result<(), String> {
            let restart = args.iter().any(|a| a == "--restart" || a == "--update");
            if restart && app::forward_restart()? {
                return Ok(());
            }
            let store = store::Store::standard()?;
            app::run(
                store,
                args.iter().any(|a| a == "--dashboard" || a == "--settings"),
                args.iter().any(|a| a == "--settings"),
                args.iter().any(|a| a == "--startup-smoke"),
                restart,
            )
            .map_err(|e| e.to_string())
        })()
    };
    if let Err(error) = result {
        eprintln!("{error}");
        if application_mode {
            unsafe {
                windows_sys::Win32::UI::WindowsAndMessaging::MessageBoxW(
                    std::ptr::null_mut(),
                    store::wide(&error).as_ptr(),
                    store::wide(channel::DISPLAY_NAME).as_ptr(),
                    windows_sys::Win32::UI::WindowsAndMessaging::MB_OK
                        | windows_sys::Win32::UI::WindowsAndMessaging::MB_ICONERROR,
                );
            }
        }
        std::process::exit(1);
    }
}
