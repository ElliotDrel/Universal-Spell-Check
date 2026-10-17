use serde_json::Value;
use std::{
    fs::{self, File},
    io::{BufRead, BufReader, Read, Seek, SeekFrom},
    path::PathBuf,
};
#[derive(Clone)]
pub struct Entry {
    pub timestamp: String,
    pub data: Value,
}
#[derive(Default, Clone)]
pub struct Stats {
    pub checks: u64,
    pub corrections: u64,
    pub streak: u64,
}
struct ReverseFile {
    file: File,
    position: u64,
    pending: Vec<u8>,
}
impl ReverseFile {
    fn new(path: &PathBuf) -> std::io::Result<Self> {
        let file = File::open(path)?;
        let position = file.metadata()?.len();
        Ok(Self {
            file,
            position,
            pending: Vec::new(),
        })
    }
    fn next(&mut self) -> std::io::Result<Option<String>> {
        loop {
            if let Some(index) = self.pending.iter().rposition(|b| *b == b'\n') {
                let line = String::from_utf8_lossy(&self.pending[index + 1..])
                    .trim_end_matches('\r')
                    .to_owned();
                self.pending.truncate(index);
                if !line.is_empty() {
                    return Ok(Some(line));
                }
                continue;
            }
            if self.position == 0 {
                if self.pending.is_empty() {
                    return Ok(None);
                }
                let line = String::from_utf8_lossy(&self.pending)
                    .trim_end_matches('\r')
                    .to_owned();
                self.pending.clear();
                return Ok(Some(line));
            }
            let count = self.position.min(65536) as usize;
            self.position -= count as u64;
            self.file.seek(SeekFrom::Start(self.position))?;
            let mut bytes = vec![0; count];
            self.file.read_exact(&mut bytes)?;
            bytes.append(&mut self.pending);
            self.pending = bytes;
        }
    }
}
fn paths(directory: &PathBuf) -> Vec<PathBuf> {
    let mut files = fs::read_dir(directory)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name()
                .and_then(|s| s.to_str())
                .is_some_and(|n| n.starts_with("spellcheck-") && n.ends_with(".jsonl"))
        })
        .collect::<Vec<_>>();
    files.sort_by(|a, b| b.file_name().cmp(&a.file_name()));
    files
}
fn parse(line: &str) -> Option<Entry> {
    let (index, _) = line.match_indices(" spellcheck_detail ").next()?;
    let data: Value = serde_json::from_str(&line[index + 19..]).ok()?;
    if data["status"] != "success"
        || data["input_text"].as_str()?.is_empty()
        || data["output_text"].as_str()?.is_empty()
    {
        return None;
    }
    Some(Entry {
        timestamp: line.split_whitespace().next()?.into(),
        data,
    })
}
pub struct Reader {
    files: Vec<PathBuf>,
    index: usize,
    current: Option<ReverseFile>,
}
impl Reader {
    pub fn new(directory: &PathBuf) -> Self {
        Self {
            files: paths(directory),
            index: 0,
            current: None,
        }
    }
    pub fn page(&mut self) -> Vec<Entry> {
        let mut page = Vec::new();
        while page.len() < 30 && self.index < self.files.len() {
            if self.current.is_none() {
                self.current = ReverseFile::new(&self.files[self.index]).ok();
                if self.current.is_none() {
                    self.index += 1;
                    continue;
                }
            }
            match self.current.as_mut().unwrap().next() {
                Ok(Some(line)) => {
                    if let Some(entry) = parse(&line) {
                        page.push(entry);
                    }
                }
                _ => {
                    self.current = None;
                    self.index += 1;
                }
            }
        }
        page
    }
}
pub fn stats(directory: &PathBuf) -> Stats {
    let mut stats = Stats::default();
    let mut days = std::collections::BTreeSet::new();
    for file in paths(directory) {
        if let Ok(file) = File::open(file) {
            for line in BufReader::new(file).lines().map_while(Result::ok) {
                if let Some(entry) = parse(&line) {
                    stats.checks += 1;
                    if entry.data["text_changed"] == true {
                        stats.corrections += 1;
                    }
                    if let Some(day) = entry.timestamp.get(..10) {
                        days.insert(day.to_owned());
                    }
                }
            }
        }
    }
    let now = time::OffsetDateTime::now_local()
        .unwrap_or_else(|_| time::OffsetDateTime::now_utc())
        .date();
    let mut day = now;
    if !days.contains(&day.to_string()) {
        day = day.previous_day().unwrap_or(day);
    }
    while days.contains(&day.to_string()) {
        stats.streak += 1;
        day = day.previous_day().unwrap_or(day);
    }
    stats
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn parses_legacy_detail_shape() {
        let line = r#"2026-10-16T12:00:00-04:00 channel=prod app_version=0.12.3 pid=1 spellcheck_detail {"status":"success","input_text":"teh","output_text":"the","text_changed":true}"#;
        assert_eq!(parse(line).unwrap().data["output_text"], "the");
    }
    #[test]
    fn reverse_reader_keeps_chunk_boundaries() {
        let path = std::env::temp_dir().join(format!("usc-reverse-{}.txt", crate::store::new_id()));
        let long = "x".repeat(70000);
        fs::write(&path, format!("first\r\n{long}\nlast\n")).unwrap();
        let mut r = ReverseFile::new(&path).unwrap();
        assert_eq!(r.next().unwrap().unwrap(), "last");
        assert_eq!(r.next().unwrap().unwrap(), long);
        assert_eq!(r.next().unwrap().unwrap(), "first");
        assert!(r.next().unwrap().is_none());
        fs::remove_file(path).unwrap();
    }
}
