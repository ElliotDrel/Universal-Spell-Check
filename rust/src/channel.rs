include!(concat!(env!("OUT_DIR"), "/channel.rs"));
pub fn hotkey_title() -> String {
    format!("{MUTEX}.HotkeyWindow")
}
pub fn update_message() -> String {
    format!("{MUTEX}.UpdateRequested")
}
