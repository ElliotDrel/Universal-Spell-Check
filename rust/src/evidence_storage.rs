use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    fs, io,
    path::{Path, PathBuf},
    time::{Duration, SystemTime},
};

pub const STORE_LIMIT: u64 = 256 * 1024 * 1024;
const PAYLOAD_LIMIT: usize = 16 * 1024 * 1024;
const ORPHAN_AGE: Duration = Duration::from_secs(24 * 60 * 60);
const PAYLOADS: &[&str] = &[
    "input_text",
    "clipboard_html",
    "clipboard_rtf",
    "clipboard_formats",
    "raw_ai_output",
    "output_text",
    "paste_text",
    "paste_html",
    "clipboard_chromium_mime_base64",
    "paste_chromium_mime_base64",
    "readback_text",
    "readback_html",
    "readback_rtf",
    "readback_formats",
    "readback_chromium_mime_base64",
];

#[derive(Default, Debug)]
pub struct Rotation {
    pub runs: usize,
    pub bytes: u64,
    pub blocked_runs: usize,
}

fn ordinary(meta: &fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return false;
        } // Reparse points may leave the store.
    }
    !meta.file_type().is_symlink()
}
fn run_id(id: &str) -> bool {
    id.len() == 32 && id.bytes().all(|b| b.is_ascii_hexdigit())
}
fn artifact(name: &str) -> bool {
    matches!(
        name,
        "manifest.json" | "before-source.png" | "after-paste.png"
    ) || name
        .strip_suffix(".txt")
        .is_some_and(|s| PAYLOADS.contains(&s))
}
fn invalid() -> io::Error {
    io::Error::other("Unexpected evidence storage path")
}
fn directory(path: &Path) -> io::Result<()> {
    let meta = fs::symlink_metadata(path)?;
    if !ordinary(&meta) || !meta.is_dir() {
        return Err(invalid());
    }
    Ok(())
}

// Count actual files on pressure, repairing old reservations after failed writes or manual cleanup.
fn size(root: &Path) -> io::Result<u64> {
    let mut pending = vec![root.to_owned()];
    let mut bytes = 0u64;
    while let Some(dir) = pending.pop() {
        for entry in fs::read_dir(dir)? {
            let entry = entry?;
            if entry.path() == root.join(".reserved-bytes") {
                continue;
            }
            let meta = entry.metadata()?;
            if !ordinary(&meta) {
                continue;
            }
            if meta.is_dir() {
                pending.push(entry.path());
            } else {
                bytes = bytes.saturating_add(meta.len());
            }
        }
    }
    Ok(bytes)
}

fn candidate(path: &Path) -> io::Result<Option<(SystemTime, Vec<PathBuf>, u64)>> {
    let meta = fs::symlink_metadata(path)?;
    if !ordinary(&meta) || !meta.is_dir() {
        return Ok(None);
    }
    let mut files = Vec::new();
    let mut bytes = 0;
    let mut modified = meta.modified()?;
    let mut complete = false;
    for entry in fs::read_dir(path)? {
        let entry = entry?;
        let meta = entry.metadata()?;
        if !ordinary(&meta) || !meta.is_file() || !artifact(&entry.file_name().to_string_lossy()) {
            return Ok(None); // Preserve backups, custom files, nested folders, and links.
        }
        modified = modified.max(meta.modified()?);
        complete |= entry.file_name() == "manifest.json";
        bytes += meta.len();
        files.push(entry.path());
    }
    if !complete
        && SystemTime::now()
            .duration_since(modified)
            .unwrap_or_default()
            < ORPHAN_AGE
    {
        return Ok(None); // Another process may still be writing a run without a manifest.
    }
    Ok(Some((modified, files, bytes)))
}

// Caller holds the shared cross-channel evidence mutex for this entire operation.
pub fn write_locked(
    root: &Path,
    id: &str,
    name: &str,
    bytes: &[u8],
    limit: u64,
) -> io::Result<(Value, Rotation)> {
    let mut rotation = Rotation::default();
    if !run_id(id) || !artifact(name) {
        return Err(invalid());
    }
    if bytes.len() > PAYLOAD_LIMIT {
        return Ok((json!({"status":"size_limit","complete":false}), rotation));
    }
    match fs::symlink_metadata(root) {
        Ok(_) => directory(root)?,
        Err(e) if e.kind() == io::ErrorKind::NotFound => fs::create_dir_all(root)?,
        Err(e) => return Err(e),
    }
    directory(root)?;
    let counter = root.join(".reserved-bytes");
    match fs::symlink_metadata(&counter) {
        Ok(meta) if ordinary(&meta) && meta.is_file() => (),
        Ok(_) => return Err(invalid()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e),
    }
    let run = root.join(id);
    match fs::symlink_metadata(&run) {
        Ok(_) => directory(&run)?,
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e),
    }
    let target = run.join(name);
    let replaced = match fs::symlink_metadata(&target) {
        Ok(meta) if ordinary(&meta) && meta.is_file() => meta.len(),
        Ok(_) => return Err(invalid()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => 0,
        Err(e) => return Err(e),
    };
    let additional = (bytes.len() as u64).saturating_sub(replaced);
    let cached = fs::read_to_string(&counter)
        .ok()
        .and_then(|s| s.trim().parse::<u64>().ok());
    let mut used = match cached {
        Some(used) => used,
        None => size(root)?,
    };
    if used.saturating_add(additional) > limit {
        used = size(root)?;
        if used.saturating_add(additional) > limit {
            let mut candidates = Vec::new();
            for entry in fs::read_dir(root)? {
                let entry = entry?;
                let name = entry.file_name();
                if name == id || !run_id(&name.to_string_lossy()) {
                    continue;
                }
                match candidate(&entry.path()) {
                    Ok(Some((age, _, _))) => candidates.push((age, entry.path())),
                    Ok(None) => (),
                    Err(_) => rotation.blocked_runs += 1,
                }
            }
            candidates.sort_by_key(|(age, path)| (*age, path.clone()));
            let target_used = (limit * 3 / 4).saturating_sub(additional);
            for (_, path) in candidates {
                if used <= target_used {
                    break;
                }
                let canonical_root = root.canonicalize()?;
                let canonical_path = match path.canonicalize() {
                    Ok(path) => path,
                    Err(_) => {
                        rotation.blocked_runs += 1;
                        continue;
                    }
                };
                if canonical_path.parent() != Some(canonical_root.as_path()) {
                    return Err(invalid());
                }
                let mut files = match candidate(&path) {
                    Ok(Some((_, files, _))) => files,
                    Ok(None) => continue,
                    Err(_) => {
                        rotation.blocked_runs += 1;
                        continue;
                    }
                };
                // Keep the completion marker until every payload is removed, even if a viewer holds one open.
                files.sort_by_key(|file| {
                    file.file_name().is_some_and(|name| name == "manifest.json")
                });
                let mut blocked = false;
                for file in files {
                    let bytes = match fs::symlink_metadata(&file) {
                        Ok(meta) => meta.len(),
                        Err(_) => {
                            blocked = true;
                            break;
                        }
                    };
                    if fs::remove_file(&file).is_err() {
                        blocked = true;
                        break;
                    }
                    used = used.saturating_sub(bytes);
                    rotation.bytes += bytes;
                }
                if blocked || fs::remove_dir(path).is_err() {
                    rotation.blocked_runs += 1;
                } else {
                    rotation.runs += 1;
                }
            }
        }
        fs::write(&counter, used.to_string())?;
    }
    if used.saturating_add(additional) > limit {
        return Ok((
            json!({"status":"storage_limit","complete":false,"stored_bytes":used,"required_bytes":additional}),
            rotation,
        ));
    }
    fs::create_dir_all(&run)?;
    // Reserve before writing: a failed write cannot allow concurrent over-allocation.
    fs::write(&counter, used.saturating_add(additional).to_string())?;
    fs::write(target, bytes)?;
    Ok((
        json!({"path":name,"sha256":format!("{:x}",Sha256::digest(bytes)),
        "bytes":bytes.len(),"complete":true,"status":"ok"}),
        rotation,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Temp(PathBuf);
    impl Temp {
        fn new() -> Self {
            let p = std::env::temp_dir()
                .join(format!("usc-evidence-retention-{}", crate::store::new_id()));
            fs::create_dir_all(&p).unwrap();
            Self(p)
        }
    }
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    fn modified(path: &Path, time: SystemTime) {
        #[cfg(windows)]
        let file = {
            use std::os::windows::fs::OpenOptionsExt;
            fs::OpenOptions::new()
                .access_mode(0x100)
                .custom_flags(0x02000000)
                .open(path)
                .unwrap()
        };
        #[cfg(not(windows))]
        let file = fs::File::open(path).unwrap();
        file.set_times(fs::FileTimes::new().set_modified(time))
            .unwrap();
    }
    fn completed(root: &Path, id: &str, bytes: usize) -> PathBuf {
        let dir = root.join(id);
        fs::create_dir(&dir).unwrap();
        fs::write(dir.join("input_text.txt"), vec![b'x'; bytes]).unwrap();
        fs::write(dir.join("manifest.json"), b"{}").unwrap();
        dir
    }
    const OLD: &str = "ffffffffffffffffffffffffffffff01";
    const NEW: &str = "00000000000000000000000000000002";
    const ACTIVE: &str = "00000000000000000000000000000003";
    #[test]
    fn full_store_evicts_oldest_whole_run_and_next_capture_continues() {
        let t = Temp::new();
        let old = completed(&t.0, OLD, 80);
        let new = completed(&t.0, NEW, 10);
        let time = SystemTime::now() - Duration::from_secs(60);
        for e in fs::read_dir(&old).unwrap() {
            modified(&e.unwrap().path(), time);
        }
        modified(&old, time);
        let (v, rotation) = write_locked(&t.0, ACTIVE, "input_text.txt", &[b'n'; 20], 100).unwrap();
        assert_eq!(v["status"], "ok");
        assert_eq!(rotation.runs, 1);
        assert!(!old.exists());
        assert!(new.join("manifest.json").exists());
        assert_eq!(
            fs::read(t.0.join(ACTIVE).join("input_text.txt")).unwrap(),
            [b'n'; 20]
        );
        assert_eq!(
            write_locked(&t.0, ACTIVE, "paste_text.txt", b"next", 100)
                .unwrap()
                .0["status"],
            "ok"
        );
    }
    #[test]
    fn stale_counter_is_repaired_without_deleting_existing_runs() {
        let t = Temp::new();
        let old = completed(&t.0, OLD, 10);
        fs::write(t.0.join(".reserved-bytes"), "100").unwrap();
        let (v, rotation) = write_locked(&t.0, NEW, "input_text.txt", b"new", 100).unwrap();
        assert_eq!(v["status"], "ok");
        assert_eq!(rotation.runs, 0);
        assert!(old.exists());
        assert_eq!(
            fs::read_to_string(t.0.join(".reserved-bytes")).unwrap(),
            "15"
        );
    }
    #[test]
    fn pressure_preserves_inflight_current_and_backup_content() {
        let t = Temp::new();
        let old = completed(&t.0, OLD, 30);
        fs::write(old.join("capture.bak"), b"keep").unwrap();
        let inflight = t.0.join(NEW);
        fs::create_dir(&inflight).unwrap();
        fs::write(inflight.join("input_text.txt"), [b'x'; 30]).unwrap();
        let current = completed(&t.0, ACTIVE, 30);
        let (v, rotation) = write_locked(&t.0, ACTIVE, "paste_text.txt", b"new", 100).unwrap();
        assert_eq!(v["status"], "storage_limit");
        assert_eq!(rotation.runs, 0);
        assert!(old.join("capture.bak").exists());
        assert!(inflight.exists());
        assert!(current.exists());
    }
    #[test]
    fn stale_incomplete_run_can_be_reclaimed_but_custom_folder_is_preserved() {
        let t = Temp::new();
        let orphan = t.0.join(OLD);
        fs::create_dir(&orphan).unwrap();
        let file = orphan.join("input_text.txt");
        fs::write(&file, [b'x'; 80]).unwrap();
        let stale = SystemTime::now() - ORPHAN_AGE - Duration::from_secs(60);
        modified(&file, stale);
        modified(&orphan, stale);
        let saved = t.0.join("saved-replay");
        fs::create_dir(&saved).unwrap();
        fs::write(saved.join("case.json"), b"{}").unwrap();
        assert_eq!(
            write_locked(&t.0, NEW, "input_text.txt", &[b'n'; 30], 100)
                .unwrap()
                .0["status"],
            "ok"
        );
        assert!(!orphan.exists());
        assert!(saved.exists());
    }
    #[test]
    fn rewrites_are_not_double_counted_and_oversized_or_escaping_paths_are_rejected() {
        let t = Temp::new();
        write_locked(&t.0, OLD, "input_text.txt", b"first", 100).unwrap();
        write_locked(&t.0, OLD, "input_text.txt", b"again", 100).unwrap();
        assert_eq!(
            fs::read_to_string(t.0.join(".reserved-bytes")).unwrap(),
            "5"
        );
        assert!(write_locked(&t.0, "../escape", "input_text.txt", b"x", 100).is_err());
        assert!(write_locked(&t.0, OLD, "../escape", b"x", 100).is_err());
        assert_eq!(
            write_locked(
                &t.0,
                NEW,
                "input_text.txt",
                &vec![0; PAYLOAD_LIMIT + 1],
                STORE_LIMIT
            )
            .unwrap()
            .0["status"],
            "size_limit"
        );
        assert!(!t.0.join(NEW).exists());
    }
    #[cfg(windows)]
    #[test]
    fn locked_oldest_file_does_not_block_other_evictions_or_erase_its_manifest() {
        use std::os::windows::fs::OpenOptionsExt;
        let t = Temp::new();
        let old = completed(&t.0, OLD, 70);
        let new = completed(&t.0, NEW, 60);
        fs::write(old.join("paste_text.txt"), [b'p'; 10]).unwrap();
        let time = SystemTime::now() - Duration::from_secs(60);
        for file in fs::read_dir(&old).unwrap() {
            modified(&file.unwrap().path(), time);
        }
        modified(&old, time);
        let held = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(old.join("paste_text.txt"))
            .unwrap();
        let (v, rotation) = write_locked(&t.0, ACTIVE, "input_text.txt", &[b'n'; 20], 100).unwrap();
        assert_eq!(v["status"], "ok");
        assert_eq!(rotation.blocked_runs, 1);
        assert!(old.join("manifest.json").exists());
        assert!(!old.join("input_text.txt").exists());
        assert!(rotation.bytes > 0);
        assert!(!new.exists());
        drop(held);
    }
    #[cfg(windows)]
    #[test]
    fn junctions_are_never_written_through_or_evicted() {
        let t = Temp::new();
        let outside = Temp::new();
        let linked = t.0.join(OLD);
        let result = std::process::Command::new("cmd")
            .args(["/C", "mklink", "/J"])
            .arg(&linked)
            .arg(&outside.0)
            .output()
            .unwrap();
        assert!(
            result.status.success(),
            "Unable to create isolated junction fixture"
        );
        fs::write(outside.0.join("input_text.txt"), [b'x'; 90]).unwrap();
        fs::write(outside.0.join("manifest.json"), b"{}").unwrap();
        assert!(write_locked(&t.0, OLD, "paste_text.txt", b"unsafe", 100).is_err());
        assert_eq!(
            write_locked(&t.0, NEW, "input_text.txt", b"new", 100)
                .unwrap()
                .0["status"],
            "ok"
        );
        assert!(outside.0.join("manifest.json").exists());
        assert!(!outside.0.join("paste_text.txt").exists());
        fs::remove_dir(&linked).unwrap();
        let linked_root = t.0.join("redirect");
        assert!(
            std::process::Command::new("cmd")
                .args(["/C", "mklink", "/J"])
                .arg(&linked_root)
                .arg(&outside.0)
                .output()
                .unwrap()
                .status
                .success()
        );
        assert!(write_locked(&linked_root, ACTIVE, "input_text.txt", b"unsafe", 100).is_err());
        fs::remove_dir(&linked_root).unwrap();
    }
}
