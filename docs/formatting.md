# Formatting

Everything that changes text between capture and paste, in order:
terminal normalization → literal protection → model → echo/prompt-leak strip → replacements →
source line-end spacing → literal restore → rich mapping. Replay any of it without the desktop:
`universal-spell-check.exe --transform-json <replacements.json>` or `--rich-json`, with a JSON row
on stdin (a `--json` log row works; `active_exe` selects target-specific paths).

## Terminal normalization (`terminal.rs`)

Only for `WindowsTerminal`, `Code`, `powershell`, `pwsh`, `cmd` and `bash`. It removes the `▎`
wrap marker, rejoins soft-wrapped Windows paths, keeps blank-line paragraph breaks and list
items, and collapses terminal-width soft wraps into spaces. A bare `\r\n` with no following
indent is left alone.

## Protected literals (`core.rs`, `literal_regex.txt`)

Before the request, URLs, complete Markdown links, UUIDs and opaque IDs, common API keys, JWTs,
secret-looking assignments, and Windows, UNC and POSIX paths become numbered placeholders.
Quoted paths may contain spaces. Replacements run while placeholders are still in place, then
values are restored byte for byte. A missing or duplicated placeholder fails the run and nothing
is pasted. In T3 Code (`t3.rs`), a whole `[Assistant quote](t3-citation://...)` link is one
literal, and only selections containing one get an extra prompt line telling the model to copy
each placeholder exactly once (protection alone was not enough in live tests).

## Replacements (`replacements.json`)

Canonical spelling → list of variants, applied case-sensitively, longest variant first. The
worker uses the file next to the exe (or the nearest ancestor in a dev checkout) and rereads it
after every correction and settings change, off the hot path, so an edit applies from the second
correction after it. An unreadable or invalid file logs `replacements_unavailable` and applies no
replacements.

## Prompt-leak and echo guard

The request text is `instructions: <PROMPT>\ntext input: <selection>`. If the model echoes the
instruction line or the `text input:` label, both are stripped. An exact echo of the input
followed by `---` and a corrected copy keeps only the correction (`echoed_original_removed`).
When the model returns the same number of lines, each line's trailing spaces and tabs are copied
from the source, so model-added Markdown hard breaks never reach the paste.

## Rich text (`rich.rs`, `rich_links.rs`, `docs_native.rs`)

Plain text cannot carry bold, links, lists or paragraph spacing, so when the source offered
CF_HTML the app rebuilds HTML around the correction and writes both flavors. Browsers already
flatten some structure before the app sees it (a gap made by CSS margin on a `<div>` is gone
from `text/plain`), so no per-site rule can recover it; only the HTML path can.

`rich_text.mode` records which path ran:

- `aligned_html`: the general path. Each source HTML text node must map in order onto the copied Unicode text, and every model edit must land inside a mapped node. Tags and attributes are kept. HTML text nodes missing from the Unicode flavor (ChatGPT omits headings) are preserved and also inserted into the Unicode paste.
- `html`: simple ChatGPT ProseMirror paragraphs and LinkedIn messaging paragraphs (detected from the CF_HTML `SourceURL`). Empty paragraphs stay as markup so they don't paste as extra lines.
- `list_body_text`: one bare list item. Only the corrected body is pasted as text, because ChatGPT nests a pasted HTML block inside the current item.
- `nested_list_html` and `structured_list_html`: list trees where every paragraph maps to exactly one generated Unicode line with the same marker before and after.
- `docs_native`: Google Docs. The copied Chromium native slice is verified against the selection, its style positions are remapped with the same diff, and its cache ID is removed so Docs cannot paste the original slice instead.

Codex link chips are reconciled with their Markdown destinations by occurrence order. Any
ambiguity keeps the original destinations and reports an unverified `link_status`; it never
guesses. Diffs have fixed work budgets: over budget, the result is `alignment_budget_exceeded`
and the app pastes plain text. A coarse display diff is never used as a formatting map. Any
refusal or exception still pastes the corrected Unicode text, with `rich_text.reason` saying why.
