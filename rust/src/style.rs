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
    let resources = nwg::EmbedResource::load(None)?;
    nwg::Icon::from_embed(&resources, Some(1), None)
}
pub fn paint_background(
    hwnd: windows_sys::Win32::Foundation::HWND,
    dc: windows_sys::Win32::Graphics::Gdi::HDC,
) -> isize {
    use windows_sys::Win32::{Graphics::Gdi::*, UI::WindowsAndMessaging::GetClientRect};
    static BRUSH: OnceLock<usize> = OnceLock::new();
    let brush =
        *BRUSH.get_or_init(|| unsafe { CreateSolidBrush(255 | (255 << 8) | (255 << 16)) as usize });
    unsafe {
        let mut rect = std::mem::zeroed();
        GetClientRect(hwnd, &mut rect);
        FillRect(dc, &rect, brush as _);
    }
    1
}

// NWG owns IDs through 0xFFFF; all application subclasses use the next range.
pub const fn raw_handler_id(local: usize) -> usize {
    assert!(local <= 0xFFFF);
    0x10000 + local
}
pub fn text_background(dc: HDC) -> isize {
    static BRUSH: OnceLock<usize> = OnceLock::new();
    let color = 255 | (255 << 8) | (255 << 16);
    let brush = *BRUSH.get_or_init(|| unsafe { CreateSolidBrush(color) as usize });
    unsafe {
        SetBkColor(dc, color);
        SetTextColor(dc, 31 | (42 << 8) | (38 << 16));
    }
    brush as isize
}

pub fn dpi_scale() -> f64 {
    unsafe {
        let dc = GetDC(null_mut());
        if dc.is_null() {
            return 1.0;
        }
        let dpi = GetDeviceCaps(dc, LOGPIXELSX as i32);
        ReleaseDC(null_mut(), dc);
        f64::from(dpi.max(96)) / 96.0
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn application_handlers_are_outside_nwg_reserved_range() {
        for local in [0, 900, 901, 910, 920, 930, 0xFFFF] {
            assert!(super::raw_handler_id(local) > 0xFFFF);
        }
    }
}
