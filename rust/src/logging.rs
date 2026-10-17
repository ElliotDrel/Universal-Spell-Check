use crate::channel;
use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    sync::mpsc::{self, Sender},
    thread,
};
use time::{OffsetDateTime, format_description::well_known::Rfc3339};
enum Message {
    Line(OffsetDateTime, String, Option<serde_json::Value>),
    Flush(Sender<()>),
}
#[derive(Clone)]
pub struct Logger {
    sender: Sender<Message>,
}
impl Logger {
    pub fn new(directory: PathBuf) -> Self {
        Self::observed(directory, None)
    }
    pub fn observed(directory: PathBuf, observer: Option<crate::runtime::Bus>) -> Self {
        let (tx, rx) = mpsc::channel::<Message>();
        thread::Builder::new()
            .name("log-writer".into())
            .spawn(move || {
                while let Ok(message) = rx.recv() {
                    let (now, event, data) = match message {
                        Message::Line(stamp, event, data) => (stamp, event, data),
                        Message::Flush(done) => {
                            let _ = done.send(());
                            continue;
                        }
                    };
                    if let Some(bus) = &observer
                        && let Some(data) = &data
                    {
                        bus.send(crate::runtime::UiEvent::Log(event.clone(), data.clone()));
                    }
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
    pub fn flush(&self) {
        let (tx, rx) = mpsc::channel();
        if self.sender.send(Message::Flush(tx)).is_ok() {
            let _ = rx.recv_timeout(std::time::Duration::from_secs(2));
        }
    }
    pub fn event(&self, event: impl Into<String>) {
        let _ = self.sender.send(Message::Line(
            OffsetDateTime::now_local().unwrap_or_else(|_| OffsetDateTime::now_utc()),
            event.into(),
            None,
        ));
    }
    pub fn data(&self, event: &str, data: serde_json::Value) {
        let _ = self.sender.send(Message::Line(
            OffsetDateTime::now_local().unwrap_or_else(|_| OffsetDateTime::now_utc()),
            event.into(),
            Some(data),
        ));
    }
}
