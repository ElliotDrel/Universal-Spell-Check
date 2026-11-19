//! Human-readable diffs for the Activity feed only.
//! Never use these edits for rich-text style or Google Docs position mapping.
use crate::diff::{self, Edit, Kind};
use std::collections::HashMap;
use unicode_segmentation::UnicodeSegmentation;

#[derive(Clone, Copy)]
struct Token<'a> {
    full: &'a str,
    prefix: &'a str,
    core: &'a str,
    word: bool,
}

fn word_grapheme(grapheme: &str) -> bool {
    grapheme
        .chars()
        .next()
        .is_some_and(|ch| ch.is_alphanumeric() || ch == '_')
}

// Split on extended grapheme clusters, so an emoji ZWJ sequence, combining
// mark, skin tone or flag is highlighted as one visible symbol.
fn tokenize(input: &str) -> Vec<Token<'_>> {
    let mut graphemes = input.grapheme_indices(true).peekable();
    let mut tokens = Vec::new();
    while let Some(&(start, _)) = graphemes.peek() {
        while graphemes
            .peek()
            .is_some_and(|(_, g)| g.chars().all(char::is_whitespace))
        {
            graphemes.next();
        }
        let Some(&(core_start, first)) = graphemes.peek() else {
            tokens.push(Token {
                full: &input[start..],
                prefix: &input[start..],
                core: "",
                word: false,
            });
            break;
        };
        graphemes.next();
        let word = word_grapheme(first);
        let mut end = core_start + first.len();
        if word {
            while let Some(&(at, g)) = graphemes.peek() {
                let internal_apostrophe = matches!(g, "'" | "’")
                    && input[at + g.len()..]
                        .graphemes(true)
                        .next()
                        .is_some_and(word_grapheme);
                if !word_grapheme(g) && !internal_apostrophe {
                    break;
                }
                graphemes.next();
                end = at + g.len();
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

// Refinement is grapheme-aware too: slicing UTF-16 units independently can
// produce invalid halves of emoji or other multi-scalar visible symbols.
fn push_grapheme_edits(out: &mut Vec<Edit>, before: &str, after: &str) {
    if before == after {
        push(out, Kind::Equal, before);
        return;
    }
    let old: Vec<_> = before.graphemes(true).collect();
    let new: Vec<_> = after.graphemes(true).collect();
    let mut ids = HashMap::new();
    for grapheme in old.iter().chain(&new) {
        let Ok(id) = u16::try_from(ids.len()) else {
            push(out, Kind::Delete, before);
            push(out, Kind::Insert, after);
            return;
        };
        ids.entry(*grapheme).or_insert(id);
    }
    let a: Vec<_> = old.iter().map(|g| ids[g]).collect();
    let b: Vec<_> = new.iter().map(|g| ids[g]).collect();
    if let Some(steps) = diff::sparse_diff(&a, &b) {
        let (mut i, mut j) = (0, 0);
        for step in steps {
            for _ in step.text {
                match step.kind {
                    Kind::Equal => {
                        push(out, Kind::Equal, old[i]);
                        i += 1;
                        j += 1;
                    }
                    Kind::Delete => {
                        push(out, Kind::Delete, old[i]);
                        i += 1;
                    }
                    Kind::Insert => {
                        push(out, Kind::Insert, new[j]);
                        j += 1;
                    }
                }
            }
        }
        debug_assert_eq!(i, old.len());
        debug_assert_eq!(j, new.len());
    } else {
        push(out, Kind::Delete, before);
        push(out, Kind::Insert, after);
    }
}

// A one- or two-grapheme typo is worth refining; unrelated words remain whole.
fn small_typo(before: &str, after: &str) -> bool {
    if before == after {
        return false;
    }
    let a: Vec<_> = before.graphemes(true).collect();
    let b: Vec<_> = after.graphemes(true).collect();
    if a.is_empty() || b.is_empty() || a.len().max(b.len()) > 48 || a.len().abs_diff(b.len()) > 2 {
        return false;
    }
    let mismatches: Vec<_> = if a.len() == b.len() {
        a.iter()
            .zip(&b)
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

fn refine_pair(out: &mut Vec<Edit>, old: &Token<'_>, new: &Token<'_>) -> bool {
    if old.core == new.core {
        push_grapheme_edits(out, old.prefix, new.prefix);
        push(out, Kind::Equal, old.core);
        return true;
    }
    if old.word && new.word && small_typo(old.core, new.core) {
        push_grapheme_edits(out, old.prefix, new.prefix);
        push_grapheme_edits(out, old.core, new.core);
        return true;
    }
    if old.prefix == new.prefix {
        push(out, Kind::Equal, old.prefix);
        push(out, Kind::Delete, old.core);
        push(out, Kind::Insert, new.core);
        return true;
    }
    false
}

fn flush(out: &mut Vec<Edit>, removed: &mut Vec<&Token<'_>>, added: &mut Vec<&Token<'_>>) {
    // Match corresponding words in multi-token hunks where every change is a
    // spacing edit or small typo. Leave actual sentence rewrites grouped.
    let refinable = removed.len() == added.len()
        && !removed.is_empty()
        && removed.iter().zip(added.iter()).all(|(old, new)| {
            old.core == new.core || (old.word && new.word && small_typo(old.core, new.core))
        });
    if refinable {
        for (old, new) in removed.iter().zip(added.iter()) {
            refine_pair(out, old, new);
        }
        removed.clear();
        added.clear();
        return;
    }
    if removed.len() == 1 && added.len() == 1 && refine_pair(out, removed[0], added[0]) {
        removed.clear();
        added.clear();
        return;
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
    let mut next_id = 0usize;
    for token in old.iter().chain(&new) {
        if let std::collections::hash_map::Entry::Vacant(slot) = ids.entry(token.full) {
            let Ok(id) = u16::try_from(next_id) else {
                let mut result = Vec::new();
                push(&mut result, Kind::Delete, before);
                push(&mut result, Kind::Insert, after);
                return result;
            };
            slot.insert(id);
            next_id += 1;
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
        assert!(!rendered(&edits, &[Kind::Delete]).contains("recieve"));
        assert!(!rendered(&edits, &[Kind::Delete]).contains("mesage"));
        assert!(!rendered(&edits, &[Kind::Insert]).contains("receive"));
        assert!(!rendered(&edits, &[Kind::Insert]).contains("message"));
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
    fn multi_word_spacing_only_changes_keep_words_equal() {
        let edits = check("a b c", "a  b  c");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "");
        assert_eq!(rendered(&edits, &[Kind::Insert]), "  ");
        assert_eq!(rendered(&edits, &[Kind::Equal]), "a b c");
    }

    #[test]
    fn accented_typo_and_spacing_are_refined_separately() {
        let edits = check("A cafè", "A  café");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "è");
        assert_eq!(rendered(&edits, &[Kind::Insert]), " é");
        assert!(rendered(&edits, &[Kind::Equal]).contains("caf"));
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
        check(
            "First line\n\nSecond line",
            "First line\n\nBetter second line",
        );
        check(" no change ", " no change ");
        check("", "some words");
        check("some words", "");
    }

    #[test]
    fn joined_emoji_and_flags_are_atomic_highlights() {
        let edits = check("Engineer 👩‍💻 says hi", "Engineer 👨‍💻 says hi");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "👩‍💻");
        assert_eq!(rendered(&edits, &[Kind::Insert]), "👨‍💻");
        assert_eq!(rendered(&edits, &[Kind::Equal]), "Engineer  says hi");
        let edits = check("🇺🇸 👍🏻", "🇨🇦 👍🏽");
        assert_eq!(rendered(&edits, &[Kind::Delete]), "🇺🇸 👍🏻");
        assert_eq!(rendered(&edits, &[Kind::Insert]), "🇨🇦 👍🏽");
    }

    #[test]
    fn extreme_rewrite_stays_bounded_and_lossless() {
        let before = "alpha ".repeat(500);
        let after = "beta ".repeat(500);
        check(&before, &after);
    }
}
