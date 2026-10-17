use std::{env, fs, path::PathBuf};
fn main() {
    let source = fs::read_to_string("../src/BuildChannel.cs").unwrap();
    let dev = env::var_os("CARGO_FEATURE_DEV").is_some();
    let acceptance = env::var_os("CARGO_FEATURE_ACCEPTANCE").is_some();
    assert!(
        !acceptance || !dev,
        "Acceptance builds require --no-default-features"
    );
    let conditional = source.split("#if DEV").nth(1).unwrap();
    let selected = if dev {
        conditional.split("#else").next().unwrap()
    } else {
        conditional
            .split("#else")
            .nth(1)
            .unwrap()
            .split("#endif")
            .next()
            .unwrap()
    };
    let extract = |text: &str, name: &str| -> String {
        let line = text
            .lines()
            .find(|l| l.contains(&format!(" {name} =")))
            .unwrap();
        let val = line
            .split_once('=')
            .unwrap()
            .1
            .trim()
            .split(';')
            .next()
            .unwrap();
        if val.starts_with('"') {
            val.trim_matches('"').replace("\\\\", "\\")
        } else {
            val.into()
        }
    };
    let shared = extract(&source, "SharedDataFolder");
    let mut code = format!(
        "pub const IS_DEV: bool = {dev};\npub const SHARED_DATA_FOLDER: &str = {shared:?};\n"
    );
    for (rust, cs) in [
        ("DISPLAY_NAME", "DisplayName"),
        ("CHANNEL", "ChannelName"),
        ("DATA_FOLDER", "AppDataFolder"),
        ("MUTEX", "MutexName"),
        ("HOTKEY_VK", "HotkeyVk"),
    ] {
        let mut v = if acceptance && rust == "MUTEX" {
            extract(&source, "AcceptanceMutexName")
        } else if acceptance && rust == "DISPLAY_NAME" {
            extract(&source, "AcceptanceDisplayName")
        } else {
            extract(selected, cs)
        };
        if v == "SharedDataFolder" {
            v = shared.clone();
        }
        if rust == "HOTKEY_VK" {
            code += &format!("pub const {rust}: u32 = {v};\n");
        } else {
            code += &format!("pub const {rust}: &str = {v:?};\n");
        }
    }
    for (rust, cs) in [
        ("EVIDENCE_MUTEX", "DeveloperEvidenceMutexName"),
        ("MIGRATION_MUTEX", "DataMigrationMutexName"),
        ("LEGACY_FOLDER", "LegacyInstallFolder"),
        ("BENCH_TITLE", "RustBenchmarkWindowTitle"),
    ] {
        let value = extract(&source, cs);
        code += &format!("pub const {rust}: &str = {value:?};\n");
    }
    let bench_vk = extract(&source, "BenchmarkHotkeyVk");
    code += &format!("pub const BENCH_VK:u32={bench_vk};\n");
    let mut version = env::var("CARGO_PKG_VERSION").unwrap();
    if acceptance {
        version = env::var("USC_ACCEPTANCE_VERSION").expect("Set USC_ACCEPTANCE_VERSION");
        assert!(
            [
                "0.13.0-acceptance.1",
                "0.13.0-acceptance.2",
                "0.13.0-acceptance.3"
            ]
            .contains(&version.as_str())
        );
        let root =
            fs::canonicalize(env::var("USC_ACCEPTANCE_ROOT").expect("Set USC_ACCEPTANCE_ROOT"))
                .expect("Acceptance root must exist");
        code += &format!(
            "pub const ACCEPTANCE_ROOT: &str = {:?};\n",
            root.to_string_lossy()
        );
        code += &format!(
            "pub const ACCEPTANCE_PACKAGE_ID: &str = {:?};\n",
            extract(&source, "AcceptancePackageId")
        );
    }
    for name in ["USC_ACCEPTANCE_VERSION", "USC_ACCEPTANCE_ROOT"] {
        println!("cargo:rerun-if-env-changed={name}");
    }
    if dev && !version.ends_with("-dev") {
        version.push_str("-dev");
    }
    code += &format!("pub const VERSION: &str = {version:?};\n");
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    fs::write(out.join("channel.rs"), code).unwrap();
    let ico = fs::canonicalize(if dev {
        "../src/Assets/app-dev.ico"
    } else {
        "../src/Assets/app.ico"
    })
    .unwrap()
    .to_string_lossy()
    .replace('\\', "/");
    let manifest = fs::canonicalize("app.manifest")
        .unwrap()
        .to_string_lossy()
        .replace('\\', "/");
    let rc = out.join("app.rc");
    fs::write(&rc, format!("1 24 \"{manifest}\"\n1 ICON \"{ico}\"\n")).unwrap();
    embed_resource::compile(rc, embed_resource::NONE)
        .manifest_required()
        .unwrap();
    for path in [
        "../src/BuildChannel.cs",
        "app.manifest",
        "../src/Assets/app-dev.ico",
        "../src/Assets/app.ico",
    ] {
        println!("cargo:rerun-if-changed={path}");
    }
}
