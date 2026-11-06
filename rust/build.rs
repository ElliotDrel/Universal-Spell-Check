use std::{env, fs, path::PathBuf};
fn main() {
    let dev = env::var_os("CARGO_FEATURE_DEV").is_some();
    let title = if dev {
        "Universal Spell Check (Dev)"
    } else {
        "Universal Spell Check"
    };
    let mut version = env::var("CARGO_PKG_VERSION").unwrap();
    if dev && !version.ends_with("-dev") {
        version.push_str("-dev");
    }
    let code = format!(
        "pub const DISPLAY_NAME: &str = {title:?};\npub const VERSION: &str = {version:?};\n"
    );
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    fs::write(out.join("channel.rs"), code).unwrap();
    let icon = if dev {
        "assets/app-dev.ico"
    } else {
        "assets/app.ico"
    };
    let absolute = |path: &str| {
        fs::canonicalize(path)
            .unwrap()
            .to_string_lossy()
            .replace('\\', "/")
    };
    let (ico, manifest) = (absolute(icon), absolute("app.manifest"));
    let rc = out.join("app.rc");
    let numeric = [
        "CARGO_PKG_VERSION_MAJOR",
        "CARGO_PKG_VERSION_MINOR",
        "CARGO_PKG_VERSION_PATCH",
    ]
    .map(|key| env::var(key).unwrap())
    .join(",");
    fs::write(
        &rc,
        format!(
            r#"1 24 "{manifest}"
1 ICON "{ico}"
1 VERSIONINFO
FILEVERSION {numeric},0
PRODUCTVERSION {numeric},0
FILEFLAGSMASK 0x3fL
FILEOS 0x40004L
FILETYPE 0x1L
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904b0"
  BEGIN
   VALUE "FileDescription", "{title}"
   VALUE "FileVersion", "{version}"
   VALUE "ProductName", "{title}"
   VALUE "ProductVersion", "{version}"
   VALUE "OriginalFilename", "UniversalSpellCheck.exe"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"#
        ),
    )
    .unwrap();
    embed_resource::compile(rc, embed_resource::NONE)
        .manifest_required()
        .unwrap();
    for path in ["app.manifest", "assets/app-dev.ico", "assets/app.ico"] {
        println!("cargo:rerun-if-changed={path}");
    }
}
