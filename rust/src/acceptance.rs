use crate::channel;
use std::path::{Path, PathBuf};

fn contained(root: &Path, path: &Path) -> Result<PathBuf, String> {
    let path = path
        .canonicalize()
        .map_err(|e| format!("{}: {e}", path.display()))?;
    if !path.starts_with(root) || path == root {
        return Err(format!(
            "Path is outside the compiled acceptance root: {}",
            path.display()
        ));
    }
    Ok(path)
}
pub fn validate() -> Result<(), String> {
    let root = Path::new(channel::ACCEPTANCE_ROOT)
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let base = PathBuf::from(std::env::var_os("LOCALAPPDATA").ok_or("LOCALAPPDATA missing")?);
    contained(&root, &base)?;
    for path in [
        base.join(channel::DATA_FOLDER),
        base.join(channel::SHARED_DATA_FOLDER).join("logs"),
    ] {
        if path.exists() {
            contained(&root, &path)?;
        }
    }
    contained(&root, &std::env::current_exe().map_err(|e| e.to_string())?)?;
    contained(&root, &root.join("feed"))?;
    let parent = std::env::current_exe()
        .map_err(|e| e.to_string())?
        .parent()
        .unwrap()
        .to_owned();
    for path in [
        parent.join("Update.exe"),
        parent.parent().unwrap_or(&parent).join("Update.exe"),
    ] {
        if path.exists() {
            contained(&root, &path)?;
        }
    }
    Ok(())
}
pub fn validate_install(locator: &velopack::locator::VelopackLocator) -> Result<(), String> {
    if locator.get_manifest_id() != channel::ACCEPTANCE_PACKAGE_ID {
        return Err("Unexpected acceptance package identity".into());
    }
    let root = Path::new(channel::ACCEPTANCE_ROOT)
        .canonicalize()
        .map_err(|e| e.to_string())?;
    contained(&root, &locator.get_update_path())?;
    contained(&root, &locator.get_root_dir())?;
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn path_containment_rejects_sibling_prefixes_and_parent_traversal() {
        let base =
            std::env::temp_dir().join(format!("usc-acceptance-paths-{}", std::process::id()));
        let root = base.join("root");
        let sibling = base.join("root-elsewhere");
        std::fs::create_dir_all(root.join("child")).unwrap();
        std::fs::create_dir_all(&sibling).unwrap();
        let canonical = root.canonicalize().unwrap();
        assert!(contained(&canonical, &root.join("child")).is_ok());
        assert!(contained(&canonical, &sibling).is_err());
        assert!(contained(&canonical, &root.join("../root-elsewhere")).is_err());
        assert!(contained(&canonical, &root).is_err());
        std::fs::remove_dir(root.join("child")).unwrap();
        std::fs::remove_dir(root).unwrap();
        std::fs::remove_dir(sibling).unwrap();
        std::fs::remove_dir(base).unwrap();
    }
}
