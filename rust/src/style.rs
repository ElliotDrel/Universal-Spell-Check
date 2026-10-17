use native_windows_gui as nwg;
use std::{ptr::null_mut, sync::OnceLock};
use windows_sys::Win32::Graphics::Gdi::*;
static FONTS: OnceLock<Vec<usize>> = OnceLock::new();
pub fn initialize_fonts() {
    FONTS.get_or_init(|| unsafe {
        [
            include_bytes!("../assets/InstrumentSerif-Regular.ttf").as_slice(),
            include_bytes!("../assets/InstrumentSans.ttf").as_slice(),
            include_bytes!("../assets/JetBrainsMono.ttf").as_slice(),
        ]
        .iter()
        .map(|bytes| {
            let mut count = 0;
            AddFontMemResourceEx(
                bytes.as_ptr().cast(),
                bytes.len() as u32,
                null_mut(),
                std::ptr::addr_of_mut!(count),
            ) as usize
        })
        .collect()
    });
}
pub fn font(family: &str, size: u32) -> Result<nwg::Font, nwg::NwgError> {
    let mut font = nwg::Font::default();
    nwg::Font::builder()
        .family(family)
        .size_absolute(size)
        .build(&mut font)?;
    Ok(font)
}
pub fn icon() -> Result<nwg::Icon, nwg::NwgError> {
    let mut icon = nwg::Icon::default();
    let bytes = if crate::channel::IS_DEV {
        include_bytes!("../../src/Assets/app-dev.ico").as_slice()
    } else {
        include_bytes!("../../src/Assets/app.ico").as_slice()
    };
    nwg::Icon::builder()
        .source_bin(Some(bytes))
        .build(&mut icon)?;
    Ok(icon)
}
pub fn paint_background(
    hwnd: windows_sys::Win32::Foundation::HWND,
    dc: windows_sys::Win32::Graphics::Gdi::HDC,
) -> isize {
    use windows_sys::Win32::{Graphics::Gdi::*, UI::WindowsAndMessaging::GetClientRect};
    static BRUSH: OnceLock<usize> = OnceLock::new();
    let brush =
        *BRUSH.get_or_init(|| unsafe { CreateSolidBrush(245 | (241 << 8) | (234 << 16)) as usize });
    unsafe {
        let mut rect = std::mem::zeroed();
        GetClientRect(hwnd, &mut rect);
        FillRect(dc, &rect, brush as _);
    }
    1
}
