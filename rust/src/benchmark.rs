use crate::{
    core::{PROMPT, Protection, Replacements, extract_output, payload},
    rich,
};
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, HashMap},
    fs,
    io::Write,
    path::PathBuf,
    time::Instant,
};
#[derive(Clone, Deserialize)]
pub struct Case {
    pub id: String,
    pub text: String,
    pub expected: String,
    #[serde(default)]
    pub html: String,
}
#[derive(Clone, Deserialize, serde::Serialize)]
pub struct Fixture {
    pub id: String,
    pub input: String,
    pub raw_ai_output: String,
    pub output: String,
    pub paste_text: String,
    pub paste_html: String,
}
pub fn option(args: &[String], name: &str) -> Option<String> {
    args.windows(2).find(|w| w[0] == name).map(|w| w[1].clone())
}
fn read(path: &str) -> Result<Vec<u8>, String> {
    fs::read(path).map_err(|e| e.to_string())
}
pub(crate) fn hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn elapsed(start: Instant) -> f64 {
    start.elapsed().as_secs_f64() * 1000.
}
pub fn compute(args: &[String]) -> Result<(), String> {
    if cfg!(debug_assertions) {
        return Err("Benchmarks require a Release build.".into());
    }
    let cases_path = option(args, "--cases").ok_or("--cases required")?;
    let fixture_path = option(args, "--fixtures").ok_or("--fixtures required")?;
    let replacements_path = option(args, "--replacements").ok_or("--replacements required")?;
    let output = PathBuf::from(option(args, "--output").ok_or("--output required")?);
    let runs = option(args, "--runs")
        .unwrap_or("20".into())
        .parse::<usize>()
        .map_err(|_| "Invalid runs")?;
    let warmup = option(args, "--warmup")
        .unwrap_or("2".into())
        .parse::<usize>()
        .map_err(|_| "Invalid warmup")?;
    if runs == 0 || runs > 10000 || warmup > 1000 {
        return Err("Invalid run count".into());
    }
    if output.exists()
        && fs::read_dir(&output)
            .map_err(|e| e.to_string())?
            .next()
            .is_some()
    {
        return Err(
            "Output directory must be empty; previous evidence is never overwritten.".into(),
        );
    }
    let case_bytes = read(&cases_path)?;
    let fixture_bytes = read(&fixture_path)?;
    let replacements_bytes = read(&replacements_path)?;
    let cases: Vec<Case> = serde_json::from_slice(&case_bytes).map_err(|e| e.to_string())?;
    let fixtures: Vec<Fixture> =
        serde_json::from_slice(&fixture_bytes).map_err(|e| e.to_string())?;
    let replacements = Replacements::parse(&replacements_bytes)?;
    if cases.is_empty()
        || cases
            .iter()
            .any(|c| c.id.is_empty() || c.text.trim().is_empty())
        || cases
            .iter()
            .map(|c| &c.id)
            .collect::<std::collections::HashSet<_>>()
            .len()
            != cases.len()
    {
        return Err("Cases must have unique nonempty IDs and text".into());
    }
    let mut responses = HashMap::new();
    for fixture in &fixtures {
        let protected = Protection::new(&fixture.input)?;
        let request =
            format!("instructions: {PROMPT}\ntext input: {}", protected.text).replace("\r\n", "\n");
        let response=serde_json::to_vec(&json!({"output":[{"type":"message","content":[{"type":"output_text","text":fixture.raw_ai_output}]}],"usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0}})).map_err(|e|e.to_string())?;
        if responses.insert(request, response).is_some() {
            return Err("Duplicate fixture input".into());
        }
    }
    for case in &cases {
        if fixtures
            .iter()
            .filter(|f| f.id == case.id && f.input == case.text)
            .count()
            != 1
        {
            return Err(format!("Missing or ambiguous fixture {}", case.id));
        }
    }
    fs::create_dir_all(&output).map_err(|e| e.to_string())?;
    let git = |args: &[&str]| {
        std::process::Command::new("git")
            .args(args)
            .output()
            .ok()
            .map(|o| String::from_utf8_lossy(&o.stdout).trim().to_owned())
            .unwrap_or_default()
    };
    let manifest = json!({"schema_version":2,"created_utc":time::OffsetDateTime::now_utc().to_string(),"implementation":"rust","options":{"mode":"compute","runs":runs,"warmup":warmup,"cases":cases_path,"fixtures":fixture_path,"developer_logging":false},"worktree_commit":git(&["rev-parse","HEAD"]),"dirty":git(&["status","--porcelain"]),"model":"gpt-4.1","prompt":PROMPT,"developer_logging":false,"saved_developer_logging":false,"machine_name":std::env::var("COMPUTERNAME").unwrap_or_default(),"case_ids":cases.iter().map(|c|&c.id).collect::<Vec<_>>(),"cases_sha256":hash(&case_bytes),"fixture_sha256":hash(&fixture_bytes),"replacements_sha256":hash(&replacements_bytes),"product_binary_sha256":hash(&fs::read(std::env::current_exe().map_err(|e|e.to_string())?).map_err(|e|e.to_string())?),"configuration":"Release","runtime":"Rust native Win32","notes":"Same compute boundaries: protection, real payload serialization/parser, exact frozen replay lookup, response parser, postprocessing and rich mapping. No clipboard/UI/network/key storage."});
    fs::write(
        output.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .map_err(|e| e.to_string())?;
    let mut raw = fs::File::create(output.join("trials.jsonl")).map_err(|e| e.to_string())?;
    let mut trials = Vec::new();
    for case in &cases {
        let expected = fixtures.iter().find(|f| f.id == case.id).unwrap();
        for index in -(warmup as isize)..runs as isize {
            let total = Instant::now();
            let result = (|| {
                let start = Instant::now();
                let protection = Protection::new(&case.text)?;
                let protect_ms = elapsed(start);
                let start = Instant::now();
                let payload = serde_json::to_vec(&payload(&protection.text, "gpt-4.1"))
                    .map_err(|e| e.to_string())?;
                let request: Value = serde_json::from_slice(&payload).map_err(|e| e.to_string())?;
                let text = request["input"][0]["content"][0]["text"]
                    .as_str()
                    .ok_or("Bad payload")?
                    .replace("\r\n", "\n");
                let response: Value = serde_json::from_slice(
                    responses
                        .get(&text)
                        .ok_or("Replay request not in frozen fixtures")?,
                )
                .map_err(|e| e.to_string())?;
                let ai = extract_output(&response)?;
                let service_ms = elapsed(start);
                let start = Instant::now();
                let (output, _, _, _) = replacements.process(&ai, &protection)?;
                let post_ms = elapsed(start);
                let start = Instant::now();
                let mapped = rich::rewrite(&case.html, &case.text, &output, &[]);
                let rich_ms = elapsed(start);
                let total_ms = elapsed(total);
                let exact = output == expected.output
                    && mapped.text == expected.paste_text
                    && mapped.html == expected.paste_html;
                Ok::<_, String>((
                    output,
                    exact,
                    json!({"total_ms":total_ms,"protect_ms":protect_ms,"replay_service_ms":service_ms,"post_process_ms":post_ms,"rich_map_ms":rich_ms,"request_ms":null,"capture_ms":null,"paste_ms":null}),
                ))
            })();
            let trial = match result {
                Ok((text, exact, timings)) => {
                    json!({"case_id":case.id,"index":index,"warmup":index<0,"success":exact,"quality_match":text.replace("\r\n","\n")==case.expected.replace("\r\n","\n"),"timings":timings,"output":text,"error":if exact{Value::Null}else{json!("Recorded output or rich mapping differs from frozen reference.")}})
                }
                Err(e) => {
                    json!({"case_id":case.id,"index":index,"warmup":index<0,"success":false,"quality_match":false,"timings":{},"output":"","error":e})
                }
            };
            writeln!(raw, "{trial}").map_err(|e| e.to_string())?;
            raw.flush().map_err(|e| e.to_string())?;
            trials.push(trial);
        }
    }
    let measured: Vec<_> = trials.iter().filter(|t| t["warmup"] == false).collect();
    let warm: Vec<_> = trials.iter().filter(|t| t["warmup"] == true).collect();
    let mut groups = BTreeMap::new();
    for case in &cases {
        groups.insert(
            &case.id,
            summarize(
                &measured
                    .iter()
                    .copied()
                    .filter(|t| t["case_id"] == case.id)
                    .collect::<Vec<_>>(),
            ),
        );
    }
    let summary =
        json!({"aggregate":summarize(&measured),"warmup":summarize(&warm),"cases":groups});
    fs::write(
        output.join("summary.json"),
        serde_json::to_vec_pretty(&summary).unwrap(),
    )
    .map_err(|e| e.to_string())?;
    println!("{}", summary["aggregate"]);
    if trials.iter().any(|t| t["success"] != true) {
        return Err("Benchmark retained failed trials; inspect summary and raw results.".into());
    }
    Ok(())
}
pub(crate) fn summarize(trials: &[&Value]) -> Value {
    let ok: Vec<_> = trials
        .iter()
        .copied()
        .filter(|t| t["success"] == true)
        .collect();
    let mut timings = serde_json::Map::new();
    let mut keys = std::collections::BTreeSet::new();
    for trial in trials {
        if let Some(values) = trial["timings"].as_object() {
            keys.extend(values.keys().map(String::as_str));
        }
    }
    for key in keys {
        let values = ok
            .iter()
            .filter_map(|t| t["timings"][key].as_f64())
            .collect();
        timings.insert(key.into(), statistics(values));
    }
    json!({"attempted":trials.len(),"succeeded":ok.len(),"failed":trials.len()-ok.len(),"quality_matches":trials.iter().filter(|t|t["quality_match"]==true).count(),"timings":timings})
}
fn statistics(mut values: Vec<f64>) -> Value {
    if values.is_empty() {
        return json!({"count":0,"mean":null,"median":null,"p95":null,"min":null,"max":null});
    }
    values.sort_by(f64::total_cmp);
    let percentile = |p: f64| {
        let position = p * (values.len() - 1) as f64;
        let lo = position.floor() as usize;
        let hi = position.ceil() as usize;
        values[lo] + (values[hi] - values[lo]) * (position - lo as f64)
    };
    json!({"count":values.len(),"mean":values.iter().sum::<f64>()/values.len()as f64,"median":percentile(0.5),"p95":percentile(0.95),"min":values[0],"max":values[values.len()-1]})
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn percentile_keeps_zero_and_interpolates() {
        assert_eq!(statistics(vec![0., 10.])["median"], 5.);
        assert_eq!(statistics(vec![0., 10.])["p95"], 9.5);
        assert!(statistics(Vec::new())["median"].is_null());
    }
}
