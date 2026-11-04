use crate::{
    core::{Protection, Replacements, extract_output, payload},
    rich,
};
use base64::{Engine, engine::general_purpose::STANDARD};
use serde_json::{Value, json};
use std::io::{self, BufRead, Write};
pub fn transform(value: &Value, replacements: &Replacements) -> Result<Value, String> {
    let source = value["input"].as_str().unwrap_or("");
    let protection = Protection::for_target(source, value["active_exe"].as_str().unwrap_or(""))?;
    let raw = value["raw_ai_output"].as_str().unwrap_or("");
    let (model_output, applied, leak, echo) = replacements.process(raw, &protection)?;
    let native = STANDARD
        .decode(value["native_base64"].as_str().unwrap_or(""))
        .map_err(|_| "Invalid native base64")?;
    let mapped = rich::rewrite(
        value["source_html"].as_str().unwrap_or(""),
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
pub fn rich_stdin() -> Result<(), String> {
    let stdin = io::stdin();
    let mut stdout = io::stdout().lock();
    for line in stdin.lock().lines() {
        let value: Value =
            serde_json::from_str(&line.map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
        let native = STANDARD
            .decode(value["native_base64"].as_str().unwrap_or(""))
            .map_err(|e| e.to_string())?;
        let result = rich::rewrite(
            value["source_html"].as_str().unwrap_or(""),
            value["input"].as_str().unwrap_or(""),
            value["output"].as_str().unwrap_or(""),
            &native,
        );
        writeln!(stdout,"{}",json!({"text":result.text,"html":result.html,"mode":result.mode,"applied":result.applied,"reason":result.reason,"paragraph_count":result.paragraph_count,"link_status":result.link_status,"native_base64":result.native.as_ref().map(|n|STANDARD.encode(n)).unwrap_or_default(),"native_status":result.native_docs_status})).map_err(|e|e.to_string())?;
        stdout.flush().map_err(|e| e.to_string())?;
    }
    Ok(())
}
#[allow(dead_code)]
fn exercise_payload_parser(text: &str) {
    let p = payload(text, "gpt-4.1");
    let _ = extract_output(
        &json!({"output":[{"content":[{"type":"output_text","text":p["input"][0]["content"][0]["text"]}]}]}),
    );
}
