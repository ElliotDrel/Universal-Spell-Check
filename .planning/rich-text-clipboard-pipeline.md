# Plan / Spec: Rich-Text Clipboard Pipeline

## Status

**Stage 1 implemented 2026-07-20: dual-flavor capture.** Every run now reads both `CF_UNICODETEXT`
and `CF_HTML` and logs both. Nothing downstream consumes the HTML yet, so behavior is unchanged, but
the capture path is permanently the two-flavor shape the rest of this spec builds on.

**Decided:** the AI-facing representation is Markdown; the reconstruction mechanism is splicing
corrections back onto the original HTML runs. See § Chosen Approach.

**Open:** which model, and how well each candidate representation actually performs — answered by
measurement against the corpus now accumulating (§ Model Comparison), not by argument.

Triggered on 2026-07-20 by a Gmail run that silently destroyed paragraph spacing. A narrow
plain-text-only fix was prototyped and deliberately abandoned: the durable answer is to stop
round-tripping through `text/plain` at all.

The abandoned prototype (a CF_HTML paragraph-gap reconstructor with a browser-serialization model and
a self-verifying baseline check) is kept out of `src/` on purpose. Its HTML tokenizer, entity
decoder, block-element model, and margin parser are directly reusable here and are described in
§ Reusable Pieces.

---

## The bug that started this

Prod 0.7.1, gpt-4.1, two runs against the same Gmail draft.

Run 1 (`2026-07-20T09:02:57`) received this as the clipboard's plain text:

```text
Good morning Tim,\nAfter our call 2 weeks ago, ... {insert point here about ocntext}. \nWould it be ...
```

Single `\n`. The draft on screen had visible blank lines between all three paragraphs. Nothing in
our pipeline touched the text before it was logged — `terminal_normalization.applied: false`, no rule
matched, `target_formatting` none. The AI received tight lines, returned tight lines, and the paste
flattened the email.

Run 2 (`09:06:09`) received `\r\n\r\n` and preserved it. The difference was not our code: between the
runs the paragraph breaks were re-created as real empty lines.

### Measured cause

Chrome's `text/plain` clipboard serialization, verified 2026-07-20 by driving Chrome over CDP against
a synthetic page and dumping both clipboard flavors:

| Markup | `text/plain` output | Gap survives |
|---|---|---|
| `<p style="margin:0 0 1em">A</p><p …>B</p>` | `A\r\n\r\nB` | yes |
| `<div>A</div><div><br></div><div>B</div>` | `A\r\n\r\nB` | yes |
| `<div>A</div><div>B</div>` | `A\r\nB` | n/a |
| `<div style="margin-bottom:1em">A</div><div …>B</div>` | `A\r\nB` | **no** |

The rule: a block boundary emits one newline, a `</p>` emits two, an empty block contributes its own.
CSS margin emits nothing, because `text/plain` cannot express it.

This is not Gmail-specific and cannot be fixed by a `TargetFormatting` rule — the information is
destroyed by the browser before `AfterCopy` ever runs, and a rule may not touch the clipboard.

### The larger loss

Paragraph gaps are the visible symptom of a general problem. We capture `CF_UNICODETEXT` only and
write `CF_UNICODETEXT` only, so every correction also flattens, inside the selection:

- bold, italic, underline, strikethrough
- links (the anchor survives as bare text, the href is gone)
- font family, size, and color
- lists (bullets and numbering become plain lines)
- headings, blockquotes, code spans
- tables (collapsed to tab-separated text)

Nobody has reported these because corrections are usually short and unstyled. The Gmail report is the
first time the flattening was visible enough to notice.

---

## Goal

Preserve the source formatting of the selection through a correction, without changing what the AI
sees, what it costs, or how fast an ordinary plain-text run is.

Non-goals:

- Changing the model, prompt, temperature, or any `docs/model-config.md` decision.
- Making the AI aware of markup.
- Rendering, re-styling, or "improving" the user's formatting.
- Supporting RTF or any clipboard flavor beyond `CF_HTML` + `CF_UNICODETEXT` in v1.

---

## Chosen Approach

**Markdown in, run-splice back out.**

Convert the captured `CF_HTML` to Markdown and send that to the model. Markdown is the one
representation models are genuinely good at, and it carries the structure that plain text destroys —
paragraph breaks, lists, headings, emphasis, links.

Then do **not** re-render the Markdown. Map the correction back onto the original HTML runs and
splice it in, leaving every tag, attribute, and inline style byte-identical. Re-rendering Markdown
would silently normalize everything Markdown cannot express: font family, size, color, arbitrary
inline styles, nested tables, and the source app's exact block structure.

The model sees structure. The document keeps its formatting. Neither representation has to be
lossless on its own.

The alternatives below stay documented because the comparison in § Model Comparison still measures
against them — if the measurement contradicts this choice, the choice changes.

## Open Decisions

### D1. What does the AI receive? (decided: Markdown, pending measurement)

| Option | AI sees | Cost / latency | Risk |
|---|---|---|---|
| **A. Raw HTML in, corrected HTML out** | Full markup | Tokens balloon; a styled Gmail paragraph is ~2 KB of inlined CSS per block. Multi-second regression. | Model rewrites, drops, or invents tags. Prompt-leak guard and protected literals now have to survive markup. High. |
| **B. Plain text in, diff-aligned back onto the original runs** (recommended) | Exactly what it sees today | Identical token count. Added cost is local parse + diff, target sub-millisecond. | Alignment bugs, bounded by a strict verification gate and a plain-text fallback. |
| **C. Text with inline formatting sentinels** | Text plus markers | Slightly more tokens | Model eats or moves the sentinels. Already a known failure mode from the protected-literal work. |
| **D. HTML converted to Markdown, corrected, converted back** | Clean Markdown | Modest token increase over plain text; far cheaper than raw HTML | Models handle Markdown very well, so the correction quality should be high. The loss is in the conversion: Markdown cannot express font family, size, color, arbitrary inline styles, nested tables, or Gmail's exact block structure. HTML→MD→HTML is lossy in a way HTML→runs→HTML is not. |

B and D are not mutually exclusive. Markdown is the better *AI-facing* representation; run-splicing
is the better *reconstruction* mechanism. The strongest version is likely a hybrid: hand the model
Markdown so it sees real structure, then map the correction back onto the original HTML runs rather
than re-rendering the Markdown. That preserves everything Markdown cannot describe.

**This is now an empirical question, not an argument.** See § Model Comparison.

### D2. Does pasting HTML back into the source ever look worse than plain text?

Chrome's `CF_HTML` carries *computed* styles inlined on every element. Pasting that back into the
same Gmail compose box should reproduce the original, but it also hard-codes what was previously
inherited (font family, size, color). If the user later changes the message font, the corrected span
may not follow. Needs a Dev experiment before this ships; see § Verification.

### D3. Behavior when the destination is plain-text-only.

Writing both flavors means Notepad, terminals, and code editors take `CF_UNICODETEXT` and are
unaffected. Confirm no editor prefers `CF_HTML` and pastes visible markup.

---

## Model Comparison

D1 gets decided by measurement against real captured markup, not by reasoning about token counts.

### Collection — Stage 1, implemented 2026-07-20

`ClipboardLoop.CaptureSelectionAsync` reads `CF_HTML` in the same clipboard window as
`CF_UNICODETEXT`. It has to happen there: `ExcludeTextFromHistory` empties the clipboard moments
later and the source markup is gone for good. Absence of an HTML flavor is normal and never fails a
run.

Both flavors ride the ordinary path — `RunRecord.CapturedHtml` beside `InputText`, logged in the
ordinary `spellcheck_detail` blob as `clipboard_html` with `clipboard_html_chars` and
`clipboard_html_truncated`. No sidecar files, no separate corpus, no parallel code path. The field is
capped at 512K chars so one pathological selection cannot produce a multi-megabyte log line;
`clipboard_html_chars` always reports the true size.

Filter with `logs.py --has-html`. The formatted view prints only the size; `--json` yields the markup.

This inflates the daily JSONL — a styled email fragment is tens of KB against ~2 KB for the rest of
the row. That is accepted deliberately: the fine-tune tooling selects fields, so a bigger file costs
disk and nothing else, and one log corpus beats two.

Let it accumulate through normal use. Real Gmail, Slack, Docs, and Notion selections are worth far
more than synthetic fixtures.

### The comparison, once there is a corpus

For each captured `(CF_HTML, CF_UNICODETEXT, corrected_output)` triple, run every candidate
representation through several models and score them:

| Axis | What to measure |
|---|---|
| Correction quality | Are the same typos fixed as the current plain-text baseline? Any regressions? |
| Markup fidelity | Does the returned structure still parse, and does it match the input tag-for-tag? |
| Invention | Tags, attributes, or styles the model added that were not in the input |
| Loss | Formatting present in the source and absent from the output |
| Tokens | Input and output, against the plain-text baseline for the same selection |
| Latency | Wall-clock, against the same baseline |

Representations to test: raw HTML (A), plain text (today's baseline), Markdown (D), and the
Markdown-in / run-splice-back hybrid. Models to test: gpt-4.1 (current production), plus a
frontier-tier and a small-fast-tier model — pick the exact set when the corpus is ready, from live
model listings rather than memory.

The `tests/` Python tooling and `bench/` already know how to run batches against the API; this
harness belongs beside them, not in `src/`.

### Success bar

A representation only beats the current plain-text baseline if it fixes the same typos, invents
nothing, and stays inside the latency contract. Better formatting preservation does not buy the right
to be slower or less accurate — speed is the product.

---

## Design

### Pipeline

`[DONE]` is shipped. `[NEW]` is not built yet.

```text
hotkey
  -> capture target identity
  -> back up clipboard
  -> Ctrl+C
  -> read CF_UNICODETEXT *and* CF_HTML in the same clipboard window   [DONE]
  -> exclude captured text from clipboard history
  -> parse CF_HTML into a run list                                    [NEW]
  -> render the run list to Markdown                                  [NEW]
  -> resolve + freeze formatting rule, AfterCopy hook
  -> protect literals
  -> AI request                          (Markdown payload)
  -> replacements / prompt guard / literal restoration
  -> align corrected Markdown onto the run list                       [NEW]
  -> re-serialize the run list to HTML                                [NEW]
  -> recapture + validate destination identity
  -> BeforePaste hook
  -> write CF_HTML + CF_UNICODETEXT together                          [NEW]
  -> settle delay
  -> final destination validation
  -> Ctrl+V
  -> async telemetry
```

Every `[NEW]` step is skippable. If any of them declines, the run falls through to today's exact
plain-text behavior. That is the core safety property: **rich-text is an enhancement layer, never a
precondition.**

### Run extraction

Parse the `CF_HTML` fragment into an ordered list of text runs:

```csharp
internal readonly record struct TextRun(
    int SourceStart,      // offset into the CF_HTML fragment
    int SourceLength,
    int PlainStart,       // offset into the reconstructed plain text
    int PlainLength,
    int MarkdownStart,    // offset into the rendered Markdown
    int MarkdownLength);
```

The parser must reproduce the browser's own plain-text serialization exactly (the table above is the
specification). The reconstructed plain text is then compared against the real `CF_UNICODETEXT`.

**If they do not match character for character, abort rich-text and run the plain-text path.** This
is the same self-verifying gate the abandoned prototype used, and it is what makes the feature safe
to ship: markup is only ever rewritten when we can prove we understand how the browser flattened it.

### Markdown rendering

Render the run list to Markdown, recording each run's span in the output. Markdown syntax the
renderer emits — `**`, `_`, `#`, `-`, `> `, `[`/`](url)`, table pipes — belongs to no run. That is the
point: syntax characters are structure, not content, and the alignment step must be able to tell the
difference.

Keep the rendering conservative. Anything with no faithful Markdown form (a colored span, a nested
table, an inline style) renders as its plain text and stays a run — its markup is preserved by the
splice regardless, because the splice never touches tags.

### Alignment

Inputs: the Markdown sent to the model, the corrected Markdown returned, and the run list.

1. Word-level diff (Myers or a bounded LCS) between sent and returned Markdown.
2. Map each edit's original offset range onto the run(s) that own it.
3. An edit wholly inside one run rewrites that run's text.
4. An edit that touches only syntax characters is **dropped**, not applied. If the model turned `**`
   into `*` or renumbered a list, that is the model editing structure it was not asked to edit.
5. An edit spanning a run boundary collapses into the first run it touches; later runs lose the
   overlapped span. This is the only lossy case and it is rare (corrections almost never straddle a
   bold/plain boundary).
6. A pure insertion at a boundary attaches to the preceding run.

Bounds: if the diff exceeds a fixed edit budget, if the correction differs in length from the original
by more than a fixed ratio, or if the returned Markdown's structure (heading levels, list item count,
link count) does not match what was sent, abort to plain text. A wholesale rewrite is not a spell
check and should not be re-flowed into someone's document.

### Re-serialization

Rebuild the fragment by splicing corrected run text back into the original markup at
`SourceStart`/`SourceLength`, leaving every tag, attribute, and inline style byte-identical. Re-encode
`&`, `<`, `>`, and NBSP on the way in. Do not pretty-print, minify, normalize, or reorder anything.

Then emit a valid `CF_HTML` payload with a correctly recomputed header — `StartHTML`, `EndHTML`,
`StartFragment`, `EndFragment` are byte offsets into the UTF-8 payload and are the single most common
source of "paste produces garbage" bugs.

### Clipboard write

`ExcludeTextFromHistory` already owns a manual `OpenClipboard`/`EmptyClipboard`/`SetClipboardData`
session. The final write becomes the same shape: one session placing `CF_HTML`, `CF_UNICODETEXT`, and
no history-exclusion DWORDs (the corrected text is meant to stay in Win+V — see root `CLAUDE.md`).
`Clipboard.SetText` is no longer sufficient.

### Protected literals

Unchanged, and deliberately so. Protection and restoration continue to operate on plain text before
and after the request. Alignment happens strictly after literal restoration, so a URL that survived
the AI also survives the re-serialization — it is simply a run whose text did not change.

---

## Reusable Pieces

From the abandoned prototype, kept at
`scratchpad/HtmlParagraphReconstructor.reference.cs` for this session and worth re-deriving into the
real implementation:

- `CF_HTML` header stripping and `<!--StartFragment-->` extraction.
- A tag scanner that finds the closing `>` without being fooled by `>` inside a quoted attribute
  value. Inline style strings hit this immediately.
- Whitespace collapsing for `white-space: normal`, with NBSP explicitly non-collapsible. The captured
  Gmail text contains real ` ` characters and they must survive byte-for-byte.
- HTML entity decoding, named and numeric.
- The block-element set and the `</p>` double-newline rule.
- Inline `margin` / `margin-bottom` / `margin-block-end` parsing including all four shorthand arities.
  Chrome inlines computed styles, so no stylesheet resolution is needed.

---

## Latency Contract

Speed is the product. The gates:

- A selection with no `CF_HTML` pays one extra clipboard read and nothing else.
- A selection with `CF_HTML` pays parse + diff + serialize. Target: under 5 ms combined at p95 for a
  typical email-length selection; anything above that needs justification or a size cap.
- No filesystem, registry, network, or browser access is added to the hot path.
- Parse and serialize timings are logged as their own fields so they cannot hide inside
  `request_ms` or `paste_ms`.
- The existing E2E benchmark must show no ordinary-target regression. Per `docs/bench.md`, anything
  under 5% is noise; a repeatable 5%+ regression blocks the feature.

---

## Telemetry

New `spellcheck_detail.timings` fields: `html_parse_ms`, `align_ms`, `html_serialize_ms`.

New `rich_text` object: whether `CF_HTML` was present, whether the serialization model verified,
run count, edits applied, edits dropped at run boundaries, abort reason, and whether both flavors
reached the clipboard. Abort reasons are a closed set (`no_html`, `too_large`, `model_mismatch`,
`edit_budget_exceeded`, `serialize_failed`, `clipboard_write_failed`). No captured text, no markup,
no URLs.

---

## Verification

Automated:

- Serialization-model fixtures for all four rows of the measured Chrome table, plus lists, headings,
  nested inline spans, tables, and `<pre>`.
- Round-trip identity: a selection the AI returns unchanged must re-serialize byte-for-byte.
- Alignment fixtures: single-word fix, multi-word fix, insertion, deletion, correction inside a bold
  run, correction straddling a run boundary, correction inside a link's anchor text.
- Protected literal survival: URL, Windows path, API key, UUID, opaque ID.
- Malformed, truncated, and oversized `CF_HTML`.
- `CF_HTML` header offset correctness under multi-byte UTF-8 content.
- Fallback: every abort reason produces exactly today's plain-text output.

Manual Dev acceptance:

- The original Tim email in Gmail, margin-styled paragraphs, spacing intact after correction.
- A selection containing bold, a link, and colored text.
- A bulleted list.
- Notepad, Windows Terminal, and VS Code — confirm no visible markup and no behavior change (D3).
- Paste into Gmail, then change the message font, and check whether the corrected span follows (D2).
- An unmatched Chrome textarea, unchanged.

Gate commands are the existing ones from `.planning/app-site-formatting-customizations.md`
§ Verification gate, plus the new test project.

---

## Sequencing

1. **Done (2026-07-20).** Dual-flavor capture, logged in the ordinary `spellcheck_detail` blob.
   Behavior unchanged. Shipped to Prod so the corpus comes from real daily use.
2. Run the comparison in § Model Comparison against the collected corpus. Confirm or overturn the
   Markdown choice and pick the model.
3. Run extraction + the serialization verification gate, logging only. Confirm `model_mismatch` is
   rare against real markup before anything depends on it. This is the honest place to find out the
   parser is wrong, and it costs nothing if it is.
4. Markdown rendering + alignment + re-serialization, still not writing `CF_HTML` to the clipboard.
   Log the diff between what would be pasted and what is pasted.
5. Dual-flavor clipboard write behind a setting, default off. Answer D2 and D3 here.
6. Default on. Update `docs/architecture.md`, `docs/replacements-and-logging.md`,
   `docs/watchlist.md`, `src/CLAUDE.md`, and the root routing table.
