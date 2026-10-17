use crate::{channel, core::Settings};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};
use windows_sys::Win32::{Foundation::*, Security::Cryptography::*};
use zeroize::{Zeroize, Zeroizing};

pub fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}
fn win_error() -> String {
    std::io::Error::last_os_error().to_string()
}
#[derive(Clone)]
pub struct Store {
    pub directory: PathBuf,
    pub log_directory: PathBuf,
    pub replacements_path: PathBuf,
}
impl Store {
    pub fn standard() -> Result<Self, String> {
        let base = std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA is unavailable")?;
        let base = PathBuf::from(base);
        let directory = base.join(channel::DATA_FOLDER);
        let mut ancestor = std::env::current_exe()
            .map_err(|e| e.to_string())?
            .parent()
            .unwrap()
            .to_owned();
        let replacements_path = loop {
            let p = ancestor.join("replacements.json");
            if p.exists() {
                break p;
            }
            if !ancestor.pop() {
                break directory.join("replacements.json");
            }
        };
        Ok(Self {
            directory,
            log_directory: base.join(channel::SHARED_DATA_FOLDER).join("logs"),
            replacements_path,
        })
    }
    pub fn isolated(directory: PathBuf) -> Self {
        Self {
            log_directory: directory.join("logs"),
            replacements_path: directory.join("replacements.json"),
            directory,
        }
    }
    pub fn load_settings(&self) -> Result<Settings, String> {
        let path = self.directory.join("settings.json");
        if !path.exists() {
            return Ok(Settings::default());
        }
        let raw = fs::read_to_string(path).map_err(|e| e.to_string())?;
        serde_json::from_str(raw.trim_start_matches('\u{feff}')).map_err(|e| e.to_string())
    }
    pub fn save_settings(&self, settings: &Settings) -> Result<(), String> {
        atomic_write(
            &self.directory.join("settings.json"),
            &serde_json::to_vec_pretty(settings).map_err(|e| e.to_string())?,
        )
    }
    pub fn load_keys(&self) -> Result<Keys, String> {
        let path = self.directory.join("apikey.dat");
        if !path.exists() {
            return Ok(Keys::default());
        }
        let encrypted = fs::read(path).map_err(|e| e.to_string())?;
        let bytes = Zeroizing::new(unprotect(&encrypted)?);
        let plaintext = Zeroizing::new(
            String::from_utf8(bytes.to_vec()).map_err(|_| "Invalid encrypted key data")?,
        );
        let mut keys: Keys = if plaintext.trim_start().starts_with('{') {
            serde_json::from_str(&plaintext)
                .map_err(|_| "The encrypted key file could not be read; it was left unchanged.")?
        } else if plaintext.trim().is_empty() {
            Keys::default()
        } else {
            Keys {
                active_key_id: Some("legacy".into()),
                keys: vec![Key {
                    id: "legacy".into(),
                    name: "Default".into(),
                    value: plaintext.to_string(),
                }],
                ..Default::default()
            }
        };
        keys.keys
            .retain(|k| !k.id.trim().is_empty() && !k.value.trim().is_empty());
        if !keys
            .keys
            .iter()
            .any(|k| Some(&k.id) == keys.active_key_id.as_ref())
        {
            keys.active_key_id = keys.keys.first().map(|k| k.id.clone());
        }
        Ok(keys)
    }
    pub fn save_keys(&self, keys: &Keys) -> Result<(), String> {
        // A write must never turn a corrupt/decryption-failed collection into an empty replacement.
        let _existing = self.load_keys()?;
        let plaintext = Zeroizing::new(
            serde_json::to_vec(keys).map_err(|_| "Cannot serialize encrypted key collection")?,
        );
        let encrypted = protect(&plaintext)?;
        atomic_write(&self.directory.join("apikey.dat"), &encrypted)
    }
}
#[derive(Default, Serialize, Deserialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Keys {
    pub active_key_id: Option<String>,
    pub keys: Vec<Key>,
    #[serde(flatten)]
    pub extra: serde_json::Map<String, serde_json::Value>,
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Key {
    pub id: String,
    pub name: String,
    pub value: String,
}
impl Drop for Key {
    fn drop(&mut self) {
        self.value.zeroize();
    }
}
impl Keys {
    pub fn active(&self) -> Option<&str> {
        self.keys
            .iter()
            .find(|k| Some(&k.id) == self.active_key_id.as_ref())
            .or(self.keys.first())
            .map(|k| k.value.as_str())
    }
    pub fn add(&mut self, name: &str, value: &str) -> Result<(), String> {
        let name = name.trim();
        let value = value.trim();
        if name.is_empty() || name.encode_utf16().count() > 64 || value.is_empty() {
            return Err("Enter a key and a name of 1–64 characters.".into());
        }
        if self
            .keys
            .iter()
            .any(|k| k.name.to_lowercase() == name.to_lowercase())
        {
            return Err("An API key with that name already exists.".into());
        }
        let id = new_id();
        self.active_key_id = Some(id.clone());
        self.keys.push(Key {
            id,
            name: name.into(),
            value: value.into(),
        });
        Ok(())
    }
}
pub fn new_id() -> String {
    unsafe {
        let mut bytes = [0u8; 16];
        if BCryptGenRandom(
            std::ptr::null_mut(),
            bytes.as_mut_ptr(),
            16,
            BCRYPT_USE_SYSTEM_PREFERRED_RNG,
        ) != 0
        {
            panic!("System random generator failed");
        }
        bytes.iter().map(|b| format!("{b:02x}")).collect()
    }
}
pub fn mask(value: &str) -> String {
    let chars: Vec<_> = value.chars().collect();
    if chars.len() <= 4 {
        "•".repeat(chars.len())
    } else {
        format!(
            "{}…{}",
            chars[..3.min(chars.len() - 4)].iter().collect::<String>(),
            chars[chars.len() - 4..].iter().collect::<String>()
        )
    }
}
fn crypt(bytes: &[u8], encrypt: bool) -> Result<Vec<u8>, String> {
    unsafe {
        let input = CRYPT_INTEGER_BLOB {
            cbData: bytes.len().try_into().map_err(|_| "Key data too large")?,
            pbData: bytes.as_ptr().cast_mut(),
        };
        let mut output: CRYPT_INTEGER_BLOB = std::mem::zeroed();
        let ok = if encrypt {
            CryptProtectData(
                &input,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null_mut(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        } else {
            CryptUnprotectData(
                &input,
                std::ptr::null_mut(),
                std::ptr::null(),
                std::ptr::null_mut(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        };
        if ok == 0 {
            return Err(win_error());
        }
        let result = std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec();
        if !encrypt {
            std::ptr::write_bytes(output.pbData, 0, output.cbData as usize);
        }
        LocalFree(output.pbData.cast());
        Ok(result)
    }
}
pub fn protect(bytes: &[u8]) -> Result<Vec<u8>, String> {
    crypt(bytes, true)
}
pub fn unprotect(bytes: &[u8]) -> Result<Vec<u8>, String> {
    crypt(bytes, false)
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<(), String> {
    fs::create_dir_all(path.parent().ok_or("Missing parent directory")?)
        .map_err(|e| e.to_string())?;
    let temporary = path.with_extension(format!("{}.tmp", new_id()));
    let write_result = (|| -> std::io::Result<()> {
        use std::io::Write;
        let mut file = fs::File::create(&temporary)?;
        file.write_all(bytes)?;
        file.sync_all()
    })();
    if let Err(error) = write_result {
        let _ = fs::remove_file(&temporary);
        return Err(error.to_string());
    }
    // MoveFileEx provides atomic replacement on Windows; std::fs::rename cannot overwrite there.
    unsafe {
        use windows_sys::Win32::Storage::FileSystem::*;
        if MoveFileExW(
            wide(&temporary.to_string_lossy()).as_ptr(),
            wide(&path.to_string_lossy()).as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        ) == 0
        {
            let error = win_error();
            let _ = fs::remove_file(temporary);
            return Err(error);
        }
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn dpapi_roundtrip_and_corrupt_store_refuses_write() {
        let directory = std::env::temp_dir().join(format!("usc-rust-test-{}", new_id()));
        let store = Store::isolated(directory.clone());
        let mut keys = Keys::default();
        keys.add("Test", "offline-replay-no-network").unwrap();
        store.save_keys(&keys).unwrap();
        assert_eq!(
            store.load_keys().unwrap().active(),
            Some("offline-replay-no-network")
        );
        let path = directory.join("apikey.dat");
        let bytes = fs::read(&path).unwrap();
        assert!(!bytes.windows(7).any(|b| b == b"offline"));
        fs::write(&path, b"corrupt").unwrap();
        assert!(store.save_keys(&keys).is_err());
        assert_eq!(fs::read(&path).unwrap(), b"corrupt");
        fs::remove_dir_all(directory).unwrap();
    }
}
