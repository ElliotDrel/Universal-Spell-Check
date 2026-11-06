//! Deterministic replay of processing without clipboard, UI, keys or network.
//! Both commands read stdin as one JSON document or as JSON lines, and accept the mapper's own
//! field names or a `logs.py --json` row; `--rich-json` also takes a `--save-replay-case` file.
use crate::{
    core::{Protection, Replacements},
    rich,
};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::io::{self, Read, Write};

fn inputs() -> Result<Vec<Value>, String> {
    let mut raw = String::new();
    io::stdin()
        .read_to_string(&mut raw)
        .map_err(|e| e.to_string())?;
    match serde_json::from_str(&raw) {
        Ok(value) => Ok(vec![value]),
        Err(_) => raw
            .lines()
            .filter(|l| !l.trim().is_empty())
            .map(|l| serde_json::from_str(l).map_err(|e| e.to_string()))
            .collect(),
    }
}
/// Returns the first present field among the mapper name and its log-record equivalent.
fn field(value: &Value, names: &[&str]) -> String {
    let value = if value["detail"].is_object() {
        &value["detail"]
    } else {
        value
    };
    names
        .iter()
        .find_map(|n| value[*n].as_str())
        .unwrap_or("")
        .to_owned()
}
fn native(value: &Value) -> Result<Vec<u8>, String> {
    STANDARD
        .decode(field(
            value,
            &["native_base64", "clipboard_chromium_mime_base64"],
        ))
        .map_err(|_| "Invalid native base64".into())
}
fn write(value: Value) -> Result<(), String> {
    let mut stdout = io::stdout().lock();
    writeln!(stdout, "{value}").map_err(|e| e.to_string())
}

/// `--transform-json`: post-processing plus rich mapping from the recorded `raw_ai_output`.
pub fn transform(value: &Value, replacements: &Replacements) -> Result<Value, String> {
    let source = field(value, &["input", "input_text"]);
    let protection = Protection::for_target(&source, &field(value, &["active_exe"]))?;
    let raw = field(value, &["raw_ai_output"]);
    if raw.is_empty() {
        return Err(
            "No raw_ai_output; saved replay cases hold mapper inputs only, use --rich-json".into(),
        );
    }
    let (model_output, applied, leak, echo) = replacements.process(&raw, &protection)?;
    let mapped = rich::rewrite(
        &field(value, &["source_html", "clipboard_html"]),
        &source,
        &model_output,
        &native(value)?,
    );
    Ok(
        json!({"protection":protection,"output":model_output,"rich":mapped,"native_base64":mapped.native.as_ref().map(|n|STANDARD.encode(n)).unwrap_or_default(),"applied":applied,"prompt_leak":leak,"echo_removed":echo}),
    )
}
pub fn transform_stdin(path: &str) -> Result<(), String> {
    let replacements = Replacements::parse(&std::fs::read(path).map_err(|e| e.to_string())?)?;
    for value in inputs()? {
        write(transform(&value, &replacements).unwrap_or_else(|e| json!({"error": e})))?;
    }
    Ok(())
}
/// `--rich-json`: rich mapping only, from the recorded `output_text`, compared with the logged
/// paste when one is present.
pub fn rich_stdin() -> Result<(), String> {
    for value in inputs()? {
        let result = rich::rewrite(
            &field(&value, &["source_html", "clipboard_html"]),
            &field(&value, &["input", "input_text"]),
            &field(&value, &["output", "output_text"]),
            &native(&value)?,
        );
        let detail = if value["detail"].is_object() {
            &value["detail"]
        } else {
            &value
        };
        let matches_recorded = detail["paste_text"].as_str().map(|text| {
            text == result.text && detail["paste_html"].as_str().unwrap_or("") == result.html
        });
        write(
            json!({"text":result.text,"html":result.html,"mode":result.mode,"applied":result.applied,"reason":result.reason,"paragraph_count":result.paragraph_count,"link_status":result.link_status,"native_base64":result.native.as_ref().map(|n|STANDARD.encode(n)).unwrap_or_default(),"native_status":result.native_docs_status,"matches_recorded":matches_recorded}),
        )?;
    }
    Ok(())
}
