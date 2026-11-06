use crate::{
    diff::{Kind, try_diff},
    docs_native,
};
use regex::Regex;
use serde::Serialize;
use std::sync::LazyLock;
macro_rules! re {
    ($p:literal) => {{
        static R: LazyLock<Regex> = LazyLock::new(|| Regex::new($p).unwrap());
        &*R
    }};
}
#[derive(Clone, Debug, Serialize)]
pub struct Rich {
    pub text: String,
    pub html: String,
    pub mode: String,
    pub attempted: bool,
    pub applied: bool,
    pub reason: String,
    pub paragraph_count: usize,
    pub link_status: String,
    #[serde(skip)]
    pub native: Option<Vec<u8>>,
    pub native_docs_status: String,
}
impl Rich {
    fn declined(reason: &str, text: &str, count: usize) -> Self {
        let mut r = Self::plain(reason, text);
        r.paragraph_count = count;
        r
    }
    fn plain(reason: &str, text: &str) -> Self {
        Self {
            text: text.into(),
            html: String::new(),
            mode: "none".into(),
            attempted: reason != "no_html",
            applied: false,
            reason: reason.into(),
            paragraph_count: 0,
            link_status: "not_applicable".into(),
            native: None,
            native_docs_status: "not_applicable".into(),
        }
    }
    fn made(text: String, fragment: &str, mode: &str, count: usize) -> Self {
        Self {
            text,
            html: if fragment.is_empty() {
                String::new()
            } else {
                build_cf_html(fragment)
            },
            mode: mode.into(),
            attempted: true,
            applied: true,
            reason: String::new(),
            paragraph_count: count,
            link_status: "not_applicable".into(),
            native: None,
            native_docs_status: "not_applicable".into(),
        }
    }
}
pub fn build_cf_html(fragment: &str) -> String {
    let prefix = "<html>\r\n<body>\r\n<!--StartFragment-->";
    let suffix = "<!--EndFragment-->\r\n</body>\r\n</html>";
    let start = 105 + prefix.len();
    let end = start + fragment.len();
    format!(
        "Version:0.9\r\nStartHTML:{:010}\r\nEndHTML:{:010}\r\nStartFragment:{start:010}\r\nEndFragment:{end:010}\r\n{prefix}{fragment}{suffix}",
        105,
        end + suffix.len()
    )
}
pub fn fragment(html: &str) -> Option<&str> {
    let offset = |name: &str| {
        html.lines()
            .find_map(|l| l.strip_prefix(name))
            .and_then(|s| s.trim().parse::<usize>().ok())
    };
    let start = offset("StartFragment:")?;
    let end = offset("EndFragment:")?;
    html.get(start..end)
}
fn decode(text: &str) -> String {
    html_escape::decode_html_entities(text).into_owned()
}
fn encode(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}
fn sections(text: &str) -> Vec<String> {
    re!(r"\n{2,}")
        .split(&text.replace("\r\n", "\n"))
        .map(str::to_owned)
        .collect()
}
fn edges(original: &str, corrected: &str, nbsp: bool) -> String {
    let edge = |c| c == ' ' || c == '\t' || (nbsp && c == '\u{a0}');
    let lead = original.len() - original.trim_start_matches(edge).len();
    let tail = original.trim_end_matches(edge).len();
    format!(
        "{}{}{}",
        &original[..lead],
        corrected.trim_matches(edge),
        &original[tail..]
    )
}
fn remove_breaks(text: &str) -> String {
    re!(r"[ \t]*[\r\n]+[ \t]*")
        .replace_all(text, " ")
        .trim_end_matches([' ', '\t'])
        .to_owned()
}
fn split_prefix(text: &str) -> (&str, &str) {
    if let Some(m) = re!(r"^[ \t]*(?:[0-9]+\.|-) ").find(text) {
        (&text[..m.end()], &text[m.end()..])
    } else {
        ("", text)
    }
}
#[derive(Clone)]
struct Paragraph {
    start: usize,
    end: usize,
    text: String,
}
fn paragraphs(fragment: &str, linkedin: bool) -> Vec<Paragraph> {
    let pattern = if linkedin {
        re!(r"(?i)<p\b[^>]*>(?:([^<]*)|<br\b[^>]*>)</p>")
    } else {
        re!(r"(?i)<p\b[^>]*>(?:([^<]*)|<span\b[^>]*>([^<]*)</span>)</p>")
    };
    pattern
        .captures_iter(fragment)
        .map(|c| {
            let all = c.get(0).unwrap();
            let text = c.get(1).or_else(|| c.get(2));
            Paragraph {
                start: text.map_or(all.start() + all.as_str().find('>').unwrap() + 1, |m| {
                    m.start()
                }),
                end: text.map_or(all.start() + all.as_str().find('>').unwrap() + 1, |m| {
                    m.end()
                }),
                text: decode(text.map_or("", |m| m.as_str())),
            }
        })
        .collect()
}
fn replace_paragraphs(fragment: &str, p: &[Paragraph], bodies: &[String]) -> String {
    let mut rebuilt = String::new();
    let mut cursor = 0;
    for (node, body) in p.iter().zip(bodies) {
        rebuilt.push_str(&fragment[cursor..node.start]);
        rebuilt.push_str(&encode(body));
        cursor = node.end;
    }
    rebuilt.push_str(&fragment[cursor..]);
    rebuilt
}
#[cfg(feature = "experimental-staged-capture")]
pub struct PreparedSource {
    html: String,
    source: String,
    paragraphs: Vec<Paragraph>,
    rest_empty: bool,
}
#[cfg(feature = "experimental-staged-capture")]
impl PreparedSource {
    pub fn new(html: &str, source: &str) -> Self {
        let fragment = fragment(html).unwrap_or("");
        Self {
            html: html.into(),
            source: source.into(),
            paragraphs: paragraphs(fragment, false),
            rest_empty: re!(r"(?i)<p\b[^>]*>(?:[^<]*|<span\b[^>]*>[^<]*</span>)</p>")
                .replace_all(fragment, "")
                .trim()
                .is_empty(),
        }
    }
    pub fn rewrite(&self, corrected: &str, native: &[u8]) -> Rich {
        rewrite_impl(&self.html, &self.source, corrected, native, Some(self))
    }
}
// Ordinary builds never construct a prepared inventory.
#[cfg(not(feature = "experimental-staged-capture"))]
type PreparedSource = ();
pub fn rewrite(html: &str, source: &str, corrected: &str, native: &[u8]) -> Rich {
    rewrite_impl(html, source, corrected, native, None)
}
fn rewrite_impl(
    html: &str,
    source: &str,
    corrected: &str,
    native: &[u8],
    prepared: Option<&PreparedSource>,
) -> Rich {
    let Some(fragment) = fragment(html).filter(|f| {
        f.contains("data-pm-slice") && re!(r"(?i)\brich-link-source-app-id\b").is_match(f)
    }) else {
        return rewrite_base(html, source, corrected, native, prepared);
    };
    match crate::rich_links::project(fragment, source, corrected) {
        Ok(Some(projected)) => {
            let mut result = rewrite_base(
                &build_cf_html(&projected.html),
                &projected.source,
                &projected.corrected,
                native,
                None,
            );
            if result.applied {
                result.link_status = "verified".into();
            } else {
                // A plain fallback must keep the destinations from the original Unicode payload.
                result.text = corrected.into();
                result.link_status = "unverified_alignment".into();
            }
            result
        }
        outcome => {
            let mut result = rewrite_base(html, source, corrected, native, prepared);
            result.link_status = match outcome {
                Err(reason) => reason,
                _ => "not_applicable",
            }
            .into();
            result
        }
    }
}

fn rewrite_base(
    html: &str,
    source: &str,
    corrected: &str,
    native: &[u8],
    _prepared: Option<&PreparedSource>,
) -> Rich {
    if html.is_empty() {
        return Rich::plain("no_html", corrected);
    }
    let Some(fragment) = fragment(html) else {
        return Rich::plain("invalid_cf_html", corrected);
    };
    let linkedin = html
        .lines()
        .find_map(|l| l.strip_prefix("SourceURL:"))
        .is_some_and(|u| {
            re!(r"(?i)^https://(?:www\.)?linkedin\.com(?::443)?/messaging/").is_match(u.trim())
        });
    if linkedin {
        return linkedin_rewrite(fragment, source, corrected);
    }
    if re!(r#"(?i)<b\b[^>]*\bid=["']docs-internal-guid-[^"']+["']"#).is_match(fragment) {
        return docs_rewrite(fragment, source, corrected, native);
    }
    if !fragment.contains("data-pm-slice") {
        return Rich::plain("unsupported_editor", corrected);
    }
    let corrected = if (fragment.contains("skill-mention-name=")
        || fragment.contains("chatgpt-library-file-mention-id="))
        && !source.contains(['\r', '\n'])
    {
        remove_breaks(corrected)
    } else {
        corrected.into()
    };
    #[cfg(feature = "experimental-staged-capture")]
    let p: std::borrow::Cow<'_, [Paragraph]> = _prepared
        .map(|p| std::borrow::Cow::Borrowed(p.paragraphs.as_slice()))
        .unwrap_or_else(|| std::borrow::Cow::Owned(paragraphs(fragment, false)));
    #[cfg(not(feature = "experimental-staged-capture"))]
    let p = paragraphs(fragment, false);
    if let Some(result) = structured_list(fragment, source, &corrected, &p) {
        return result;
    }
    let rest_empty = || {
        re!(r"(?i)<p\b[^>]*>(?:[^<]*|<span\b[^>]*>[^<]*</span>)</p>")
            .replace_all(fragment, "")
            .trim()
            .is_empty()
    };
    #[cfg(feature = "experimental-staged-capture")]
    let rest_empty = _prepared.map_or_else(rest_empty, |p| p.rest_empty);
    #[cfg(not(feature = "experimental-staged-capture"))]
    let rest_empty = rest_empty();
    if p.is_empty() || !rest_empty {
        return align(fragment, source, &corrected, false);
    }
    let bodies: Vec<_> = p
        .iter()
        .filter(|p| !p.text.is_empty())
        .map(|p| p.text.clone())
        .collect();
    let mut source_sections = sections(source);
    let mut target_sections = sections(&corrected);
    let mut list = false;
    if bodies != source_sections
        && p.len() == 1
        && bodies.len() == 1
        && source_sections.len() == 1
        && target_sections.len() == 1
        && (fragment.contains("&quot;ordered_list&quot;") || fragment.contains("&quot;list&quot;"))
    {
        let (a, b) = split_prefix(&source_sections[0]);
        let (c, d) = split_prefix(&target_sections[0]);
        if !a.is_empty() && a == c && b == bodies[0] {
            source_sections = vec![b.into()];
            target_sections = vec![d.into()];
            list = true;
        }
    }
    if bodies != source_sections || target_sections.len() != bodies.len() {
        let mut result = align(fragment, source, &corrected, false);
        if !result.applied && result.reason != "alignment_budget_exceeded" {
            result.reason = "model_mismatch".into();
            result.paragraph_count = p.len();
        }
        return result;
    }
    for (i, body) in bodies.iter().enumerate() {
        target_sections[i] = edges(body, &target_sections[i], false);
    }
    if list {
        return Rich::made(target_sections[0].clone(), "", "list_body_text", p.len());
    }
    let normalized = corrected.replace("\r\n", "\n");
    let breaks: Vec<_> = re!(r"\n{2,}")
        .find_iter(&normalized)
        .map(|m| m.as_str())
        .collect();
    let mut text = String::new();
    for (i, section) in target_sections.iter().enumerate() {
        text.push_str(section);
        if let Some(b) = breaks.get(i) {
            text.push_str(b);
        }
    }
    let mut index = 0;
    let all_bodies: Vec<_> = p
        .iter()
        .map(|p| {
            if p.text.is_empty() {
                String::new()
            } else {
                let s = target_sections[index].clone();
                index += 1;
                s
            }
        })
        .collect();
    Rich::made(
        text,
        &replace_paragraphs(fragment, &p, &all_bodies),
        "html",
        p.len(),
    )
}
fn linkedin_rewrite(fragment: &str, source: &str, corrected: &str) -> Rich {
    let p = paragraphs(fragment, true);
    let rest = re!(r"(?i)<p\b[^>]*>(?:[^<]*|<br\b[^>]*>)</p>").replace_all(fragment, "");
    if p.is_empty() || !rest.trim().is_empty() {
        return Rich::plain("unsupported_fragment", corrected);
    }
    if source.replace("\r\n", "\n").replace('\u{a0}', " ")
        != p.iter()
            .map(|p| p.text.as_str())
            .collect::<Vec<_>>()
            .join("\n\n")
            .replace('\u{a0}', " ")
    {
        return Rich::declined("model_mismatch", corrected, p.len());
    }
    let sections = sections(corrected);
    if sections.len() != p.iter().filter(|p| !p.text.is_empty()).count()
        || sections.iter().any(|s| s.contains('\n'))
    {
        return Rich::declined("model_mismatch", corrected, p.len());
    }
    let mut index = 0;
    let bodies: Vec<_> = p
        .iter()
        .map(|p| {
            if p.text.is_empty() {
                String::new()
            } else {
                let body = edges(&p.text, &sections[index], true);
                index += 1;
                body
            }
        })
        .collect();
    Rich::made(
        bodies.join("\n").replace('\u{a0}', " "),
        &replace_paragraphs(fragment, &p, &bodies),
        "html",
        p.len(),
    )
}
fn structured_list(fragment: &str, source: &str, corrected: &str, p: &[Paragraph]) -> Option<Rich> {
    if p.len() < 2 || !re!(r"(?i)^<(?:ol|ul|li)\b").is_match(fragment) {
        return None;
    }
    let rest =
        re!(r"(?i)<p\b[^>]*>(?:[^<]*|<span\b[^>]*>[^<]*</span>)</p>").replace_all(fragment, "");
    if !re!(r"(?i)</?(?:ol|ul|li)\b[^>]*>")
        .replace_all(&rest, "")
        .trim()
        .is_empty()
    {
        return None;
    }
    let source = source.replace("\r\n", "\n");
    let lines: Vec<_> = source.split('\n').collect();
    if lines.len() != p.len() {
        return None;
    }
    let target = corrected.replace("\r\n", "\n");
    let targets: Vec<_> = target.split('\n').collect();
    let mut cursor = 0;
    let mut bodies = Vec::new();
    let mut prefixes = Vec::new();
    for (line, node) in lines.iter().zip(p) {
        let (prefix, body) = split_prefix(line);
        if body != node.text {
            return None;
        }
        while cursor < targets.len() && targets[cursor].is_empty() && !prefix.is_empty() {
            cursor += 1;
        }
        let (next_prefix, next_body) = split_prefix(targets.get(cursor)?);
        if prefix != next_prefix {
            return None;
        }
        prefixes.push(prefix);
        bodies.push(next_body.to_owned());
        cursor += 1;
    }
    if targets[cursor..].iter().any(|s| !s.is_empty()) {
        return None;
    }
    let text = prefixes
        .iter()
        .zip(&bodies)
        .map(|(p, b)| format!("{p}{b}"))
        .collect::<Vec<_>>()
        .join("\n");
    // The exact two-level ordered fragment uses its original Unicode line separators.
    let nested=p.len()==2&&re!(r#"(?i)^<li\b[^>]*data-pm-slice="[^"]+"[^>]*><p><span\b[^>]*>[^<]*</span></p><ol\b[^>]*><li><p><span\b[^>]*>[^<]*</span></p></li></ol></li>$"#).is_match(fragment)&&targets.len()==2&&prefixes.iter().all(|p|re!(r"^[ \t]*[0-9]+\. $").is_match(p));
    Some(Rich::made(
        if nested { corrected.into() } else { text },
        &replace_paragraphs(fragment, p, &bodies),
        if nested {
            "nested_list_html"
        } else {
            "structured_list_html"
        },
        p.len(),
    ))
}
#[derive(Clone)]
struct Node {
    start: usize,
    end: usize,
    position: Option<usize>,
    text: Vec<u16>,
    original: Vec<u16>,
}
fn units(text: &str) -> Vec<u16> {
    text.encode_utf16().collect()
}
fn find(hay: &[u16], needle: &[u16], from: usize) -> Option<usize> {
    if needle.is_empty() {
        return Some(from);
    }
    hay.get(from..)?
        .windows(needle.len())
        .position(|s| s == needle)
        .map(|p| p + from)
}
fn owner(nodes: &[Node], p: usize) -> Option<usize> {
    nodes
        .iter()
        .position(|n| n.position.is_some_and(|s| p >= s && p < s + n.text.len()))
}
fn insertion(nodes: &[Node], p: usize) -> Option<usize> {
    owner(nodes, p).or_else(|| {
        nodes
            .iter()
            .rposition(|n| n.position.is_some_and(|s| p == s + n.text.len()))
    })
}
fn map_offset(edits: &[crate::diff::Edit], position: usize) -> usize {
    let (mut from, mut to) = (0, 0);
    for edit in edits {
        if edit.kind == Kind::Insert {
            to += edit.text.len();
            continue;
        }
        if position <= from + edit.text.len() {
            return to
                + if edit.kind == Kind::Equal {
                    position - from
                } else {
                    0
                };
        }
        from += edit.text.len();
        if edit.kind == Kind::Equal {
            to += edit.text.len();
        }
    }
    to
}
fn align(fragment: &str, source: &str, corrected: &str, nbsp: bool) -> Rich {
    let mut result = Rich::plain("unsupported_fragment", corrected);
    let mut nodes = Vec::new();
    let bytes = fragment.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'<' {
            let mut quote = 0u8;
            let mut end = None;
            for (j, &b) in bytes.iter().enumerate().skip(i + 1) {
                if quote == 0 && (b == b'\'' || b == b'"') {
                    quote = b;
                } else if quote != 0 && b == quote {
                    quote = 0;
                } else if b == b'>' && quote == 0 {
                    end = Some(j + 1);
                    break;
                }
            }
            let Some(next) = end else {
                return result;
            };
            i = next;
            continue;
        }
        let start = i;
        while i < bytes.len() && bytes[i] != b'<' {
            i += 1;
        }
        let original = decode(&fragment[start..i]);
        let decoded = if nbsp {
            original.replace('\u{a0}', " ")
        } else {
            original.clone()
        };
        if !decoded.is_empty() {
            nodes.push(Node {
                start,
                end: i,
                position: None,
                text: units(&decoded),
                original: units(&original),
            });
        }
    }
    if nodes.is_empty() {
        return result;
    }
    let source = units(source);
    let target = units(corrected);
    let mut cursor = 0;
    for index in 0..nodes.len() {
        let candidate = find(&source, &nodes[index].text, cursor);
        let later = candidate.is_some_and(|candidate| {
            nodes[index + 1..].iter().any(|node| {
                find(&source, &node.text, cursor).is_some_and(|p| {
                    p < candidate && p + node.text.len() >= candidate + nodes[index].text.len()
                })
            })
        });
        if !later && let Some(p) = candidate {
            nodes[index].position = Some(p);
            cursor = p + nodes[index].text.len();
        }
    }
    if nodes.iter().all(|n| n.position.is_none()) {
        return result;
    }
    let Some(edits) = try_diff(&source, &target) else {
        result.reason = "alignment_budget_exceeded".into();
        return result;
    };
    let mut complete = target.clone();
    for (index, node) in nodes.iter().enumerate().rev() {
        if node.position.is_some() || String::from_utf16_lossy(&node.text).trim().is_empty() {
            continue;
        }
        let next = nodes[index + 1..]
            .iter()
            .find_map(|n| n.position)
            .unwrap_or(source.len());
        let p = map_offset(&edits, next);
        let mut recovered = node.text.clone();
        recovered.extend_from_slice(&[10, 10]);
        complete.splice(p..p, recovered);
    }
    result.text = String::from_utf16_lossy(&complete);
    let mut output = vec![Vec::<u16>::new(); nodes.len()];
    let mut position = 0;
    let mut removed_whitespace = None;
    for edit in edits {
        match edit.kind {
            Kind::Equal => {
                removed_whitespace = None;
                for _ in edit.text {
                    if let Some(n) = owner(&nodes, position) {
                        output[n].push(nodes[n].original[position - nodes[n].position.unwrap()]);
                    }
                    position += 1;
                }
            }
            Kind::Delete => {
                for c in edit.text {
                    if owner(&nodes, position).is_none() {
                        if !char::from_u32(c as u32).is_some_and(char::is_whitespace) {
                            return result;
                        }
                        if removed_whitespace.is_none() {
                            removed_whitespace = Some(position);
                        }
                    } else {
                        removed_whitespace = None;
                    }
                    position += 1;
                }
            }
            Kind::Insert => {
                let n = insertion(&nodes, position).or_else(|| {
                    if position == source.len() {
                        removed_whitespace.and_then(|p| insertion(&nodes, p))
                    } else {
                        None
                    }
                });
                let Some(n) = n else {
                    if edit
                        .text
                        .iter()
                        .all(|c| char::from_u32(*c as u32).is_some_and(char::is_whitespace))
                    {
                        continue;
                    }
                    return result;
                };
                if edit.text.contains(&10) || edit.text.contains(&13) {
                    return result;
                }
                output[n].extend(edit.text);
                removed_whitespace = None;
            }
        }
    }
    if position != source.len() {
        return result;
    }
    let mut rebuilt = String::new();
    let mut cursor = 0;
    for (index, node) in nodes.iter().enumerate() {
        rebuilt.push_str(&fragment[cursor..node.start]);
        if node.position.is_none() || output[index] == node.original {
            rebuilt.push_str(&fragment[node.start..node.end]);
        } else {
            rebuilt.push_str(&encode(&String::from_utf16_lossy(&output[index])));
        }
        cursor = node.end;
    }
    rebuilt.push_str(&fragment[cursor..]);
    Rich::made(
        result.text,
        &rebuilt,
        "aligned_html",
        paragraphs(fragment, false).len(),
    )
}
fn visible(html: &str) -> String {
    let breaks = re!(r"(?i)<br\b[^>]*>").replace_all(html, "\n");
    decode(&re!(r"<[^>]+>").replace_all(&breaks, "")).replace('\u{a0}', " ")
}
fn docs_rewrite(fragment: &str, copied: &str, corrected: &str, native: &[u8]) -> Rich {
    let fragment = re!(r#"(?i)<br\b[^>]*class=["']Apple-interchange-newline["'][^>]*>"#)
        .replace_all(fragment, "");
    let blocks_re = re!(r"(?is)<(p|h[1-6])\b[^>]*>(.*?)</(?:p|h[1-6])\s*>");
    let blocks = blocks_re.find_iter(&fragment).count();
    let serialized = if blocks == 0 {
        visible(&fragment)
    } else {
        let s = blocks_re.replace_all(&fragment, |c: &regex::Captures| {
            let body = visible(&c[2]);
            format!("{}\n", if body == "\n" { "" } else { &body })
        });
        let s = visible(&s);
        s.strip_suffix('\n').unwrap_or(&s).to_owned()
    };
    let copied_normal = copied.replace("\r\n", "\n").replace('\u{a0}', " ");
    let mut source = if blocks > 0 {
        copied_normal
            .strip_suffix('\n')
            .unwrap_or(&copied_normal)
            .to_owned()
    } else {
        copied_normal.clone()
    };
    if copied_normal == serialized || copied_normal == format!("{serialized}\n") {
        source = serialized.clone();
    }
    let mut corrected = corrected.replace("\r\n", "\n").replace('\u{a0}', " ");
    if !source.contains('\n') {
        corrected = remove_breaks(&corrected);
    }
    let bodies: Vec<_> = source.split('\n').collect();
    let mut sections: Vec<_> = corrected.split('\n').map(str::to_owned).collect();
    if sections.len() == bodies.len() + 1 && sections.last().is_some_and(|s| s.is_empty()) {
        sections.pop();
    }
    if sections.len() != bodies.len() {
        return Rich::declined("model_mismatch", &corrected, blocks);
    }
    for (i, original) in bodies.iter().enumerate() {
        if original.trim_matches([' ', '\t']).is_empty() {
            if !sections[i].trim().is_empty() {
                return Rich::declined("model_mismatch", &corrected, blocks);
            }
            sections[i] = (*original).into();
        } else {
            sections[i] = edges(original, &sections[i], false);
        }
    }
    corrected = sections.join("\n");
    let mut result = Rich::plain("model_mismatch", &corrected);
    if serialized == source {
        let rest = blocks_re.replace_all(&fragment, "");
        if blocks == 0 || visible(&rest).trim().is_empty() {
            let clean=re!(r#"(?i)<b\b[^>]*\bid=["']docs-internal-guid-[^"']+["']"#).replace_all(&fragment,|c:&regex::Captures|re!(r#"(?i)\s+id\s*=\s*(?:"docs-internal-guid-[^"]+"|'docs-internal-guid-[^']+')"#).replace_all(&c[0],"").into_owned());
            let clean = if blocks == 1 && !source.contains('\n') {
                re!(r"(?i)</?(?:ol|ul|li|p|h[1-6])\b[^>]*>")
                    .replace_all(&clean, "")
                    .into_owned()
            } else {
                clean.into_owned()
            };
            result = align(&clean, &source, &corrected, true);
        } else {
            result.reason = "unsupported_fragment".into();
        }
    }
    if !native.is_empty() {
        match docs_native::rewrite(native, copied, &result.text) {
            Ok(bytes) => {
                let text = result.text.clone();
                result = Rich::made(text, "", "docs_native", blocks);
                result.native = Some(bytes);
                result.native_docs_status = "ok".into();
            }
            Err(reason) => result.native_docs_status = reason,
        }
    } else {
        result.native_docs_status = "absent".into();
    }
    result.paragraph_count = blocks;
    result
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn precise_alignment_budget_exhaustion_is_explicit() {
        let source = "a".repeat(5000);
        let target = "b".repeat(5000);
        let html = build_cf_html(&format!(
            "<p data-pm-slice=\"0 0 []\"><strong>{source}</strong></p>"
        ));
        let out = rewrite(&html, &source, &target, &[]);
        assert!(!out.applied);
        assert_eq!(out.reason, "alignment_budget_exceeded");
        assert_eq!(out.text, target);
    }
    #[test]
    fn precise_edits_preserve_varied_structures_above_the_old_size_limit() {
        for size in [200, 800, 1500, 5000] {
            let text = format!("teh {}tail", "alpha ".repeat(size / 6));
            for (open, close, prefix) in [
                ("<blockquote><p>", "</p></blockquote>", ""),
                ("<h2>", "</h2>", ""),
                ("<ul><li><p>", "</p></li></ul>", "- "),
                ("<table><tr><td>", "</td></tr></table>", ""),
            ] {
                let f = format!(
                    "<p data-pm-slice=\"0 0 []\"><strong>{text}</strong></p>{open}<a href=\"https://example.com\">{text}</a>{close}"
                );
                let source = format!("{text}\n{prefix}{text}");
                let target = source.replace("teh", "the");
                let mapped = rewrite(&build_cf_html(&f), &source, &target, &[]);
                assert!(mapped.applied, "size {size}, {open}: {}", mapped.reason);
                assert_eq!(
                    fragment(&mapped.html),
                    Some(f.replace("teh", "the").as_str())
                );
                assert_eq!(mapped.text, target);
            }
        }
        let text = format!("teh {}teh end", "alpha ".repeat(1000));
        let f = format!("<p data-pm-slice=\"0 0 []\"><em>{text}</em></p>");
        let mapped = rewrite(&build_cf_html(&f), &text, &text.replace("teh", "the"), &[]);
        assert!(mapped.applied);
        assert_eq!(
            fragment(&mapped.html),
            Some(f.replace("teh", "the").as_str())
        );
    }
    #[test]
    fn mixed_nested_lists_preserve_inline_markup_and_blank_paragraphs() {
        let f = "<ul data-pm-slice=\"0 0 []\"><li><p>teh parent</p><ul><li><p>child <strong>teh word</strong></p></li></ul></li></ul><p></p><p>Next <span>steps:</span></p><ol start=\"1\"><li><p>teh end</p></li></ol>";
        let source = "- teh parent\n  - child teh word\n\nNext steps:\n1. teh end";
        let target = "- the parent\n  - child the word\n\nNext steps:\n1. the end";
        let out = rewrite(&build_cf_html(f), source, target, &[]);
        assert!(out.applied, "{}", out.reason);
        assert_eq!(fragment(&out.html), Some(f.replace("teh", "the").as_str()));
        assert_eq!(out.text, target);
        assert!(
            !rewrite(
                &build_cf_html(f),
                source,
                &target.replace("\n\n", "\nnew\n"),
                &[]
            )
            .applied
        );
    }
    #[test]
    fn codex_rich_link_recovers_only_an_unambiguous_copied_web_destination() {
        let f = "<p data-pm-slice=\"0 0 []\">teh <span rich-link-source-app-id=\"\" class=\"mention\"><span class=\"label\">Inbox</span></span></p>";
        let source = "teh [Inbox](https://example.com/mail?id=1&view=all)";
        let out = rewrite(
            &build_cf_html(f),
            source,
            &source.replacen("teh", "the", 1),
            &[],
        );
        assert!(out.applied);
        assert!(
            out.html
                .contains("<a href=\"https://example.com/mail?id=1&amp;view=all\"><span class=\"label\">Inbox</span></a>")
        );
        assert!(out.html.contains("the "));
        assert_eq!(out.text, "the Inbox");
        assert_eq!(out.link_status, "verified");
    }
    #[test]
    fn cfhtml_offsets_are_utf8_bytes() {
        let f = "<p>Crab 🦀 & café</p>";
        assert_eq!(fragment(&build_cf_html(f)), Some(f));
        for bad in [
            "",
            "StartFragment:999\nEndFragment:1000\n",
            "StartFragment:2\nEndFragment:1\n",
        ] {
            assert!(fragment(bad).is_none());
        }
    }
    #[test]
    fn rich_edits_cannot_inject_markup() {
        let html = build_cf_html("<p data-pm-slice=\"1 1 []\">teh <strong>cat</strong></p>");
        let out = rewrite(&html, "teh cat", "the <cat>", &[]);
        assert!(out.applied);
        assert!(out.html.contains("&lt;"));
        assert!(!out.html.contains("<cat>"));
    }
}

#[cfg(test)]
mod golden_tests {
    use super::*;
    use base64::Engine;
    use serde_json::{Value, json};
    #[test]
    fn original_csharp_formatting_contracts() {
        for (index, line) in include_str!("../tests/fixtures/csharp-formatting.jsonl")
            .lines()
            .enumerate()
        {
            let case: Value = serde_json::from_str(line).unwrap();
            let native = base64::engine::general_purpose::STANDARD
                .decode(case["native_base64"].as_str().unwrap())
                .unwrap();
            let result = rewrite(
                case["source_html"].as_str().unwrap(),
                case["input"].as_str().unwrap(),
                case["output"].as_str().unwrap(),
                &native,
            );
            let actual = json!({"text":result.text,"html":result.html,"mode":result.mode,"applied":result.applied,"reason":result.reason,"paragraph_count":result.paragraph_count,"native_base64":result.native.as_ref().map(|n|base64::engine::general_purpose::STANDARD.encode(n)).unwrap_or_default(),"native_status":result.native_docs_status});
            assert_eq!(actual, case["expected"], "C# golden case {index}");
        }
    }
    #[test]
    fn frozen_baseline_processing_matches() {
        let cases: Vec<crate::benchmark::Case> =
            serde_json::from_str(include_str!("../../bench/baseline-cases.json")).unwrap();
        let fixtures: Vec<crate::benchmark::Fixture> =
            serde_json::from_str(include_str!("../tests/fixtures/baseline-api.json")).unwrap();
        let replacements =
            crate::core::Replacements::parse(include_bytes!("../../replacements.json")).unwrap();
        for case in cases {
            let fixture = fixtures.iter().find(|f| f.id == case.id).unwrap();
            let protection = crate::core::Protection::new(&case.text).unwrap();
            let (output, _, _, _) = replacements
                .process(&fixture.raw_ai_output, &protection)
                .unwrap();
            let rich = rewrite(&case.html, &case.text, &output, &[]);
            assert_eq!(output, fixture.output, "{} text", case.id);
            assert_eq!(rich.text, fixture.paste_text, "{} paste text", case.id);
            assert_eq!(rich.html, fixture.paste_html, "{} HTML", case.id);
        }
    }
}
