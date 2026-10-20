//! Reconcile Codex link semantics across the HTML and Unicode clipboard flavors.
use regex::Regex;
use std::sync::LazyLock;

static TAG: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"(?is)<!--.*?-->|<(/?)([a-z][a-z0-9:-]*)\b((?:"[^"]*"|'[^']*'|[^'">])*)>"#)
        .unwrap()
});
static ATTRIBUTE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r#"([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?"#).unwrap()
});

struct LinkedNode {
    start: usize,
    inner_start: usize,
    inner_end: usize,
    end: usize,
    label: String,
    chip: bool,
}
struct Link {
    start: usize,
    end: usize,
    label: String,
    href: String,
}
pub struct Projection {
    pub html: String,
    pub source: String,
    pub corrected: String,
}
fn decoded(text: &str) -> String {
    html_escape::decode_html_entities(text).into_owned()
}
fn linked_nodes(html: &str) -> Option<Vec<LinkedNode>> {
    let mut stack = Vec::new();
    let mut nodes = Vec::new();
    for tag in TAG.captures_iter(html) {
        let Some(name) = tag.get(2) else {
            continue;
        };
        let name = name.as_str().to_ascii_lowercase();
        if name != "span" && name != "a" {
            continue;
        }
        let all = tag.get(0)?;
        if &tag[1] == "/" {
            let (open_name, start, inner_start, chip): (String, usize, usize, bool) =
                stack.pop()?;
            if open_name != name {
                return None;
            }
            if chip || name == "a" {
                nodes.push(LinkedNode {
                    start,
                    inner_start,
                    inner_end: all.start(),
                    end: all.end(),
                    label: decoded(&TAG.replace_all(&html[inner_start..all.start()], "")),
                    chip,
                });
            }
        } else {
            let chip = ATTRIBUTE
                .captures_iter(&tag[3])
                .any(|a| a[1].eq_ignore_ascii_case("rich-link-source-app-id"));
            stack.push((name, all.start(), all.end(), chip));
        }
    }
    if !stack.is_empty() {
        return None;
    }
    nodes.sort_by_key(|n| n.start);
    if nodes.windows(2).any(|w| w[0].end > w[1].start) {
        return None;
    }
    Some(nodes)
}
fn unescape(text: &str) -> String {
    let mut out = String::new();
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\\' && chars.peek().is_some_and(|c| c.is_ascii_punctuation()) {
            out.push(chars.next().unwrap());
        } else {
            out.push(c);
        }
    }
    out
}
fn links(text: &str) -> Vec<Link> {
    let bytes = text.as_bytes();
    let mut found = Vec::new();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'\\' {
            i += 2;
            continue;
        }
        if bytes[i] != b'[' {
            i += 1;
            continue;
        }
        let start = i;
        let mut end = i + 1;
        let mut depth = 1;
        while end < bytes.len() && depth > 0 {
            match bytes[end] {
                b'\\' => {
                    end += 2;
                    continue;
                }
                b'[' => depth += 1,
                b']' => depth -= 1,
                b'\r' | b'\n' => break,
                _ => {}
            }
            if depth > 0 {
                end += 1;
            }
        }
        if depth != 0 || bytes.get(end + 1) != Some(&b'(') {
            i = end.max(i + 1);
            continue;
        }
        let label_end = end;
        let href_start = end + 2;
        end = href_start;
        depth = 1;
        while end < bytes.len() && depth > 0 {
            if bytes[end] == b'\\' && bytes.get(end + 1).is_some_and(u8::is_ascii_punctuation) {
                // Codex escapes closing URL parentheses even when their opening partner is literal.
                if bytes[end + 1] == b')' && depth > 1 {
                    depth -= 1;
                }
                end += 2;
                continue;
            }
            match bytes[end] {
                b'(' => depth += 1,
                b')' => depth -= 1,
                b'\r' | b'\n' => break,
                _ => {}
            }
            if depth > 0 {
                end += 1;
            }
        }
        if depth != 0 {
            i = end.max(i + 1);
            continue;
        }
        let href = &text[href_start..end];
        if !(href.starts_with("https://") || href.starts_with("http://"))
            || href
                .chars()
                .any(|c| c.is_whitespace() || c.is_control() || matches!(c, '<' | '>'))
        {
            i = end + 1;
            continue;
        }
        found.push(Link {
            start,
            end: end + 1,
            label: unescape(&text[start + 1..label_end]),
            href: unescape(href),
        });
        i = end + 1;
    }
    found
}
pub(crate) fn literal_ranges(text: &str) -> Vec<(usize, usize)> {
    if !text.contains("](") {
        return Vec::new();
    }
    links(text)
        .into_iter()
        .map(|link| (link.start, link.end))
        .collect()
}
fn normalize(text: &str, links: &[Link], selected: &[usize]) -> String {
    let mut out = String::new();
    let mut cursor = 0;
    for &index in selected {
        let link = &links[index];
        out.push_str(&text[cursor..link.start]);
        out.push_str(&link.label);
        cursor = link.end;
    }
    out.push_str(&text[cursor..]);
    out
}
pub fn project(
    html: &str,
    source: &str,
    corrected: &str,
) -> Result<Option<Projection>, &'static str> {
    if html
        .len()
        .saturating_add(source.len())
        .saturating_add(corrected.len())
        > 2_000_000
    {
        return Err("projection_budget_exceeded");
    }
    let nodes = linked_nodes(html).ok_or("unverified_markup")?;
    if !nodes.iter().any(|n| n.chip) {
        return Ok(None);
    }
    let original = links(source);
    let target = links(corrected);
    if nodes.len() > 1024 || original.len() > 1024 || target.len() > 1024 {
        return Err("projection_budget_exceeded");
    }
    if original.len() != target.len() || original.iter().zip(&target).any(|(a, b)| a.href != b.href)
    {
        return Err("unverified_destinations");
    }
    let mut selected = Vec::new();
    let mut rebuilt = String::new();
    let mut cursor = 0;
    for node in nodes.iter().filter(|n| n.chip) {
        let candidates: Vec<_> = original
            .iter()
            .enumerate()
            .filter(|(_, l)| l.label == node.label)
            .collect();
        let peers: Vec<_> = nodes.iter().filter(|n| n.label == node.label).collect();
        if candidates.len() != peers.len() {
            return Err("unverified_occurrences");
        }
        let occurrence = peers.iter().position(|n| n.start == node.start).unwrap();
        let (index, link) = candidates[occurrence];
        if selected.last().is_some_and(|previous| *previous >= index) {
            return Err("unverified_order");
        }
        selected.push(index);
        let href = link
            .href
            .replace('&', "&amp;")
            .replace('"', "&quot;")
            .replace('<', "&lt;")
            .replace('>', "&gt;");
        rebuilt.push_str(&html[cursor..node.start]);
        rebuilt.push_str(&format!(
            "<a href=\"{href}\">{}</a>",
            &html[node.inner_start..node.inner_end]
        ));
        cursor = node.end;
    }
    rebuilt.push_str(&html[cursor..]);
    Ok(Some(Projection {
        html: rebuilt,
        source: normalize(source, &original, &selected),
        corrected: normalize(corrected, &target, &selected),
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::rich::{build_cf_html, fragment, rewrite};
    #[test]
    fn copied_codex_escaped_parentheses_survive_the_complete_request_pipeline() {
        let source = r"Fix teh word and keep [Inbox](https://example.com/a_(b\)) and [Inbox](https://example.com/c?x=1&y=2).";
        let protection = crate::core::Protection::new(source).unwrap();
        assert_eq!(protection.entries.len(), 2);
        assert!(!protection.text.contains("__)"));
        let corrected = crate::core::Replacements::default()
            .process(&protection.text.replace("teh", "the"), &protection)
            .unwrap()
            .0;
        let chip = "<span rich-link-source-app-id=''><span>Inbox</span></span>";
        let f = format!("<p data-pm-slice=\"0 0 []\">Fix teh word and keep {chip} and {chip}.</p>");
        let out = rewrite(&build_cf_html(&f), source, &corrected, &[]);
        assert!(out.applied, "{}", out.reason);
        assert_eq!(out.link_status, "verified");
        assert_eq!(out.text, "Fix the word and keep Inbox and Inbox.");
        assert!(out.html.contains("href=\"https://example.com/a_(b)\""));
        assert!(!out.html.contains("a_(b\\"));
    }
    #[test]
    fn varied_nested_markup_repeated_labels_and_parenthesized_urls() {
        for tag in [
            "<span class='chip' rich-link-source-app-id=''>",
            "<span rich-link-source-app-id=\"\" title=\"one > two\">",
            "<SPAN data-other='value' RICH-LINK-SOURCE-APP-ID=''>",
        ] {
            let chip = format!("{tag}<span><em>Inbox &amp; archive</em></span></span>");
            let f = format!(
                "<ul data-pm-slice=\"0 0 []\"><li><p>teh {chip}</p><ul><li><p>teh {chip}</p></li></ul></li></ul>"
            );
            let source = "- teh [Inbox & archive](https://example.com/a_(b))\n  - teh [Inbox & archive](https://example.com/c?x=1&y=2)";
            let out = rewrite(
                &build_cf_html(&f),
                source,
                &source.replace("teh", "the"),
                &[],
            );
            assert!(out.applied, "{}", out.reason);
            assert_eq!(out.link_status, "verified");
            assert_eq!(out.text, "- the Inbox & archive\n  - the Inbox & archive");
            assert!(out.html.contains("<a href=\"https://example.com/a_(b)\"><span><em>Inbox &amp; archive</em></span></a>"));
            assert!(
                out.html
                    .contains("<a href=\"https://example.com/c?x=1&amp;y=2\">")
            );
            assert!(out.html.contains("<ul><li><p>the"));
        }
    }
    #[test]
    fn preserve_nonchip_markdown_and_allow_corrected_link_labels() {
        let f = "<p data-pm-slice=\"0 0 []\">teh <span rich-link-source-app-id=''><b>inbox</b></span> literal [Other](https://example.com/other)</p>";
        let source =
            "teh [inbox](https://example.com/inbox) literal [Other](https://example.com/other)";
        let out = rewrite(
            &build_cf_html(f),
            source,
            "the [Inbox](https://example.com/inbox) literal [Other](https://example.com/other)",
            &[],
        );
        assert_eq!(
            out.text,
            "the Inbox literal [Other](https://example.com/other)"
        );
        assert!(out.html.contains("<b>Inbox</b>"));
        assert_eq!(out.link_status, "verified");
        let f = "<p data-pm-slice=\"0 0 []\">literal [Inbox](https://example.com/a)</p>";
        assert_eq!(
            fragment(
                &rewrite(
                    &build_cf_html(f),
                    "literal [Inbox](https://example.com/a)",
                    "literal [Inbox](https://example.com/a)",
                    &[]
                )
                .html
            ),
            Some(f)
        );
    }
    #[test]
    fn unsafe_or_unverifiable_links_are_reported_without_losing_destinations() {
        let f = "<p data-pm-slice=\"0 0 []\">teh <span rich-link-source-app-id=''><span>Inbox</span></span></p>";
        for source in [
            "teh [Inbox](javascript:alert(1))",
            "teh [Inbox](https://example.com/a) [Inbox](https://example.com/b)",
            "teh Inbox",
            "teh [Inbox](https://example.com/unclosed",
        ] {
            let out = rewrite(
                &build_cf_html(f),
                source,
                &source.replace("teh", "the"),
                &[],
            );
            assert!(out.link_status.starts_with("unverified"));
            assert!(!out.html.contains("<a href="));
            assert_eq!(out.text, source.replace("teh", "the"));
        }
        let source = "teh [Inbox](https://example.com/a)";
        let out = rewrite(
            &build_cf_html(f),
            source,
            "the [Inbox](https://example.com/changed)",
            &[],
        );
        assert_eq!(out.link_status, "unverified_destinations");
        let out = rewrite(
            &build_cf_html(f),
            source,
            "the [Inbox](https://example.com/a)\nnew paragraph",
            &[],
        );
        assert!(!out.applied);
        assert_eq!(out.link_status, "unverified_alignment");
        assert!(out.text.contains("[Inbox](https://example.com/a)"));
    }
    #[test]
    fn malformed_markup_and_pathological_inputs_decline_boundedly() {
        assert!(
            project(
                "<span rich-link-source-app-id=''><span>Inbox</span>",
                "[Inbox](https://example.com)",
                "[Inbox](https://example.com)"
            )
            .is_err()
        );
        assert!(links(&"[".repeat(20_000)).is_empty());
        assert!(links("[Project [2026]](https://example.com/a_(b))").len() == 1);
        let huge = "x".repeat(2_000_001);
        assert!(matches!(
            project(&huge, "", ""),
            Err("projection_budget_exceeded")
        ));
    }
}
