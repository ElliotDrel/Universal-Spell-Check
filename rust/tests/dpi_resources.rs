#[test]
fn repeated_dpi_queries_do_not_leak_screen_contexts() {
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetGuiResources};
    unsafe {
        assert!(native_windows_gui::dpi() >= 96);
        let process = GetCurrentProcess();
        let before = GetGuiResources(process, 0);
        for _ in 0..100 {
            assert!(native_windows_gui::dpi() >= 96);
        }
        let after = GetGuiResources(process, 0);
        assert!(
            after <= before + 2,
            "DPI queries grew GDI objects: {before} -> {after}"
        );
    }
}
