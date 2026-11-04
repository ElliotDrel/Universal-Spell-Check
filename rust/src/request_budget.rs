//! Benchmark-only durable send reservations. Normal application runs have no budget.
use std::{
    fs::{File, OpenOptions},
    io::Write,
    path::Path,
    sync::{Mutex, OnceLock},
};

struct Budget {
    ledger: File,
    used: u32,
    limit: u32,
    failed: bool,
}
static BUDGET: OnceLock<Mutex<Budget>> = OnceLock::new();
static CAMPAIGN: OnceLock<std::path::PathBuf> = OnceLock::new();

pub fn campaign(path: &Path) -> Result<(), String> {
    CAMPAIGN
        .set(path.to_owned())
        .map_err(|_| "Campaign already configured".into())
}

fn reserve_campaign() -> Result<(), String> {
    let Some(path) = CAMPAIGN.get() else {
        return Ok(());
    };
    reserve_campaign_at(path)
}

fn reserve_campaign_at(path: &Path) -> Result<(), String> {
    use std::{
        io::{Read, Seek, SeekFrom},
        os::windows::fs::OpenOptionsExt,
    };
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
    let mut file = loop {
        match OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .share_mode(0)
            .open(path)
        {
            Ok(file) => break file,
            Err(e) if std::time::Instant::now() < deadline && e.raw_os_error() == Some(32) => {
                std::thread::sleep(std::time::Duration::from_millis(5))
            }
            Err(e) => return Err(format!("Campaign ledger unavailable: {e}")),
        }
    };
    let mut raw = String::new();
    file.read_to_string(&mut raw).map_err(|e| e.to_string())?;
    if !raw.is_empty() && !raw.ends_with('\n') {
        return Err("Incomplete campaign reservation".into());
    }
    let mut count = 0;
    for line in raw.lines() {
        count += 1;
        if line.parse::<u32>().ok() != Some(count) {
            return Err("Corrupt campaign ledger".into());
        }
    }
    if count >= 2100 {
        return Err("Campaign HTTP ceiling exhausted".into());
    }
    file.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
    writeln!(file, "{}", count + 1)
        .and_then(|_| file.sync_all())
        .map_err(|e| e.to_string())
}

pub fn initialize(path: &Path, limit: u32) -> Result<(), String> {
    if !(1..=100).contains(&limit) {
        return Err("HTTP attempt limit must be 1..=100".into());
    }
    let ledger = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| format!("Cannot create HTTP reservation ledger: {e}"))?;
    BUDGET
        .set(Mutex::new(Budget {
            ledger,
            used: 0,
            limit,
            failed: false,
        }))
        .map_err(|_| "HTTP budget already initialized".into())
}

impl Budget {
    fn reserve(&mut self) -> Result<(), String> {
        if self.failed || self.used >= self.limit {
            return Err("HTTP attempt budget exhausted or ledger unavailable".into());
        }
        reserve_campaign()?;
        self.used += 1;
        // Persist before sending. A crash in the following gap consumes the slot:
        // reservations are a conservative upper bound, never proof of billing.
        if let Err(error) = writeln!(
            self.ledger,
            "{{\"reservation\":{},\"limit\":{}}}",
            self.used, self.limit
        )
        .and_then(|_| self.ledger.sync_all())
        {
            self.failed = true;
            return Err(format!("Cannot persist HTTP reservation: {error}"));
        }
        Ok(())
    }
}

pub fn reserve() -> Result<(), String> {
    match BUDGET.get() {
        Some(budget) => budget
            .lock()
            .map_err(|_| "HTTP budget lock poisoned".to_string())?
            .reserve(),
        None => Ok(()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Temp(std::path::PathBuf);
    impl Temp {
        fn new(label: &str) -> Self {
            Self(std::env::temp_dir().join(format!(
                    "usc-{label}-{}-{}.tmp",
                    std::process::id(),
                    std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .unwrap()
                        .as_nanos()
                )))
        }
    }
    impl Drop for Temp {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    #[test]
    fn campaign_persists_ceiling_and_refuses_partial_records() {
        let temp = Temp::new("budget-test");
        let path = &temp.0;
        let contents = (1..=2099).map(|i| format!("{i}\n")).collect::<String>();
        std::fs::write(path, contents).unwrap();
        reserve_campaign_at(path).unwrap();
        assert!(reserve_campaign_at(path).is_err());
        std::fs::write(path, "1\n2").unwrap();
        assert!(reserve_campaign_at(path).is_err());
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn ledger_failure_blocks_every_later_send() {
        let temp = Temp::new("budget-test");
        let path = &temp.0;
        File::create_new(path).unwrap();
        let mut budget = Budget {
            ledger: File::open(path).unwrap(),
            used: 0,
            limit: 100,
            failed: false,
        };
        assert!(budget.reserve().is_err());
        assert!(budget.failed);
        assert!(budget.reserve().is_err());
        assert_eq!(budget.used, 1);
        drop(budget);
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn retries_share_the_same_durable_limit() {
        let temp = Temp::new("budget-test");
        let path = &temp.0;
        let ledger = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)
            .unwrap();
        let mut budget = Budget {
            ledger,
            used: 0,
            limit: 100,
            failed: false,
        };
        for _ in 0..100 {
            budget.reserve().unwrap();
        }
        assert!(budget.reserve().is_err());
        assert_eq!(std::fs::read_to_string(path).unwrap().lines().count(), 100);
        drop(budget);
        std::fs::remove_file(path).unwrap();
    }
}
