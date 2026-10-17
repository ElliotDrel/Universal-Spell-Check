#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod channel;
mod cli;
mod clipboard;
mod core;
mod diff;
mod docs_native;
mod http;
mod logging;
mod rich;
mod store;
fn main() {
    let args: Vec<String> = std::env::args().collect();
    let result = if args.get(1).is_some_and(|s| s == "--transform-json") {
        cli::transform_stdin(args.get(2).map_or("../replacements.json", String::as_str))
    } else if args.get(1).is_some_and(|s| s == "--rich-json") {
        cli::rich_stdin()
    } else {
        Ok(())
    };
    if let Err(error) = result {
        eprintln!("{error}");
        std::process::exit(1);
    }
}
