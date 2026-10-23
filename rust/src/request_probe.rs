use crate::{benchmark::option, core, http::Client, store::Store};
use serde_json::json;
use std::{fs, io::Write, path::PathBuf, time::Instant};

pub fn run(args: &[String]) -> Result<(), String> {
    if !args.iter().any(|s| s == "--allow-api") {
        return Err(
            "--allow-api required: sends only the fixed synthetic fixture using the saved key"
                .into(),
        );
    }
    if cfg!(debug_assertions) {
        return Err("Benchmarks require a Release build.".into());
    }
    let output = PathBuf::from(option(args, "--output").ok_or("--output required")?);
    let settings = PathBuf::from(option(args, "--settings").ok_or("--settings required")?);
    let runs = option(args, "--runs")
        .unwrap_or("3".into())
        .parse::<usize>()
        .map_err(|_| "Invalid runs")?;
    if !(1..=10).contains(&runs) {
        return Err("--runs must be 1..10".into());
    }
    // Read only: no migration, startup registration, settings/key writes, clipboard or GUI.
    let store = Store::isolated(settings);
    let settings = store.load_settings()?;
    if !settings
        .model
        .eq_ignore_ascii_case(core::normalize_model(&settings.model))
    {
        return Err("Unsupported saved model; select a supported model in Settings first".into());
    }
    let model = core::normalize_model(&settings.model);
    let keys = store.load_keys()?;
    let key = keys.active().ok_or("No active saved key")?;
    let client = Client::diagnostic()?;
    fs::create_dir(&output).map_err(|e| e.to_string())?;
    let prewarm = args.iter().any(|s| s == "--prewarm");
    let manifest = json!({"schema_version":2,"model":model,"fixture":"This is a tset.","runs":runs,"prewarm":prewarm,"binary_sha256":crate::benchmark::hash(&fs::read(std::env::current_exe().map_err(|e|e.to_string())?).map_err(|e|e.to_string())?),"boundary":"Client::request; no clipboard, mapper, GUI or physical hotkey"});
    fs::write(
        output.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest).unwrap(),
    )
    .map_err(|e| e.to_string())?;
    let mut journal =
        fs::File::create_new(output.join("trials.jsonl")).map_err(|e| e.to_string())?;
    if prewarm {
        client.warm();
    }
    let mut failed = false;
    for index in 0..runs {
        let started = Instant::now();
        let result = client.request(
            &core::payload("This is a tset.", &settings.model),
            key,
            || {},
        );
        let row = match result {
            Ok(r) => {
                let text = core::extract_output(&r.body).ok();
                let success = (200..300).contains(&r.status) && text.is_some();
                failed |= !success;
                json!({"index":index,"success":success,"status":r.status,"attempts":r.attempts,"output":text,"quality_match":text.as_deref()==Some("This is a test."),"total_ms":started.elapsed().as_secs_f64()*1000.,"send_ms":r.send_ms,"headers_ms":r.wait_ms,"body_ms":r.download_ms,"final_response":{"attempt":r.attempts,"send_api_ms":r.send_api_ms,"send_completion_wait_ms":r.send_completion_wait_ms,"native":r.native}})
            }
            Err(error) => {
                failed = true;
                json!({"index":index,"success":false,"error":error,"total_ms":started.elapsed().as_secs_f64()*1000.})
            }
        };
        writeln!(journal, "{row}").map_err(|e| e.to_string())?;
        journal.flush().map_err(|e| e.to_string())?;
    }
    if failed {
        Err("Request probe retained failed attempts".into())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn api_opt_in_and_release_are_required_before_settings_access() {
        assert!(
            super::run(&[])
                .unwrap_err()
                .contains("--allow-api required")
        );
        if cfg!(debug_assertions) {
            assert!(
                super::run(&["--allow-api".into()])
                    .unwrap_err()
                    .contains("Release build")
            );
        }
    }
}
