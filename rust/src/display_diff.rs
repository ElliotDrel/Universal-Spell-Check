//! Human-readable diffs for the Activity feed only.
//! Never use these edits for rich-text style or Google Docs position mapping.
use crate::diff::{self, Edit, Kind};
use std::collections::HashMap;

#[derive(Clone, Copy)]
struct Token<'a> {
    full: &'a str,
    prefix: &'a str,
    core: &'a str,
    word: bool,
}

fn word_char(ch: char) -> bool {
    ch.is_alphanumeric()
        || ch == '_'
        || matches!(
            ch,
            '\u{0300}'..='\u{036f}'
                | '\u{1ab0}'..='\u{1aff}'
                | '\u{1dc0}'..='\u{1dff}'
                | '\u{20d0}'..='\u{20ff}'
                | '\u{fe20}'..='\u{fe2f}'
        )
}

// Attach leading whitespace to the next word or punctuation mark.
// Whitespace must not become a cheap Myers anchor between unrelated words.
fn tokenize(input: &str) -> Vec<Token<'_>> {
    let mut chars = input.char_indices().peekable();
    let mut tokens = Vec::new();
    while let Some(&(start, _)) = chars.peek() {
        while chars.peek().is_some_and(|(_, ch)| ch.is_whitespace()) {
            chars.next();
        }
        let Some(&(core_start, first)) = chars.peek() else {
            tokens.push(Token {
                full: &input[start..],
                prefix: &input[start..],
                core: "",
                word: false,
            });
            break;
        };
        chars.next();
        let word = word_char(first);
        let mut end = core_start + first.len_utf8();
        if word {
            while let Some(&(at, ch)) = chars.peek() {
                let internal_apostrophe = matches!(ch, '\'' | '’')
                    && input[at + ch.len_utf8()..]
                        .chars()
                        .next()
                        .is_some_and(word_char);
                if !word_char(ch) && !internal_apostrophe {
                    break;
                }
                chars.next();
                end = at + ch.len_utf8();
            }
        }
        tokens.push(Token {
            full: &input[start..end],
            prefix: &input[start..core_start],
            core: &input[core_start..end],
            word,
        });
    }
    tokens
}

fn push(out: &mut Vec<Edit>, kind: Kind, text: &str) {
    if text.is_empty() {
        return;
    }
    if let Some(last) = out.last_mut().filter(|edit| edit.kind == kind) {
        last.text.extend(text.encode_utf16());
    } else {
        out.push(Edit {
            kind,
            text: text.encode_utf16().collect(),
        });
    }
}

fn push_character_edits(out: &mut Vec<Edit>, before: &str, after: &str) {
    let a: Vec<_> = before.encode_utf16().collect();
    let b: Vec<_> = after.encode_utf16().collect();
    for edit in diff::diff(&a, &b) {
        if let Some(last) = out.last_mut().filter(|last| last.kind == edit.kind) {
            last.text.extend(edit.text);
        } else {
            out.push(edit);
        }
    }
}

// A one- or two-character typo is worth refining. Unrelated replacement words
// stay intact, even if an LCS algorithm happens to match some internal letters.
fn small_typo(before: &str, after: &str) -> bool {
    if !before.is_ascii() || !after.is_ascii() || before == after {
        return false;
    }
    let a = before.as_bytes();
    let b = after.as_bytes();
    if a.is_empty() || b.is_empty() || a.len().max(b.len()) > 48 || a.len().abs_diff(b.len()) > 2 {
        return false;
    }
    let mismatches: Vec<_> = if a.len() == b.len() {
        a.iter()
            .zip(b)
            .enumerate()
            .filter_map(|(i, (x, y))| (x != y).then_some(i))
            .collect()
    } else {
        Vec::new()
    };
    let transposition = mismatches.len() == 2
        && mismatches[1] == mismatches[0] + 1
        && a[mismatches[0]] == b[mismatches[1]]
        && a[mismatches[1]] == b[mismatches[0]];
    if transposition {
        return true;
    }
    let min_len = a.len().min(b.len());
    if min_len < 4 {
        return false;
    }
    let mut previous: Vec<usize> = (0..=b.len()).collect();
    let mut current = vec![0; b.len() + 1];
    for (i, left) in a.iter().enumerate() {
        current[0] = i + 1;
        for (j, right) in b.iter().enumerate() {
            current[j + 1] = (previous[j + 1] + 1)
                .min(current[j] + 1)
                .min(previous[j] + usize::from(left != right));
        }
        std::mem::swap(&mut previous, &mut current);
    }
    previous[b.len()] <= if min_len >= 6 { 2 } else { 1 }
}

fn flush(
    out: &mut Vec<Edit>,
    removed: &mut Vec<&Token<'_>>,
    added: &mut Vec<&Token<'_>>,
) {
    if removed.len() == 1 && added.len() == 1 {
        let old = removed[0];
        let new = added[0];
        if old.core == new.core {
            push_character_edits(out, old.prefix, new.prefix);
            push(out, Kind::Equal, old.core);
            removed.clear();
            added.clear();
            return;
        }
        if old.word && new.word && old.prefix == new.prefix && small_typo(old.core, new.core) {
            push(out, Kind::Equal, old.prefix);
            push_character_edits(out, old.core, new.core);
            removed.clear();
            added.clear();
            return;
        }
    }
    for token in removed.drain(..) {
        push(out, Kind::Delete, token.full);
    }
    for token in added.drain(..) {
        push(out, Kind::Insert, token.full);
    }
}

/// Compute a presentation-only diff: Myers over complete words/punctuation,
/// with character-level edits only for small spelling and whitespace changes.
pub fn display_diff(before: &str, after: &str) -> Vec<Edit> {
    if before == after {
        let mut result = Vec::new();
        push(&mut result, Kind::Equal, before);
        return result;
    }

    let old = tokenize(before);
    let new = tokenize(after);
    let mut ids = HashMap::new();
    for token in old.iter().chain(&new) {
        if !ids.contains_key(token.full) {
            let Ok(id) = u16::try_from(ids.len()) else {
                return vec![
                    Edit {
                        kind: Kind::Delete,
                        text: before.encode_utf16().collect(),
                    },
                    Edit {
                        kind: Kind::Insert,
                        text: after.encode_utf16().collect(),
                    },
                ];
            };
            ids.insert(token.full, id);
        }
    }
    let a: Vec<u16> = old.iter().map(|token| ids[token.full]).collect();
    let b: Vec<u16> = new.iter().map(|token| ids[token.full]).collect();

    let mut prefix = 0;
    while prefix < a.len().min(b.len()) && a[prefix] == b[prefix] {
        prefix += 1;
    }
    let mut suffix = 0;
    while suffix < a.len() - prefix
        && suffix < b.len() - prefix
        && a[a.len() - suffix - 1] == b[b.len() - suffix - 1]
    {
        suffix += 1;
    }

    let mut result = Vec::new();
    for token in &old[..prefix] {
        push(&mut result, Kind::Equal, token.full);
    }

    let middle_a = &a[prefix..a.len() - suffix];
    let middle_b = &b[prefix..b.len() - suffix];
    if let Some(steps) = diff::sparse_diff(middle_a, middle_b) {
        let (mut i, mut j) = (prefix, prefix);
        let (mut removed, mut added) = (Vec::new(), Vec::new());
        for step in steps {
            for _ in step.text {
                match step.kind {
                    Kind::Equal => {
                        flush(&mut result, &mut removed, &mut added);
                        push(&mut result, Kind::Equal, old[i].full);
                        i += 1;
                        j += 1;
                    }
                    Kind::Delete => {
                        removed.push(&old[i]);
                        i += 1;
                    }
                    Kind::Insert => {
                        added.push(&new[j]);
                        j += 1;
                    }
                }
            }
        }
        flush(&mut result, &mut removed, &mut added);
        debug_assert_eq!(i, a.len() - suffix);
        debug_assert_eq!(j, b.len() - suffix);
    } else {
        // An extreme rewrite exceeds the bounded Myers budget. Display it
        // coarsely rather than doing unbounded work on the dashboard thread.
        for token in &old[prefix..old.len() - suffix] {
            push(&mut result, Kind::Delete, token.full);
        }
        for token in &new[prefix..new.len() - suffix] {
            push(&mut result, Kind::Insert, token.full);
        }
    }

    for token in &old[old.len() - suffix..] {
        push(&mut result, Kind::Equal, token.full);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rendered(edits: &[Edit], kinds: &[Kind]) -> String {
        let units: Vec<_> = edits
            .iter()
            .filter(|edit| kinds.contains(&edit.kind))
            .flat_map(|edit| edit.text.iter().copied())
            .collect();
        String::from_utf16(&units).unwrap()
    }

    fn check(before: &str, after: &str) -> Vec<Edit> {
        let edits = display_diff(before, after);
        assert_eq!(rendered(&edits, &[Kind::Equal, Kind::Delete]), before);
        assert_eq!(rendered(&edits, &[Kind::Equal, Kind::Insert]), after);
        edits
    }

    #[test]
    fn semantic_rewrites_keep_complete_words() {
        let edits = check(
            "We need to discuss the plan tomorrow.",
            "We should review the strategy tomorrow.",
        );
        assert!(rendered(&edits, &[Kind::Delete]).contains("need to discuss"));
        assert!(rendered(&edits, &[Kind::Insert]).contains("should review"));
        assert!(rendered(&edits, &[Kind::Equal]).contains(" tomorrow."));
    }

    #[test]
    fn small_typo_refines_characters_inside_one_word() {
        let edits = check("I recieve your mesage.", "I receive your message.");
        assert!(rendered(&edits, &[Kind::Equal]).contains("I rec"));
        assert!(rendered(&edits, &[Kind::Delete]).len() < "recieve mesage".len());
    }

    #[test]
    fn punctuation_insertion_leaves_words_unchanged() {
        let edits = check("Hello world.", "Hello, world.");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "");
        assert_eq!(rendered(&edits, &[Kind::Insert]), ",");
    }

    #[test]
    fn whitespace_only_change_does_not_highlight_word() {
        let edits = check("Hello world", "Hello  world");
        assert_eq!(rendered(&edits, &[Kind::Insert]), " ");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "");
    }

    #[test]
    fn apostrophes_and_unicode_preserve_valid_boundaries() {
        check("I cant see 🦀 café.", "I can't see 🦀 cafés.");
        check("We’ll go tomorrow.", "We'll go tomorrow.");
        check("a\u{0301} 🐙 test", "á 🐙 test");
    }

    #[test]
    fn repeated_words_and_newlines_are_lossless() {
        check("the the the\nwhat what", "the that the\nwhat?");
        check("First line\n\nSecond line", "First line\n\nBetter second line");
        check(" no change ", " no change ");
        check("", "some words");
        check("some words", "");
    }

    #[test]
    fn extreme_rewrite_stays_bounded_and_lossless() {
        let before = "alpha ".repeat(500);
        let after = "beta ".repeat(500);
        check(&before, &after);
    }
}
