//! T3's composer copies Markdown, not rendered HTML. Protect atoms; verify syntax after correction.
//! Source: pingdotgg/t3code at 15fe2832727893cbdd2214383f9f971474849695,
//! ComposerPromptEditorTiptap.tsx, composer-rich-text-doc.ts and shared composer token helpers.
use regex::Regex;
use std::sync::LazyLock;

macro_rules! re {
    ($pattern:literal) => {{
        static R: LazyLock<Regex> = LazyLock::new(|| Regex::new($pattern).unwrap());
        &*R
    }};
}

pub fn matches(process: &str) -> bool {
    let process = process.strip_suffix(".exe").unwrap_or(process);
    ["T3 Code", "T3 Code (Beta)"]
        .iter()
        .any(|p| process.eq_ignore_ascii_case(p))
}

pub fn ranges(text: &str) -> Vec<(usize, usize)> {
    collect_ranges(text, true)
}

pub fn literal_ranges(text: &str) -> Vec<(usize, usize)> {
    collect_ranges(text, false)
}

fn collect_ranges(text: &str, syntax: bool) -> Vec<(usize, usize)> {
    let mut spans = Vec::new();
    // Fences can contain arbitrary backticks, so only a matching line closes them.
    let mut offset = 0;
    let mut fence: Option<(usize, u8, usize)> = None;
    for line in text.split_inclusive('\n') {
        let trimmed = line.trim_start_matches([' ', '\t']);
        let marker = trimmed.as_bytes().first().copied().unwrap_or(0);
        let length = trimmed.bytes().take_while(|b| *b == marker).count();
        if let Some((start, character, count)) = fence {
            if marker == character && length >= count && trimmed[length..].trim().is_empty() {
                spans.push((start, offset + line.trim_end_matches(['\r', '\n']).len()));
                fence = None;
            }
        } else if matches!(marker, b'`' | b'~') && length >= 3 {
            fence = Some((offset, marker, length));
        }
        offset += line.len();
    }
    if let Some((start, _, _)) = fence {
        spans.push((start, text.len()));
    }
    for pattern in [
        // Citation/context labels and their encoded destinations are a single editor atom.
        re!(r"!?\[[^\]\r\n]*\]\(t3-(?:citation|context)://v1/[^\s)]+\)"),
        // T3 serializes file chips as basename links (also retain image/link syntax).
        re!(r"!?\[(?:\\.|[^\]\\\r\n])*\]\((?:\\.|[^\s)\\])+\)"),
        re!(
            r#"(?:^|\s)(@(?:"(?:\\.|[^"\\])*"|[^\s@"]+)|\p{Sc}[A-Za-z0-9][A-Za-z0-9:_-]*[A-Za-z][A-Za-z0-9:_-]*|\p{Sc}[A-Za-z])"#
        ),
        re!(r"`[^`\r\n]+`"),
        re!(r"(?s)\$\$[^$]+\$\$|\$[^$\r\n]+\$"),
    ] {
        for captures in pattern.captures_iter(text) {
            let m = captures.get(1).or_else(|| captures.get(0)).unwrap();
            spans.push((m.start(), m.end()));
        }
    }
    if syntax {
        for pattern in [
            re!(r"\r\n|\n|\r"),
            re!(
                r"(?m)^[\t ]*(?:(?:>[\t ]*)+|#{1,6}[\t ]+|(?:[-+*]|[0-9]+[.)])[\t ]+(?:\[[ xX]\][\t ]*)?|(?:[-*_][\t ]*){3,}$)"
            ),
            re!(r"\*+|_+|~~|\\[^\r\n]"),
        ] {
            spans.extend(pattern.find_iter(text).map(|m| (m.start(), m.end())));
        }
    }
    spans.sort_by_key(|(a, b)| (*a, std::cmp::Reverse(*b)));
    let mut end = 0;
    spans.retain(|(a, b)| {
        if *a < end {
            false
        } else {
            end = *b;
            true
        }
    });
    spans
}

pub fn structure(text: &str) -> Vec<String> {
    ranges(text)
        .into_iter()
        .map(|(a, b)| text[a..b].to_owned())
        .collect()
}

/// The HTML wrapper transports context records; its body must match copied Markdown exactly.
pub fn context_html(html: &str, source: &str, corrected: &str) -> Option<String> {
    let fragment = crate::rich::fragment(html)?;
    let captures =
        re!(r#"(?s)^<pre data-t3-context-fragment="([^"<>]+)">(.*)</pre>$"#).captures(fragment)?;
    if html_escape::decode_html_entities(&captures[2]) != source {
        return None;
    }
    let body = corrected
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;");
    Some(crate::rich::build_cf_html(&format!(
        "<pre data-t3-context-fragment=\"{}\">{body}</pre>",
        &captures[1]
    )))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::{Protection, Replacements};

    #[test]
    fn correct_prose_without_changing_composer_formats() {
        let cases = [
            "[Assistant quote](t3-citation://v1/env/thread/message?text=teh+quote&start=0&end=9&prefix=&suffix=&comment=teh+comment)\n\nteh prose",
            "![teh screenshot](t3-context://v1/image/image_123) [teh thread](t3-context://v1/thread/thread_456) teh prose",
            "@\"folder/teh file.rs\" $teh-skill [teh.rs](src/teh.rs) teh prose",
            "# teh title\n\n**teh bold** *teh italic* ~~teh strike~~ `teh_code`\n> teh quote\n> second line\n\n---\n- [x] teh task\n  3. teh child\n+ teh bullet",
            "teh prose\n```rust\nlet teh_code = `raw`;\n```\n\nteh prose",
            "teh prose\n~~~~text\n```\nteh code\n~~~~\nteh prose",
            "teh prose\r\n\r\n- teh item\r\n",
            "teh prose and \\*literal\\* and __teh bold__",
        ];
        for source in cases {
            let p = Protection::for_target(source, "T3 Code (Beta)").unwrap();
            let corrected = Replacements::default()
                .process(&p.text.replace("teh", "the"), &p)
                .unwrap()
                .0;
            assert_eq!(structure(source), structure(&corrected), "{source}");
            assert!(corrected.contains("the"), "{source}");
            assert!(!p.text.contains("t3-citation://"));
            assert!(!p.text.contains("t3-context://"));
        }
    }

    #[test]
    fn missing_reordered_or_new_formatting_fails_closed() {
        let p = Protection::for_target("# teh title @one\n- teh item @two", "T3 Code").unwrap();
        assert!(p.restore("the title".into()).is_err());
        assert!(p.restore(format!("{}\n", p.text)).is_err());
        assert!(p.restore(p.text.replace("teh", "**the**")).is_err());
        let reversed = p
            .text
            .replace(&p.entries[0].placeholder, "TEMP")
            .replace(&p.entries[1].placeholder, &p.entries[0].placeholder)
            .replace("TEMP", &p.entries[1].placeholder);
        assert!(p.restore(reversed).is_err());
    }

    #[test]
    fn scope_is_exact_and_other_editors_are_unchanged() {
        assert!(matches("T3 Code (Beta).exe"));
        for process in ["Code", "t3", "not-t3-code", "T3 Code Settings"] {
            assert!(!matches(process));
            let p = Protection::for_target("# teh\n**teh** $skill", process).unwrap();
            assert!(p.entries.is_empty());
        }
    }

    #[test]
    fn context_records_and_corrected_unicode_survive_transport() {
        let source = "[teh thread](t3-context://v1/thread/thread_123)\n\nteh <body> & é";
        let encoded = source
            .replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;");
        let html = crate::rich::build_cf_html(&format!(
            "<pre data-t3-context-fragment=\"%7B%22version%22%3A1%7D\">{encoded}</pre>"
        ));
        let corrected = source.replace("teh <body>", "the <body>");
        let result = context_html(&html, source, &corrected).unwrap();
        assert!(result.contains("%7B%22version%22%3A1%7D"));
        assert!(result.contains("the &lt;body&gt; &amp; é"));
        assert!(context_html(&html, "other selection", &corrected).is_none());
        let mapped = crate::rich::rewrite(&html, source, &corrected, &[]);
        assert_eq!(mapped.mode, "t3_context");
        assert_eq!(mapped.text, corrected);
        assert_eq!(mapped.html, result);
    }

    #[test]
    fn prose_changes_but_code_math_and_chip_contents_are_byte_exact() {
        let source = "# teh title\n\n**teh bold** *teh italic* ~~teh strike~~\n- [x] teh task\n  3. teh child\n> teh quote\n\n`teh_code` $x_{teh}$\n$$\ny_{teh} = 2\n$$\n```rust\nlet teh = 1;\n```\n@\"teh file.rs\" $teh-skill [teh.rs](src/teh.rs)";
        let expected = "# the title\n\n**the bold** *the italic* ~~the strike~~\n- [x] the task\n  3. the child\n> the quote\n\n`teh_code` $x_{teh}$\n$$\ny_{teh} = 2\n$$\n```rust\nlet teh = 1;\n```\n@\"teh file.rs\" $teh-skill [teh.rs](src/teh.rs)";
        let protection = Protection::for_target(source, "T3 Code (Beta)").unwrap();
        let output = Replacements::default()
            .process(&protection.text.replace("teh", "the"), &protection)
            .unwrap()
            .0;
        assert_eq!(output, expected);
        assert!(protection.text.contains("# teh title\n\n**teh bold**"));
    }

    #[test]
    fn citation_is_one_literal_and_model_omission_still_cannot_paste() {
        let quote = "[Assistant quote](t3-citation://v1/11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222/message%3Aprovider%3Acodex%3Anative-item%3Amsg_123?text=teh+quote&start=0&end=9&prefix=&suffix=&comment=keep+teh+comment)";
        let source = format!("{quote}\n\nteh prose");
        let protection = Protection::for_target(&source, "T3 Code (Beta)").unwrap();
        assert_eq!(protection.entries.len(), 1);
        assert_eq!(protection.entries[0].value, quote);
        assert_eq!(protection.text, "__USC_LITERAL_0_1__\n\nteh prose");
        assert!(protection.restore("the prose".into()).is_err());
        assert_eq!(
            protection
                .restore("__USC_LITERAL_0_1__\n\nthe prose".into())
                .unwrap(),
            format!("{quote}\n\nthe prose")
        );
    }

    #[test]
    fn target_request_instructions_and_leak_guard_use_the_same_prompt() {
        let source = "# This is a tset.\n\n@file.rs";
        let p = Protection::for_target(source, "T3 Code").unwrap();
        let request = p.payload("gpt-4.1");
        let prompt = request["input"][0]["content"][0]["text"].as_str().unwrap();
        assert!(prompt.contains("including standalone tokens"));
        let processed = Replacements::default().process(prompt, &p).unwrap();
        assert_eq!(processed.0, source);
        assert!(processed.2);
        let ordinary = Protection::new(source).unwrap().payload("gpt-4.1");
        assert!(
            !ordinary["input"][0]["content"][0]["text"]
                .as_str()
                .unwrap()
                .contains("including standalone tokens")
        );
    }

    #[cfg(feature = "dev")]
    #[test]
    #[ignore = "Explicit USC_T3_ALLOW_API=1 required; synthetic text only, no clipboard or UI"]
    fn live_synthetic_corrections_preserve_t3_formatting() {
        assert_eq!(std::env::var("USC_T3_ALLOW_API").as_deref(), Ok("1"));
        let store = crate::store::Store::standard().unwrap();
        let settings = store.load_settings().unwrap();
        let keys = store.load_keys().unwrap();
        let key = keys.active().expect("Dev key required");
        let client = crate::http::Client::diagnostic().unwrap();
        let cases = [
            "[Assistant quote](t3-citation://v1/11111111-1111-4111-8111-111111111111/22222222-2222-4222-8222-222222222222/message%3Atest?text=Do+not+edit&start=0&end=11&prefix=&suffix=&comment=Do+not+edit)\n\nThis is a tset.",
            "# Test heading\n\n**This is a tset.**\n*This is a tset.*\n~~This is a tset.~~\n\n> This is a tset.\n\n- [x] This is a tset.\n  3. This is a tset.\n\n---\n\n`teh_code`\n```rust\nlet teh = 1;\n```\n\nThis is a tset.",
            "![Screenshot](t3-context://v1/image/image_123) [Thread](t3-context://v1/thread/thread_456)\n\n@\"folder/teh file.rs\" $test-skill [teh.rs](src/teh.rs)\n\nThis is a tset.",
        ];
        for (index, source) in cases.into_iter().enumerate() {
            let p = Protection::for_target(source, "T3 Code (Beta)").unwrap();
            let response = client
                .request(&p.payload(&settings.model), key, || {})
                .unwrap();
            let raw = crate::core::extract_output(&response.body).unwrap();
            let result = Replacements::default().process(&raw, &p);
            println!(
                "{}",
                serde_json::json!({"case":index,"model":settings.model,"input":source,"raw":raw,"result":result})
            );
            let corrected = result.unwrap().0;
            assert!(corrected.contains("This is a test."));
            assert!(!corrected.contains("tset"));
        }
    }
}
