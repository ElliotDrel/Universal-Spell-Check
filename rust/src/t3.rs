//! Protect the complete assistant-quote serialization copied by T3's desktop composer.
use regex::Regex;
use std::sync::LazyLock;

pub fn matches(process: &str) -> bool {
    let process = process.strip_suffix(".exe").unwrap_or(process);
    ["T3 Code", "T3 Code (Beta)"]
        .iter()
        .any(|p| process.eq_ignore_ascii_case(p))
}

pub fn citation_ranges(text: &str) -> Vec<(usize, usize)> {
    // T3's assistantCitations.ts emits this label/prefix and percent-encodes delimiters.
    static CITATION: LazyLock<Regex> =
        LazyLock::new(|| Regex::new(r"\[Assistant quote\]\(t3-citation://v1/[^\s)]+\)").unwrap());
    CITATION
        .find_iter(text)
        .map(|m| (m.start(), m.end()))
        .collect()
}

#[cfg(test)]
mod tests {
    use crate::core::{Protection, Replacements};

    const QUOTE: &str = "[Assistant quote](t3-citation://v1/11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222/message%3Atest?text=Do+not+edit&start=0&end=11&prefix=&suffix=&comment=Keep+unchanged)";

    #[test]
    fn complete_citation_survives_correction_and_missing_or_duplicate_tokens_refuse() {
        let source = format!("{QUOTE}\n\nThis is a tset.");
        let p = Protection::for_target(&source, "T3 Code (Beta)").unwrap();
        assert_eq!(p.entries.len(), 1);
        assert_eq!(p.entries[0].value, QUOTE);
        assert_eq!(p.text, "__USC_LITERAL_0_1__\n\nThis is a tset.");
        let corrected = Replacements::default()
            .process(&p.text.replace("tset", "test"), &p)
            .unwrap()
            .0;
        assert_eq!(corrected, format!("{QUOTE}\n\nThis is a test."));
        assert!(p.restore("This is a test.".into()).is_err());
        assert!(
            p.restore(format!("{} __USC_LITERAL_0_1__", p.text))
                .is_err()
        );
    }

    #[test]
    fn ordinary_t3_text_uses_existing_protection_and_prompt() {
        for source in [
            "# Tset\n\n**Tset**\n1. Tset\n   1. Tset",
            "`teh_code` $x_{teh}$ $skill",
            "[Thread](t3-context://v1/thread/thread_123)",
        ] {
            let ordinary = Protection::new(source).unwrap();
            let target = Protection::for_target(source, "T3 Code (Beta)").unwrap();
            assert_eq!(
                serde_json::to_value(&target).unwrap(),
                serde_json::to_value(&ordinary).unwrap()
            );
            assert_eq!(target.payload("gpt-4.1"), ordinary.payload("gpt-4.1"));
        }
        assert!(!super::matches("Code"));
        assert!(super::matches("T3 Code (Beta).exe"));
    }

    #[test]
    fn citation_instructions_and_leak_guard_agree() {
        let p = Protection::for_target(QUOTE, "T3 Code").unwrap();
        let request = p.payload("gpt-4.1");
        let prompt = request["input"][0]["content"][0]["text"].as_str().unwrap();
        assert!(prompt.contains("including standalone tokens"));
        let processed = Replacements::default().process(prompt, &p).unwrap();
        assert_eq!(processed.0, QUOTE);
        assert!(processed.2);
    }

    #[cfg(feature = "dev")]
    #[test]
    #[ignore = "Explicit USC_T3_ALLOW_API=1 required; synthetic citation only, no clipboard or UI"]
    fn live_synthetic_citation() {
        assert_eq!(std::env::var("USC_T3_ALLOW_API").as_deref(), Ok("1"));
        let store = crate::store::Store::standard().unwrap();
        let settings = store.load_settings().unwrap();
        let keys = store.load_keys().unwrap();
        let key = keys.active().expect("Dev key required");
        let client = crate::http::Client::diagnostic().unwrap();
        let p = Protection::for_target(&format!("{QUOTE}\n\nThis is a tset."), "T3 Code (Beta)")
            .unwrap();
        let response = client
            .request(&p.payload(&settings.model), key, || {})
            .unwrap();
        let raw = crate::core::extract_output(&response.body).unwrap();
        let corrected = Replacements::default().process(&raw, &p).unwrap().0;
        assert_eq!(corrected, format!("{QUOTE}\n\nThis is a test."));
    }
}
