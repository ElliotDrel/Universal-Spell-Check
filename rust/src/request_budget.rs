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
    #[test]
    fn ledger_failure_blocks_every_later_send() {
        let path =
            std::env::temp_dir().join(format!("usc-budget-fail-{}.jsonl", std::process::id()));
        File::create_new(&path).unwrap();
        let mut budget = Budget {
            ledger: File::open(&path).unwrap(),
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
        let path = std::env::temp_dir().join(format!("usc-budget-{}.jsonl", std::process::id()));
        let ledger = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
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
        assert_eq!(std::fs::read_to_string(&path).unwrap().lines().count(), 100);
        drop(budget);
        std::fs::remove_file(path).unwrap();
    }
}
