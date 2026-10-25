//! Offline-only selector: ordinary app, desktop and request modes cannot enable an arm.
use std::cell::Cell;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Arm {
    Baseline,
    Replacements,
    Restore,
    Rich,
}
impl Arm {
    pub fn name(self) -> &'static str {
        match self {
            Self::Baseline => "baseline",
            Self::Replacements => "replacements-ac",
            Self::Restore => "restore-splice",
            Self::Rich => "rich-index",
        }
    }
}
thread_local! { static ARM: Cell<Arm> = const { Cell::new(Arm::Baseline) }; }
pub fn selected() -> Arm {
    ARM.get()
}
pub fn configure(args: &[String]) -> Result<(), String> {
    if !args.iter().any(|arg| arg == "--processing-arm") {
        return Ok(());
    }
    if !matches!(
        args.get(1).map(String::as_str),
        Some("--transform-json" | "--rich-json" | "--bench-compute")
    ) {
        return Err(
            "--processing-arm is restricted to offline transform/rich/compute modes".into(),
        );
    }
    let value = crate::benchmark::option(args, "--processing-arm")
        .ok_or("--processing-arm requires a value")?;
    let arm = match value.as_str() {
        "baseline" => Arm::Baseline,
        "replacements-ac" => Arm::Replacements,
        "restore-splice" => Arm::Restore,
        "rich-index" => Arm::Rich,
        _ => return Err(format!("Unknown processing arm: {value}")),
    };
    ARM.set(arm);
    Ok(())
}
#[cfg(test)]
pub fn with_arm<T>(arm: Arm, work: impl FnOnce() -> T) -> T {
    struct Reset(Arm);
    impl Drop for Reset {
        fn drop(&mut self) {
            ARM.set(self.0);
        }
    }
    let _reset = Reset(ARM.replace(arm));
    work()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::{Protection, Replacements};
    use serde_json::{Value, json};
    #[test]
    fn frozen_exact_processing_all_arms() {
        let cases: Vec<crate::benchmark::Case> =
            serde_json::from_str(include_str!("../../bench/baseline-cases.json")).unwrap();
        let fixtures: Vec<crate::benchmark::Fixture> =
            serde_json::from_str(include_str!("../tests/fixtures/baseline-api.json")).unwrap();
        for case in cases {
            let fixture = fixtures.iter().find(|f| f.id == case.id).unwrap();
            let input = json!({"input":case.text,"raw_ai_output":fixture.raw_ai_output,"source_html":case.html});
            let run = |arm| {
                with_arm(arm, || {
                    let replacements =
                        Replacements::parse(include_bytes!("../../replacements.json")).unwrap();
                    crate::cli::transform(&input, &replacements).unwrap()
                })
            };
            let baseline = run(Arm::Baseline);
            for arm in [Arm::Replacements, Arm::Restore, Arm::Rich] {
                assert_eq!(run(arm), baseline, "{} {arm:?}", case.id);
            }
        }
    }
    #[test]
    fn rich_index_exact_html_native_and_refusals() {
        use base64::{Engine, engine::general_purpose::STANDARD};
        for (i, line) in include_str!("../tests/fixtures/csharp-formatting.jsonl")
            .lines()
            .enumerate()
        {
            let case: Value = serde_json::from_str(line).unwrap();
            let native = STANDARD
                .decode(case["native_base64"].as_str().unwrap())
                .unwrap();
            let run = |arm| {
                with_arm(arm, || {
                    let result = crate::rich::rewrite(
                        case["source_html"].as_str().unwrap(),
                        case["input"].as_str().unwrap(),
                        case["output"].as_str().unwrap(),
                        &native,
                    );
                    (serde_json::to_value(&result).unwrap(), result.native)
                })
            };
            assert_eq!(run(Arm::Rich), run(Arm::Baseline), "rich case {i}");
        }
    }
    #[test]
    fn replacement_overlaps_duplicates_unicode_labels_and_non_cascading() {
        let rules = r#"{"first":["ab","dup","é"],"second":["dup","abc","🦀"],"third":["first"],"fourth":["éx"]}"#;
        for input in [
            "abcd dup dup é éx 🦀 first",
            "",
            "unmatched",
            "ababcab",
            "🦀éxdup",
        ] {
            let run = |arm| {
                with_arm(arm, || {
                    Replacements::parse(rules.as_bytes()).unwrap().apply(input)
                })
            };
            assert_eq!(run(Arm::Replacements), run(Arm::Baseline), "{input}");
        }
        let stress_rules = br#"{"GitHub":["github","Github"],"OpenAI":["open ai"]}"#;
        let rules = Replacements::parse(stress_rules).unwrap();
        for size in [20, 20_000, 100_000] {
            for unit in ["no-match 🦀 ", "github open ai Github "] {
                let input = unit.repeat(size / unit.len() + 1);
                let baseline = rules.apply(&input);
                let alternative = with_arm(Arm::Replacements, || {
                    Replacements::parse(stress_rules).unwrap().apply(&input)
                });
                assert_eq!(alternative, baseline, "{size} {unit}");
            }
        }
    }
    #[test]
    fn restore_reordered_missing_duplicate_and_collision_namespace() {
        for input in [
            "Keep __USC_LITERAL_0_1__ and https://example.com and C:\\Data\\file.txt",
            "https://example.com/🦀 https://example.org",
            "no literals",
        ] {
            let p = Protection::new(input).unwrap();
            let mut candidates = vec![p.text.clone(), "missing".into()];
            if let Some(first) = p.entries.first() {
                candidates.push(format!("{} {}", p.text, first.placeholder));
                candidates.push(
                    p.entries
                        .iter()
                        .rev()
                        .map(|e| e.placeholder.clone())
                        .collect::<Vec<_>>()
                        .join(" "),
                );
            }
            for candidate in candidates {
                let baseline = p.restore(candidate.clone());
                let alternative = with_arm(Arm::Restore, || p.restore(candidate));
                assert_eq!(alternative, baseline);
            }
        }
    }
    #[test]
    fn rich_index_large_mixed_spans_boundaries_and_nbsp() {
        for size in [1, 50, 200] {
            let mut fragment = String::from("<p data-pm-slice=\"0 0 []\">");
            let mut source = String::new();
            for i in 0..size {
                let word = format!("word{i:04} ");
                fragment.push_str(&format!("<strong>{word}</strong>"));
                source.push_str(&word);
            }
            fragment.push_str("<a href=\"https://example.com\">tail&nbsp;end</a></p>");
            source.push_str("tail end");
            let html = crate::rich::build_cf_html(&fragment);
            for target in [
                source.clone(),
                source.replacen("word0000", "newword0000", 1),
                source.replace("tail end", "tail finish"),
                format!("{source}!"),
                source.replace("word0000 ", ""),
            ] {
                let run = |arm| {
                    with_arm(arm, || {
                        serde_json::to_value(crate::rich::rewrite(&html, &source, &target, &[]))
                            .unwrap()
                    })
                };
                assert_eq!(run(Arm::Rich), run(Arm::Baseline), "{size} nodes");
            }
        }
    }
    #[test]
    fn selector_is_offline_only_and_rejects_unknown_arm() {
        for mode in ["--bench-desktop", "--bench-request", "--settings"] {
            let args = ["app", mode, "--processing-arm", "rich-index"].map(str::to_owned);
            assert!(configure(&args).is_err());
        }
        assert!(
            configure(&["app", "--rich-json", "--processing-arm", "unknown"].map(str::to_owned))
                .is_err()
        );
    }
}
