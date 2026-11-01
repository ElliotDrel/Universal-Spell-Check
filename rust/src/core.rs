use fancy_regex::Regex;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::sync::LazyLock;

pub const PROMPT: &str = "Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.";
const T3_INSTRUCTIONS: &str = "Every __USC_LITERAL_N_M__ token is immutable content, including standalone tokens. Copy every token exactly once in its original order.";
static LITERALS: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(include_str!("literal_regex.txt")).expect("literal regex"));

#[derive(Clone, Debug, Serialize)]
pub struct Literal {
    pub placeholder: String,
    pub value: String,
    pub kind: String,
}
#[derive(Clone, Debug, Serialize)]
pub struct Protection {
    pub text: String,
    pub entries: Vec<Literal>,
    #[serde(skip)]
    t3_citations: bool,
}
impl Protection {
    fn prompt(&self) -> String {
        if self.t3_citations {
            format!("{PROMPT} {T3_INSTRUCTIONS}")
        } else {
            PROMPT.into()
        }
    }
    pub fn payload(&self, model: &str) -> Value {
        let mut result = payload(&self.text, model);
        if self.t3_citations {
            result["input"][0]["content"][0]["text"] = json!(format!(
                "instructions: {}\ntext input: {}",
                self.prompt(),
                self.text
            ));
        }
        result
    }
    pub fn new(text: &str) -> Result<Self, String> {
        Self::for_target(text, "")
    }
    pub fn for_target(text: &str, process: &str) -> Result<Self, String> {
        let mut namespace = 0;
        while text.contains(&format!("__USC_LITERAL_{namespace}_")) {
            namespace += 1;
        }
        let mut entries = Vec::new();
        let mut output = String::with_capacity(text.len());
        let mut cursor = 0;
        let mut spans = Vec::new();
        for found in LITERALS.captures_iter(text) {
            let cap = found.map_err(|_| "Literal protection exceeded its work limit")?;
            let m = cap.get(0).unwrap();
            let kind = ["url", "api_key", "uuid", "file_path", "opaque_id"]
                .into_iter()
                .find(|n| cap.name(n).is_some())
                .unwrap();
            spans.push((m.start(), m.end(), kind));
        }
        spans.extend(
            crate::rich_links::literal_ranges(text)
                .into_iter()
                .map(|(start, end)| (start, end, "url")),
        );
        let citation_ranges = if crate::t3::matches(process) {
            crate::t3::citation_ranges(text)
        } else {
            Vec::new()
        };
        let t3_citations = !citation_ranges.is_empty();
        spans.extend(citation_ranges.into_iter().map(|(a, b)| (a, b, "url")));
        spans.sort_by_key(|(start, end, _)| (*start, std::cmp::Reverse(*end)));
        for (start, end, kind) in spans {
            if start < cursor {
                continue;
            }
            output.push_str(&text[cursor..start]);
            let placeholder = format!("__USC_LITERAL_{namespace}_{}__", entries.len() + 1);
            output.push_str(&placeholder);
            entries.push(Literal {
                placeholder,
                value: text[start..end].to_owned(),
                kind: kind.to_owned(),
            });
            cursor = end;
        }
        output.push_str(&text[cursor..]);
        Ok(Self {
            text: output,
            entries,
            t3_citations,
        })
    }
    pub fn restore(&self, mut text: String) -> Result<String, String> {
        // Validate every token before replacing any: literal values may contain token-looking text.
        if self
            .entries
            .iter()
            .any(|e| text.matches(&e.placeholder).count() != 1)
        {
            return Err(
                "The model changed a protected literal; selection was left unchanged.".into(),
            );
        }
        if self.t3_citations {
            let mut cursor = 0;
            for entry in &self.entries {
                let position = text[cursor..].find(&entry.placeholder).ok_or(
                    "The model reordered protected citation content; selection was left unchanged.",
                )?;
                cursor += position + entry.placeholder.len();
            }
        }
        for entry in &self.entries {
            text = text.replace(&entry.placeholder, &entry.value);
        }
        Ok(text)
    }
}

#[derive(Clone, Default)]
pub struct Replacements {
    pairs: Vec<(String, String)>,
}
impl Replacements {
    pub fn parse(bytes: &[u8]) -> Result<Self, String> {
        let text = std::str::from_utf8(bytes)
            .map_err(|e| e.to_string())?
            .trim_start_matches('\u{feff}');
        let value: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
        let mut pairs = Vec::new();
        for (canonical, variants) in value.as_object().ok_or("Replacements must be an object")? {
            for variant in variants
                .as_array()
                .ok_or("Replacement variants must be an array")?
            {
                let v = variant.as_str().ok_or("Replacement variant must be text")?;
                if !v.is_empty() && !canonical.contains(v) {
                    pairs.push((v.to_owned(), canonical.clone()));
                }
            }
        }
        pairs.sort_by_key(|(v, _)| std::cmp::Reverse(v.encode_utf16().count()));
        Ok(Self { pairs })
    }
    pub fn apply(&self, text: &str) -> (String, Vec<String>) {
        let mut output = String::with_capacity(text.len());
        let mut applied = Vec::new();
        let mut pos = 0;
        while pos < text.len() {
            let found = self
                .pairs
                .iter()
                .filter_map(|(variant, canonical)| {
                    text[pos..]
                        .find(variant)
                        .map(|i| (i + pos, variant, canonical))
                })
                .min_by(|a, b| a.0.cmp(&b.0).then_with(|| b.1.len().cmp(&a.1.len())));
            let Some((index, variant, canonical)) = found else {
                output.push_str(&text[pos..]);
                break;
            };
            output.push_str(&text[pos..index]);
            output.push_str(canonical);
            let label = format!("{variant} -> {canonical}");
            if !applied.contains(&label) {
                applied.push(label);
            }
            pos = index + variant.len();
        }
        (output, applied)
    }
    pub fn process(
        &self,
        output: &str,
        protection: &Protection,
    ) -> Result<(String, Vec<String>, bool, bool), String> {
        let deecho = strip_echo(output, &protection.text);
        let echo_removed = deecho != output;
        let (mut text, applied) = self.apply(deecho);
        let leak = format!("instructions: {}", protection.prompt());
        let triggered = text.contains(&leak);
        if triggered {
            text = text.replace(&leak, "").trim_start().to_owned();
            if let Some(rest) = text.strip_prefix("text input:") {
                text = rest.trim_start().to_owned();
            }
        }
        Ok((protection.restore(text)?, applied, triggered, echo_removed))
    }
}
fn strip_echo<'a>(output: &'a str, input: &str) -> &'a str {
    if input.is_empty() {
        return output;
    }
    if let Some(rest) = output.strip_prefix(input)
        && let Some(corrected) = rest
            .strip_prefix("\n\n---\n\n")
            .or_else(|| rest.strip_prefix("\r\n\r\n---\r\n\r\n"))
    {
        let corrected = if corrected
            .get(..15)
            .is_some_and(|s| s.eq_ignore_ascii_case("Corrected text:"))
        {
            corrected[15..].trim_start_matches(['\r', '\n'])
        } else {
            corrected
        };
        if !corrected.is_empty() {
            return corrected;
        }
    }
    output
}
pub fn normalize_model(model: &str) -> &'static str {
    if model.eq_ignore_ascii_case("gpt-5.4-mini") {
        "gpt-5.4-mini"
    } else {
        "gpt-4.1"
    }
}
pub fn payload(input: &str, model: &str) -> Value {
    let model = normalize_model(model);
    let mut value = json!({"model":model,"input":[{"role":"user","content":[{"type":"input_text","text":format!("instructions: {PROMPT}\ntext input: {input}")}]}],"store":true});
    if model == "gpt-5.4-mini" {
        value["text"] = json!({"verbosity":"low"});
        value["reasoning"] = json!({"effort":"none","summary":"auto"});
    } else {
        value["text"] = json!({"verbosity":"medium"});
        value["temperature"] = json!(0.3);
    }
    value
}
pub fn extract_output(body: &Value) -> Result<String, String> {
    if let Some(text) = body
        .get("output_text")
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty())
    {
        return Ok(text.into());
    }
    let mut output = String::new();
    if let Some(items) = body.get("output").and_then(Value::as_array) {
        for item in items {
            if let Some(content) = item.get("content").and_then(Value::as_array) {
                for part in content {
                    if part["type"] == "output_text"
                        && let Some(text) = part["text"].as_str()
                    {
                        output.push_str(text);
                    }
                }
            }
        }
    }
    if output.trim().is_empty() {
        Err("OpenAI returned no replacement text.".into())
    } else {
        Ok(output)
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default, rename_all = "PascalCase")]
pub struct Settings {
    pub developer_logging: bool,
    pub start_on_login: bool,
    pub model: String,
    #[serde(flatten)]
    pub extra: serde_json::Map<String, Value>,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            developer_logging: false,
            start_on_login: false,
            model: "gpt-4.1".into(),
            extra: Default::default(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn markdown_links_are_protected_as_complete_literals_before_the_model() {
        for link in [
            r"[Inbox](https://example.com/a_(b\))",
            r"[Inbox](https://example.com/a_\(b\))",
            r"[Inbox](https://example.com/a\))",
            "[Project [2026]](https://example.com/a_(b))",
        ] {
            let input = format!("Fix teh word and keep {link} intact.");
            let protection = Protection::new(&input).unwrap();
            assert_eq!(protection.entries.len(), 1, "{link}");
            assert_eq!(protection.entries[0].value, link);
            assert_eq!(
                protection.text,
                "Fix teh word and keep __USC_LITERAL_0_1__ intact."
            );
            let result = Replacements::default()
                .process(&protection.text.replace("teh", "the"), &protection)
                .unwrap()
                .0;
            assert_eq!(result, input.replace("teh", "the"));
        }
    }
    #[test]
    fn protected_literal_failure_is_atomic() {
        let p = Protection::new("Use https://example.com and C:\\Data\\x.txt").unwrap();
        assert_eq!(p.entries.len(), 2);
        assert_eq!(
            p.restore(p.text.clone()).unwrap(),
            "Use https://example.com and C:\\Data\\x.txt"
        );
        assert!(
            p.restore(format!("{} {}", p.text, p.entries[0].placeholder))
                .is_err()
        );
        assert!(p.restore("missing".into()).is_err());
    }
    #[test]
    fn csharp_literal_kinds_links_and_collision_contracts() {
        let cases = [
            ("See https://example.com/open-ai?q=GitHub.", "url"),
            (
                "Session d5c821f1-68a1-4a4b-9e69-e2f8882f373e failed.",
                "uuid",
            ),
            ("Use sk-proj-abcdefghijklmnopqrstuvwxyz123456.", "api_key"),
            ("Token ghp_abcdefghijklmnopqrstuvwxyz1234567890.", "api_key"),
            ("api_key = \"abcDEF1234567890+/=\"", "api_key"),
            (
                "Open \"C:\\Users\\Elliot\\Documents\\Open AI\\notes.txt\" now.",
                "file_path",
            ),
            (r"Open \\server\share\folder\notes.txt now.", "file_path"),
            ("Read /usr/local/share/app/config.json next.", "file_path"),
            ("Edit src/TextPostProcessor.cs next.", "file_path"),
            (
                "Request req_abcDEF12345678901234567890 failed.",
                "opaque_id",
            ),
        ];
        for (input, kind) in cases {
            let protected = Protection::new(input).unwrap();
            assert_eq!(protected.entries.len(), 1, "{input}");
            assert_eq!(protected.entries[0].kind, kind, "{input}");
            assert_eq!(protected.restore(protected.text.clone()).unwrap(), input);
        }
        let input =
            "github: [https://github.com/pmxi/purdue-sso](https://github.com/pmxi/purdue-sso)";
        let protected = Protection::new(input).unwrap();
        assert_eq!(protected.entries.len(), 1);
        assert!(protected.entries[0].value.starts_with("[https://"));
        assert_eq!(
            protected
                .restore(protected.text.replace("github", "GitHub"))
                .unwrap(),
            input.replacen("github", "GitHub", 1)
        );
        let input = "Keep __USC_LITERAL_0_1__ and https://example.com.";
        let protected = Protection::new(input).unwrap();
        assert!(
            !protected.entries[0]
                .placeholder
                .starts_with("__USC_LITERAL_0_")
        );
        assert_eq!(protected.restore(protected.text.clone()).unwrap(), input);
    }
    #[test]
    fn replacements_do_not_cascade() {
        let r =
            Replacements::parse(br#"{"ABC":["ab"],"DEF":["ABC"],"Competition":["Comp"]}"#).unwrap();
        assert_eq!(r.apply("ab Competition").0, "ABC Competition");
    }
    #[test]
    fn model_parameters_are_exclusive() {
        let a = payload("hello", "gpt-4.1");
        let b = payload("hello", "gpt-5.4-mini");
        assert!(a.get("reasoning").is_none());
        assert!(b.get("temperature").is_none());
    }
}
