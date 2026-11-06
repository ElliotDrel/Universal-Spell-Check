use crate::{
    core::{Protection, Replacements},
    rich,
};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::io::{self, BufRead, Read, Write};
/// Runs post-processing and rich mapping on a recorded model answer. Accepts `input` or a log
/// row's `input_text`, plus `raw_ai_output`, `source_html` and optional `active_exe`.
pub fn transform(value: &Value, replacements: &Replacements) -> Result<Value, String> {
    let value = if value["detail"].is_object() {
        &value["detail"]
    } else {
        value
    };
    let source = value["input"]
        .as_str()
        .or(value["input_text"].as_str())
        .unwrap_or("");
    let protection = Protection::for_target(source, value["active_exe"].as_str().unwrap_or(""))?;
    let raw = value["raw_ai_output"].as_str().unwrap_or("");
    let (model_output, applied, leak, echo) = replacements.process(raw, &protection)?;
    let native = STANDARD
        .decode(value["native_base64"].as_str().unwrap_or(""))
        .map_err(|_| "Invalid native base64")?;
    let mapped = rich::rewrite(
        value["source_html"]
            .as_str()
            .or(value["clipboard_html"].as_str())
            .unwrap_or(""),
        source,
        &model_output,
        &native,
    );
    Ok(
        json!({"protection":protection,"output":model_output,"rich":mapped,"native_base64":mapped.native.as_ref().map(|n|STANDARD.encode(n)).unwrap_or_default(),"applied":applied,"prompt_leak":leak,"echo_removed":echo}),
    )
}
pub fn transform_stdin(path: &str) -> Result<(), String> {
    let replacements = Replacements::parse(&std::fs::read(path).map_err(|e| e.to_string())?)?;
    let stdin = io::stdin();
    let mut stdout = io::stdout().lock();
    for line in stdin.lock().lines() {
        let line = line.map_err(|e| e.to_string())?;
        let result = serde_json::from_str::<Value>(&line)
            .map_err(|e| e.to_string())
            .and_then(|v| transform(&v, &replacements));
        let output = match result {
            Ok(v) => v,
            Err(e) => json!({"error":e}),
        };
        writeln!(stdout, "{output}").map_err(|e| e.to_string())?;
        stdout.flush().map_err(|e| e.to_string())?;
    }
    Ok(())
}
/// Rebuilds rich formatting for each input: one JSON object, or JSON lines. Accepts the mapper's
/// own fields (`input`, `source_html`, `output`, `native_base64`), a `logs.py --json` row, or a
/// `--save-replay-case` file, and compares against the recorded paste when present.
pub fn rich_stdin() -> Result<(), String> {
    let mut raw = String::new();
    io::stdin()
        .read_to_string(&mut raw)
        .map_err(|e| e.to_string())?;
    let values: Vec<Value> = match serde_json::from_str(&raw) {
        Ok(value) => vec![value],
        Err(_) => raw
            .lines()
            .filter(|l| !l.trim().is_empty())
            .map(|l| serde_json::from_str(l).map_err(|e| e.to_string()))
            .collect::<Result<_, _>>()?,
    };
    let mut stdout = io::stdout().lock();
    for value in values {
        let v = if value["detail"].is_object() {
            &value["detail"]
        } else {
            &value
        };
        let field = |names: &[&str]| {
            names
                .iter()
                .find_map(|n| v[*n].as_str())
                .unwrap_or("")
                .to_owned()
        };
        let native = STANDARD
            .decode(field(&["native_base64", "clipboard_chromium_mime_base64"]))
            .map_err(|e| e.to_string())?;
        let result = rich::rewrite(
            &field(&["source_html", "clipboard_html"]),
            &field(&["input", "input_text"]),
            &field(&["output", "output_text"]),
            &native,
        );
        let matches_recorded = v["paste_text"].as_str().map(|text| {
            text == result.text && v["paste_html"].as_str().unwrap_or("") == result.html
        });
        writeln!(stdout,"{}",json!({"text":result.text,"html":result.html,"mode":result.mode,"applied":result.applied,"reason":result.reason,"paragraph_count":result.paragraph_count,"link_status":result.link_status,"native_base64":result.native.as_ref().map(|n|STANDARD.encode(n)).unwrap_or_default(),"native_status":result.native_docs_status,"matches_recorded":matches_recorded})).map_err(|e|e.to_string())?;
    }
    Ok(())
}
