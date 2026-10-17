use crate::channel;
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    sync::mpsc::{self, Sender},
    thread,
};
use time::{OffsetDateTime, format_description::well_known::Rfc3339};
#[derive(Clone)]
pub struct Logger {
    sender: Sender<(String, Option<serde_json::Value>)>,
}
impl Logger {
    pub fn new(directory: PathBuf) -> Self {
        let (tx, rx) = mpsc::channel::<(String, Option<serde_json::Value>)>();
        thread::Builder::new()
            .name("log-writer".into())
            .spawn(move || {
                while let Ok((event, data)) = rx.recv() {
                    let now =
                        OffsetDateTime::now_local().unwrap_or_else(|_| OffsetDateTime::now_utc());
                    let date = now.date().to_string();
                    let stamp = now.format(&Rfc3339).unwrap_or_default();
                    let tail = data.map(|d| format!(" {d}")).unwrap_or_default();
                    let line = format!(
                        "{stamp} channel={} app_version={} pid={} {event}{tail}\r\n",
                        channel::CHANNEL,
                        channel::VERSION,
                        std::process::id()
                    );
                    let _ = fs::create_dir_all(&directory);
                    let path = directory.join(format!("spellcheck-{date}.jsonl"));
                    for attempt in 0..=5 {
                        match OpenOptions::new()
                            .create(true)
                            .append(true)
                            .open(&path)
                            .and_then(|mut f| f.write_all(line.as_bytes()))
                        {
                            Ok(_) => break,
                            Err(_) => {
                                thread::sleep(std::time::Duration::from_millis(10 * (attempt + 1)))
                            }
                        }
                    }
                }
            })
            .expect("log writer");
        Self { sender: tx }
    }
    pub fn event(&self, event: impl Into<String>) {
        let _ = self.sender.send((event.into(), None));
    }
    pub fn data(&self, event: &str, data: serde_json::Value) {
        let _ = self.sender.send((event.into(), Some(data)));
    }
}
