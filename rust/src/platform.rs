use crate::{
    channel,
    store::{Store, wide},
};
use std::{
    fs,
    path::Path,
    ptr::{null, null_mut},
};
use windows_sys::Win32::{
    Foundation::*,
    System::{Registry::*, Threading::*},
    UI::Shell::*,
};
pub struct Mutex(HANDLE);
impl Drop for Mutex {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
impl Mutex {
    pub fn instance() -> Result<Option<Self>, String> {
        unsafe {
            let handle = CreateMutexW(null(), 0, wide(channel::MUTEX).as_ptr());
            if handle.is_null() {
                return Err(crate::clipboard::error());
            }
            if GetLastError() == ERROR_ALREADY_EXISTS {
                CloseHandle(handle);
                return Ok(None);
            }
            Ok(Some(Self(handle)))
        }
    }
}
pub fn open(path: impl AsRef<Path>) {
    unsafe {
        ShellExecuteW(
            null_mut(),
            wide("open").as_ptr(),
            wide(&path.as_ref().to_string_lossy()).as_ptr(),
            null(),
            null(),
            1,
        );
    }
}
pub fn startup(enabled: bool) -> Result<(), String> {
    if channel::IS_DEV {
        return Ok(());
    }
    unsafe {
        let mut key = null_mut();
        let status = RegCreateKeyExW(
            HKEY_CURRENT_USER,
            wide("Software\\Microsoft\\Windows\\CurrentVersion\\Run").as_ptr(),
            0,
            null(),
            0,
            KEY_SET_VALUE,
            null(),
            &mut key,
            null_mut(),
        );
        if status != 0 {
            return Err(format!("Cannot open startup registration: {status}"));
        }
        let name = wide(channel::MUTEX);
        let status = if enabled {
            let value = wide(&format!(
                "\"{}\"",
                std::env::current_exe()
                    .map_err(|e| e.to_string())?
                    .display()
            ));
            let bytes = std::slice::from_raw_parts(value.as_ptr().cast::<u8>(), value.len() * 2);
            RegSetValueExW(
                key,
                name.as_ptr(),
                0,
                REG_SZ,
                bytes.as_ptr(),
                bytes.len() as u32,
            )
        } else {
            RegDeleteValueW(key, name.as_ptr())
        };
        RegCloseKey(key);
        if status != 0 && status != ERROR_FILE_NOT_FOUND {
            return Err(format!("Cannot update startup registration: {status}"));
        }
        Ok(())
    }
}
pub fn first_run(store: &Store) -> Result<(), String> {
    if channel::IS_DEV {
        return Ok(());
    }
    let flag = store.directory.join("startup.initialized");
    if flag.exists() {
        return Ok(());
    }
    startup(true)?;
    fs::create_dir_all(&store.directory).map_err(|e| e.to_string())?;
    fs::write(flag, time::OffsetDateTime::now_utc().to_string()).map_err(|e| e.to_string())
}
pub fn migrate(store: &Store) -> Result<(), String> {
    let base = std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA missing")?;
    let base = std::path::PathBuf::from(base);
    let legacy = base.join(channel::LEGACY_FOLDER);
    let destination = base.join(channel::SHARED_DATA_FOLDER);
    fs::create_dir_all(&destination).map_err(|e| e.to_string())?;
    unsafe {
        let handle = CreateMutexW(null(), 0, wide(channel::MIGRATION_MUTEX).as_ptr());
        if handle.is_null() {
            return Err(crate::clipboard::error());
        }
        let result = WaitForSingleObject(handle, 10000);
        if result != WAIT_OBJECT_0 && result != WAIT_ABANDONED {
            CloseHandle(handle);
            return Err("Legacy migration busy".into());
        }
        let result = (|| {
            for name in [
                "settings.json",
                "apikey.dat",
                "startup.initialized",
                "last-update-check.txt",
            ] {
                let source = legacy.join(name);
                let target = destination.join(name);
                let newer = name != "apikey.dat"
                    && source
                        .metadata()
                        .and_then(|m| m.modified())
                        .ok()
                        .zip(target.metadata().and_then(|m| m.modified()).ok())
                        .is_some_and(|(a, b)| a > b);
                if source.exists() && (!target.exists() || newer) {
                    fs::copy(source, target).map_err(|e| e.to_string())?;
                }
            }
            let old_logs = legacy.join("logs");
            fs::create_dir_all(&store.log_directory).map_err(|e| e.to_string())?;
            for entry in fs::read_dir(old_logs).into_iter().flatten().flatten() {
                let path = entry.path();
                let name = entry.file_name();
                if !name.to_string_lossy().starts_with("spellcheck-")
                    || path.extension().is_none_or(|s| s != "jsonl")
                {
                    continue;
                }
                let target = store.log_directory.join(name);
                if !target.exists() {
                    fs::copy(&path, &target).map_err(|e| e.to_string())?;
                } else {
                    let marker = destination.join(".migrated-from-install-directory-v1");
                    let changed = path
                        .metadata()
                        .and_then(|m| m.modified())
                        .ok()
                        .zip(marker.metadata().and_then(|m| m.modified()).ok())
                        .is_none_or(|(source, previous)| source > previous);
                    if changed {
                        use std::io::{BufRead, BufReader, Write};
                        let mut seen: std::collections::HashSet<String> =
                            BufReader::new(fs::File::open(&target).map_err(|e| e.to_string())?)
                                .lines()
                                .collect::<Result<_, _>>()
                                .map_err(|e| e.to_string())?;
                        let mut append = fs::OpenOptions::new()
                            .append(true)
                            .open(&target)
                            .map_err(|e| e.to_string())?;
                        for line in
                            BufReader::new(fs::File::open(&path).map_err(|e| e.to_string())?)
                                .lines()
                        {
                            let line = line.map_err(|e| e.to_string())?;
                            if seen.insert(line.clone()) {
                                writeln!(append, "{line}").map_err(|e| e.to_string())?;
                            }
                        }
                    }
                }
            }
            fs::write(
                destination.join(".migrated-from-install-directory-v1"),
                time::OffsetDateTime::now_utc().to_string(),
            )
            .map_err(|e| e.to_string())?;
            Ok(())
        })();
        ReleaseMutex(handle);
        CloseHandle(handle);
        result
    }
}

pub fn startup_registered() -> bool {
    if channel::IS_DEV {
        return false;
    }
    unsafe {
        let mut length = 0;
        RegGetValueW(
            HKEY_CURRENT_USER,
            wide("Software\\Microsoft\\Windows\\CurrentVersion\\Run").as_ptr(),
            wide(channel::MUTEX).as_ptr(),
            RRF_RT_REG_SZ,
            null_mut(),
            null_mut(),
            &mut length,
        ) == 0
            && length > 2
    }
}
