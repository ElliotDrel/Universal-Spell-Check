# Plan / Spec: Rich-Text Clipboard Pipeline

## Status

### Native-only retest isolates destination list rebasing (2026-10-11 11:29 Eastern)

The partial-start isolation above is now rendered and still fails. Run
`a057dd031b044e549c306d7a70a9bcb7` starts at the selected tail of a nested item,
then crosses the top-level Reflect item and its children. The 600ms after-image
shows Reflect changing from `2.` to `d.`, with its children changing from alpha
level 1 to roman level 2. The final selected paragraph joins the roman level,
so the unselected questions following it become siblings instead of children.
Source/request/readback retain list levels 1,0,1,1,1 and indentation 72,36,72,72,72.
The replacement offers no HTML. The observed shift therefore occurs in Docs'
contextual insertion, not HTML import or native offset mapping.

Control `acb0c3b9f60648be8c51bcc0862a634e`, starting at the top-level Reflect
item, preserves visible levels. Single-paragraph control
`d8fc4b774db94aa393d2ebcbc8e6f0e9` corrects text without moving its paragraph.
These distinguish a cross-paragraph insertion at a nested boundary from the
single-paragraph and top-level-start paths. Native-only is not a root repair
for this issue. Absolute serialized levels are not sufficient acceptance.

Elliot confirmed original Docs Ctrl+C/Ctrl+V over the same selection also
shifts the list. The explicitly authorized clipboard read preserved that copy
locally: the native slice retains levels 1,0,1,1,1 and its original internal
copy ID. This establishes ordinary structural-paste rebasing, not an external
clipboard identity defect. The repair must keep the document's delimiters.

A guarded paragraph-text replacement draft is implemented but not connected
to the coordinator. The pure planner verifies one source block per paragraph
and generates only inline HTML, no list/heading/paragraph wrappers or breaks.
Every paragraph is planned, including unchanged text. Empty paragraphs and
soft breaks decline this initial range contract. The unused executor proposes
reverse text selections, requires a fresh clipboard copy and exact one-block
content before each paste, then recopies/verifies each replacement. It verifies
the entire corrected selection and native paragraph/list layout afterward.
Navigation mismatches stop explicitly; there is no blind whole-selection retry.
Native layout is checked before navigation; issued and verified paste counts
are separate. These edits may form multiple undo operations.

The formatting suite passes, including boundary/whitespace/structural rejection,
inline bold/link preservation, and layout comparison catching the observed
rebase, omitted final paragraphs and missing style data. Both real replays
produce complete inline-only plans: five paragraphs (two unchanged) and eleven
paragraphs (ten unchanged). No editor rendering or keyboard contract has been
claimed from those checks. Automatic approval review rejected live coordinator
integration due to unverified caret positioning/unselected-text risk. Explicit
user approval to enable a Dev test is pending. Current running Dev retains
the previous native-only behavior. No clipboard write or keyboard input was
performed by the agent; only Elliot's explicitly authorized clipboard read.

### Partial-start selection isolation (2026-10-11, editor test pending)

Run `e742b24ff24942c5bbb97db612fd454c` starts inside a nested list paragraph,
then crosses a top-level item and nested items. Native source/request/readback
retain the original indentation, list identities and nesting at every delimiter
and the final trailing paragraph. This establishes transport fidelity, not
correct insertion. Its 150ms after image still shows the original selection
and does not establish the reported rendered shift.

Previously supported slices offered both native and HTML. The first HTML block
represents the selected sentence tail as a complete nested list item. To isolate
that competing import path, verified native replacements now offer only native
and Unicode; HTML remains the fallback when native validation declines. No
native style or selection-boundary metadata is guessed. Changed and unchanged
partial-start regressions retain nesting and indentation, with no HTML or added
terminal separator. The diagnostic after-image delay is now 600ms, off the
correction path. The same partial-start selection needs an original-editor
retest to distinguish HTML import from native insertion behavior; this change
is not yet a verified root-cause fix.

### One Docs paragraph model; headings no longer block native (2026-10-11, reviewed; editor retest pending)

Sanitized failure: an `h2` heading followed by a four-item `ul`, copied
Unicode heading + items separated by CRLF plus one terminal separator. The
Docs mapper counted only `p` blocks, saw four paragraphs instead of five and
returned `model_mismatch`; the native slice, which carries heading level/ID
and list styles, was never attempted because it was gated on HTML success.

Root cause is that HTML block serialization owned both the paragraph
boundaries and the native gate. Now one selection model owns them. Verified
HTML serialization distinguishes an intentional final blank from a copy-only
separator; copied Unicode supplies the model for unfamiliar HTML shapes.
The correction is normalized against that model once; changed paragraph counts and filled
blank paragraphs decline. CF_HTML (serializer now treats `p` and `h1`-`h6` as
blocks) and native are verified against that model independently. A verified
native slice is emitted with Unicode even when the HTML shape is not
understood (`docs_native`, empty `paste_html`); the clipboard writer now
supports that combination. A single whole heading block is unwrapped to its
inline spans, like a single list paragraph. The native copy-only LF rule from
the entry below is unchanged; it now receives the common selection target.

Behavior changes beyond headings: inline selections also keep source edge
whitespace; single-paragraph model line breaks are collapsed instead of
declining; the plain fallback after a verified paragraph model pastes the
normalized text (no model terminal newline). Primary review found and repaired
the initial proposal's unconditional separator trimming: an intentional final
blank now survives whether copied Unicode includes an additional separator or
matches HTML serialization exactly. The added regression failed before repair.

Tested (mapper only, no clipboard): changed and unchanged heading + list HTML
and native (heading ID on remapped delimiter, list nesting, link/bold/font
transitions, final-item trailing styles, no terminal LF), mixed paragraph /
multi-node heading / blank / list, structural rejections, whole-heading inline
paste, native-only with an unmodeled HTML block, final intentional blank with
and without a copy-only separator, heading inside a list, an in-paragraph br,
and all prior regressions.
Primary verification: the formatting suite and Dev build pass, and six real native replays
(latest heading plus four single/multiline captures and the changed six-paragraph
variant) emit matching native/Unicode text without a final separator. Native
style marker values, heading properties, list entities and other source metadata
remain unchanged; only offsets and the removed delimiter's trailing styles move.
Additional native regressions distinguish the same terminal LF used as a single
heading's copy separator versus a real break before an intentional final blank.

Rendering remains unverified: the original heading selection and the new
native-without-HTML fallback still require Docs acceptance. Unsupported positional
embedded objects and suggestions retain the established HTML/text fallback.
This is a shared mechanism for text selections, not a claim of complete support
for every Docs object type.

### Native terminal separator assumption superseded (2026-10-11 09:38 Eastern)

Run `07cdf3a31dbb4e3c8e67f347f34c0049` proves the native copy can also
contain a copy-only final LF. Before-image selection contains one item; the
requested Unicode/HTML contain no newline, but native requested/readback data
contains the LF and the after-image shows an empty `c.` item. The previous
native repair deliberately restored that LF. Its test incorrectly called it
a real selected break; that assumption and test are superseded.

The native rewrite now follows the mapper's verified HTML paragraph count and
removes exactly one excess terminal LF. The removed delimiter's paragraph/list
styles move to `stsl_trailing`, matching captured partial native slices, so
indentation survives without inserting an empty item. Internal breaks and final
breaks retained by verified HTML remain. Changed and unchanged text both paste.
No new editor integration, skip behavior, or logging layer is added.
Verification: the full formatting suite and Dev build pass. Five native
replays (latest single item, two earlier single items, six-paragraph unchanged
text and its changed-word variant) emit native text identical to replacement
Unicode with no final LF. Regressions cover changed/unchanged single and multiple
items, final-item indentation/nesting, UTF-16 offsets, and intentional breaks.
Original-editor retest remains required; replay verifies the emitted payload.

### Native Docs slice repair; unchanged-text skip removed (2026-10-11)

Elliot explicitly rejected skipping unchanged text. Commit `24007d2` reverts
that entire behavior; all corrections still write and paste the replacement.
The six-paragraph native capture from `0ac375c0fb364a0b8e7e18ab2d996168`
contains five internal paragraph breaks and no final newline. The last item's
paragraph/list styles live in `stsl_trailing`, while Unicode includes a final
copy-only CRLF and HTML closes a whole list. The HTML-only paste created an
empty top-level item. The native slice carries the selection boundary HTML lost.

`GoogleDocsClipboard` reads Chromium's bounded Pickle string map, validates the
slice text, and remaps non-null sparse style offsets through the existing
bounded alignment diff. Leading/trailing styles, list identities, and inline style transitions survive.
The final-break assumption here is superseded by the captured retest above.
Unchanged text also emits a native replacement. Original internal clip IDs are
removed so edited content cannot retrieve the original cached slice. Native
suggestions and position-bearing embedded content are declined explicitly,
retaining the established corrected HTML/text fallback. No native schema is
invented: original objects are retained and only text/style offsets change.

The Chromium container matches its [custom-data implementation](https://chromium.googlesource.com/chromium/src/+/refs/tags/129.0.6631.1/ui/base/clipboard/custom_data_helper.cc): entry count and aligned UTF-16 key/value pairs.

The writer emits the custom native bytes beside Unicode/HTML and immediately
verifies exact binary readback. Developer logging retains full source,
requested and readback native sidecars; disabled logging uses native bytes
only in memory. Mapper replay includes native source/output and the exporter
hydrates it with integrity checks. Source-editor acceptance remains separate.

Verification: native formatting regressions (including changed/unchanged text,
style positions, nested list IDs, partial final paragraphs, a terminal-break fixture
subsequently superseded above, supplementary Unicode and corrupt/mismatched source rejection),
three integrity-checked native incident replays, and a changed-word variant of
the six-paragraph capture pass. Changed and unchanged six-paragraph outputs both
have exactly five internal breaks and no final break. Fourteen log-reader tests,
exporter-to-native-replay integration, Release/Dev builds and isolated developer
evidence tests pass. No live clipboard/keyboard test was run while Elliot was
active. Browser setup failed before connecting (recorded in tooling gaps), so
original Docs rendering remains the acceptance gate.

### Retest rejects the block-only diagnosis (2026-10-11 08:50 Eastern)

Run `e504e1b96e784e94a481ffd44cf485c3` used the rebuilt inline-only Dev code.
Requested/readback Unicode and HTML are corrected and newline-free, with no
list/paragraph tags. Nevertheless the after image shows the original wording
("goals to I need", rather than corrected "goals I need") and a new empty list
paragraph. Removing block wrappers alone did not fix destination behavior; it
cannot be cited as acceptance or the complete root cause.

The remaining `id="docs-internal-guid-..."` matches the captured native internal
clip ID and identifies the original Docs slice, whose text contains the typo
and final newline. The unchanged wording despite edited HTML supports Docs
using its cached original slice. Generated Docs HTML now removes that original
copy ID after producer recognition. It preserves unrelated IDs, formatting,
links, and multi-paragraph/list structure. No native slice schema is invented
or mutated. Tests verify this for inline, single-paragraph and multi-level
selections; source-editor acceptance still requires a live retest.

### Single-paragraph Docs extra line (2026-10-11 08:40 Eastern)

Run `91cfde99502f446cb9d060a8a89b61cd` selected one level-three list item's
text. AI output and requested/readback Unicode contain no terminal newline;
HTML readback exactly matches the requested payload. Before/after images show
a new blank, unnumbered line after the corrected item. The source copied the
text as a whole `ol/li/p` block despite the inline selection; the first hypothesis was that reimporting that
block introduced a destination paragraph boundary. The 08:50 retest above
rejects this as the complete explanation.

For a verified single-paragraph Docs selection with no internal line break,
remove only its `ol/ul/li/p` wrappers before alignment. Keep the producer and
inline styles, bold, links, and original edge whitespace. This leaves the
existing destination paragraph/list item in charge of layout. Multiple
paragraphs and intentional blank paragraphs retain their structure. Native
regressions and incident replay verify the emitted fragment has no block end;
original-editor retest is the remaining rendering acceptance check. This does
not establish acceptance for multi-level selections.

### Developer logging native Docs capture (2026-10-11)

Supersedes the approval-pending diagnostic proposal below. Elliot explicitly
approved capture/storage and then asked to make it part of normal Developer
logging, including the Production logging setting after release. Enabled
corrections now capture bounded Chromium custom MIME bytes for Docs-marked
selections before history exclusion. The 1 MiB binary cap, sequence validation,
explicit read status, 512K base64 inline cap and full integrity-checked evidence
sidecar keep missing/truncated inputs visible. Superseded by the native-paste repair above: disabled logging now reads native
bytes in memory for Docs, but never persists them. No separate diagnostic flag or new UI was added.

Release/Dev builds, formatting regressions, and synthetic developer-evidence
roundtrip/hash/failure-status tests pass. The enabled Dev correction `91cfde99502f446cb9d060a8a89b61cd` captured
26,320 bytes of Chromium metadata successfully with a complete base64 sidecar.
It contains the native Docs document slice and internal clip ID. Multi-level
list paste acceptance remains unresolved.

### Docs list paste acceptance failed (2026-10-11 08:27 Eastern)

The earlier producer-marked replay results below establish mapper preservation,
not Google Docs list-level acceptance. Dev run `5d06f010e9254dadb555e00243c8dbfb`
preserved the captured list tags/styles/aria-level attributes and had exact HTML
readback, but the user's screenshots show a destination list-level shift:
selected level-2 alpha items become level-3 roman items, their children become
level-4 decimal items, and a later level-1 heading becomes a level-2 item.
The previous HTML-only repair is incomplete. Do not treat another exact mapper
replay as proof of a list paste fix.

At that retest, the source offered `Chromium Web Custom MIME Data Format`,
but the app listed without serializing it. The subsequently approved logging
repair above captures this format; the 08:40 run confirms it contains a native
Docs slice and list entities. Its necessity for multi-level paste remains
unverified.
No constant indent subtraction or invented native payload is justified.

Proposed next diagnostic: for one explicitly approved, developer-logging-enabled
Docs retest, capture at most 1 MiB of that original custom format before clipboard
history exclusion, with explicit absent/read-failed/size-limit statuses. Persist
base64 in the existing local evidence with a bounded inline log preview. This
may contain identifiers and document data beyond the visible selected text.
Automatic approval review originally rejected capture without explicit approval.
Elliot then explicitly approved it; the normal Developer logging implementation
above supersedes this proposal and the earlier approval-pending state.

### Google Docs selections and inline references (2026-10-11)

Audited 171 Google Docs runs from 2026-07-07 through 2026-10-11 08:13 Eastern.
All 58 records with a rich-text decision rejected Docs HTML as `unsupported_editor`;
older records do not establish visible formatting acceptance. The 106 captures
with the `docs-internal-guid-` producer marker now replay as `aligned_html`.
The remaining historical records lack that marker or HTML and are outside this
producer-specific reconstruction path.

Prod 0.12.1 runs `7c527bb164f947998af1874846643e69`,
`28ff855359804a48900ac60b298dadcc`, and `9ac4df84343844ceb2e6624a0d957947`
captured nested alpha/roman lists but requested plain text. Before/after images
show an extra empty list item after the first paste. The mapper now verifies Docs'
complete Unicode serialization, maps edits into its original text nodes, retains
list levels/styles, inline fonts, emphasis, links, and NBSP, and excludes the
copy-only final paragraph separator from replacement Unicode. A historical
2026-09-02 paragraph selection additionally carries a real inter-paragraph `<br>`
and a terminal `Apple-interchange-newline`; the real break is retained and the
copy-only interchange marker is excluded. Inline heading run
`151ceb8188f84ed4a780f18668edbc74` requested `Mentalities` without a newline;
it now retains the original inline 16pt span rather than manufacturing a block.
Its screenshots alone do not prove a newly inserted terminal blank paragraph.

Skill run `efb5afb6bd3f42d4aa3dc1c99eaa4b5d` and document-reference run
`246090d0545644d89cbaad40540df19f` copied one ProseMirror paragraph. The model
inserted a Markdown hard break beside the chip; the mapper rejected that inserted
newline and fell back to plain text, losing the chip's identity. For captured
skill/file-reference selections whose source Unicode has no line breaks, remove
only model-added line breaks before alignment. Preserve the original skill/file
identity and link attributes. Multi-paragraph selections retain their existing
breaks. This repairs clipboard reconstruction; destination deserialization still
requires a real-editor retest.

Verification: pinned SDK 10.0.401, Release formatting regressions, Dev build,
106 historical producer-marked replays, and five full-evidence incident replays.
No live user clipboard/keyboard test was run: the existing native smoke uses the
interactive desktop and is not isolated from ongoing user activity. Original
Google Docs and reference-chip paste acceptance remains pending.

### LinkedIn comment character encoding (2026-10-10)

Prod 0.10.1 runs `e4b40ced28a34fdd830443e0610b2fd9` and
`70bacc2ed81b4a1bb94c3c34f68ca039` returned normal apostrophes from the AI.
The mapper emitted `&#39;` and `&#160;`; exact clipboard readback confirmed those
payloads, but the destination captures showed `#39;` and `#160;` as visible text.
The later run used a literal curly apostrophe and avoided that corruption.
Text-node encoding now escapes only `&`, `<`, and `>` and retains apostrophes,
NBSP, accents and emoji as literal Unicode, with UTF-8 CF_HTML byte offsets.
This applies to every reconstruction path; tags and attributes are unchanged.
Verification: the Release formatting suite passes, Dev builds, and both full-evidence
incident replays emit literal apostrophes (and literal NBSP for the tagged run) with
no `&#39;` or `&#160;`. Validation used installed SDK 10.0.204; the repository pin
remains 10.0.401. The native developer-evidence smoke stopped at its pre-paste
RTF-source assertion (`Rich editor source must expose RTF.`), so it does not
establish paste acceptance. Elliot confirmed the LinkedIn retest has no weird
characters. Dev runs `861cbe4716cb4c32b97242e45f2ba885` and
`73053c4bddda41589fcf3172902d78cc` (2026-10-10 20:10 Eastern) succeeded
with `mode=html`, exact 239-character HTML readback, and no paste errors.
Those samples contain no apostrophe; the original incident replays verify the
encoding change, and Elliot provides the visible destination acceptance.

### Future request: preserve tags in place (deferred 2026-10-10)

Elliot wants to select an entire LinkedIn comment and correct the text around tags
while leaving the original mention nodes untouched, including multiple tags in the
middle of a comment. This is explicitly out of the current encoding fix's scope.
The captured mention HTML retained its display name but contained no profile
identifier; re-pasting that markup flattened the active tag into plain text.
A future implementation must replace only the editable text segments, preserve
mention identity and order, and never replace or recreate the tag nodes.
Investigate reliable selection/edit support before choosing keyboard automation,
Windows accessibility ranges, or browser integration; no mechanism is verified yet.
Verify multiple/repeated names, adjacent tags, partial selections, punctuation,
Unicode, selection changes during requests, and a safe failure that leaves tags intact.
Acceptance requires the real LinkedIn editor, with no test comment posted.

**2026-10-10 LinkedIn:** The captured messaging incident showed one empty HTML paragraph
copied as four CRLF sequences. The plain fallback pasted three LF characters and the
before/after screenshots confirmed an added blank line. A narrow producer-URL-gated
path now preserves the original simple paragraph markup and empty blocks, verifies the
copied source serialization, and requires the same corrected nonempty section count.
The captured incident replays as `html` with three original paragraph blocks and
one empty block. Release formatting regressions and the Dev build pass; the rebuilt
native clipboard/evidence smoke exits 0. The original editor retest remains the
rendering acceptance gate.

**Original-editor acceptance:** Dev run `7dc89838439d4406a53fa316975e7cea`
(2026-10-10 13:04 Eastern, PID 17952) pasted into the same LinkedIn messaging editor
with `mode=html`, three original paragraphs, exact 3843-character CF_HTML readback,
and the intended two Unicode line breaks. Elliot confirmed the spacing works.
Developer evidence was disabled for this retest; visible acceptance is his confirmation.

**2026-10-09:** Captured ChatGPT skill selections exposed two alignment boundaries: final punctuation inserted after removal of a copied trailing newline had no HTML owner, and long selections exceeded the diff matrix budget even when much of the text was unchanged. The mapper now anchors that final insertion at the deleted whitespace suffix and excludes identical selection edges from the bounded diff. Original skill spans, attributes, list structure, and empty paragraphs remain intact when alignment succeeds. Changed paragraph structure still falls back. Mapper regression and original-editor acceptance are separate checks.

Verification: both captured selections replay as `aligned_html` with unchanged source tags/attributes and exact corrected Unicode. The native formatting suite passes, Dev builds, and the disposable Dev clipboard/evidence smoke exits 0. The original ChatGPT retest succeeded after the follow-up fix below.

**2026-10-09 follow-up:** New full-selection captures revealed an older recovery defect shared by Prod and Dev: lookahead matched a later standalone space or typo before the current node and falsely treated already-captured text as omitted. Plain fallback then duplicated those original misspelled nodes. Lookahead now requires the later match to contain the current candidate. Removed the rule that reclassified model-deleted nodes as missing source text, which could restore deleted typo characters. Both new incident replays retain exact corrected Unicode and rich markup; regression cases cover split skill spans, standalone whitespace, deleted typo nodes, fallback without duplication, and genuine omitted headings whose text repeats later.

Verification: all four captured incidents replay with rich HTML; both duplication incidents paste exactly the 1115-character corrected text, with unchanged source tags/attributes. The formatting suite and rebuilt Dev native clipboard/evidence smoke pass. The original ChatGPT retest at 2026-10-09 22:13 Eastern (run `fa7e93f721b34e858e1beb048a7632c4`) succeeded: `aligned_html`, exact HTML readback, and paste text identical to all 1115 corrected characters. Elliot confirmed the visible result worked and authorized publication.

**2026-09-28:** Mixed ChatGPT spans, links, line breaks, and lists now have a bounded text-node alignment path. It maps each source HTML text node to the copied Unicode text, applies only edits owned by those nodes, and emits corrected HTML with original tags intact. Unmappable edits and any reconstruction exception fall back to corrected Unicode paste. The temporary refusal to paste unsupported ChatGPT formatting was removed: spell-check completion takes priority over formatting fidelity. An optional before-paste formatting failure also falls back to the corrected text.

**2026-09-28 follow-up:** A real Dev run proved that ChatGPT can omit a visible selected heading from `CF_UNICODETEXT` while retaining it in `CF_HTML`. The alignment path now keeps unmatched HTML text nodes and reinserts their text in the Unicode replacement, even on structural fallback. Markdown links are protected as one literal before the model request so their closing punctuation cannot be duplicated.

**Stage 1 implemented 2026-07-20: dual-flavor capture.** Every run now reads both `CF_UNICODETEXT`
and `CF_HTML` and logs both. The capture path is permanently the two-flavor shape the rest of this
spec builds on.

**Narrow production fix implemented 2026-08-16: ChatGPT simple-paragraph re-emission.**
`RichTextClipboard` accepts only `data-pm-slice` fragments made exclusively of simple `<p>` text
nodes. It verifies that the non-empty source paragraphs exactly equal the copied Unicode sections and
that the corrected output keeps their count, then changes only those text nodes and writes `CF_HTML`
plus Unicode text. This prevents ChatGPT's copied four-newline representation of an empty paragraph
from becoming extra pasted lines. It also accepts one ordered-list item when ProseMirror adds its
generated `1. ` marker to Unicode text but keeps only the item body in HTML. It accepts a single
inline `<span>` wrapper, strips the synthetic list marker from the corrected text, and pastes only the
body as Unicode text. ChatGPT treats both the copied list slice and a neutral HTML paragraph as new
blocks, so any HTML flavor creates a subbullet when pasted over selected text inside an existing item.
It is intentionally not the full Markdown/alignment pipeline: nested markup, malformed offsets,
changed structure, and every other producer retain the exact plain-text fallback.

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
| **A. Raw HTML in, corrected HTML out** | Full markup | Highly variable — see the note below. | Model rewrites, drops, or invents tags. Prompt-leak guard and protected literals now have to survive markup. High. |
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

### First findings from the corpus (2026-07-20)

Four runs on v0.8.0 before the first review. Small sample, but two results already matter:

**Slack offers no `CF_HTML` at all.** Three Slack runs, all `clipboard_html_chars=0`, including one
selection containing a code span and an `@mention`. Both arrived as plain text. If this holds, the
rich-text pipeline cannot help Slack — the formatting is destroyed by the source app before the
clipboard, and no amount of reconstruction recovers it. Worth confirming against
`clipboard_formats` now that it is captured; if Slack offers RTF instead, the picture changes.

**Akiflow encodes every space as `&nbsp;`** while its plain-text flavor uses ordinary spaces:

```text
CF_HTML:          Meet&nbsp;at&nbsp;Middletown&nbsp;Plannet&nbsp;Fitness
CF_UNICODETEXT:   Meet at Middletown Plannet Fitness
```

This breaks the serialization verification gate as specified. Decoding `&nbsp;` to ` ` and
comparing byte-for-byte against the plain text yields `model_mismatch` on every Akiflow run, which
would silently disable the pipeline there. **The gate must treat NBSP and ordinary space as
equivalent when comparing**, while still preserving whichever character the source used when
splicing. This is exactly the kind of thing the collection pass exists to find.

### Second pass, with `clipboard_formats` (2026-07-20 Dev)

Seven runs across Gmail, Slack, ChatGPT, and Windows Terminal. `clipboard_ms` ranged 226–272 ms
against a 251–260 ms pre-change baseline — reading three flavors instead of one costs nothing
measurable.

**The bug reproduced in real captured data.** A Gmail reading-pane selection:

```html
<h1 style="... margin: 0px 0px 12px; ...">Your channel with fathom is ready</h1><div ...>
```

```text
CF_UNICODETEXT: Your channel with fathom is ready\r\n#fathom-ios-beta was joined by ...
```

One `\r\n`. The 12 px gap under the heading exists only as CSS margin and is unrecoverable from the
plain-text flavor. This is the fourth row of the measured Chrome table, observed in the wild rather
than on a synthetic page.

**Gmail compose itself is fine.** A re-typed version of the original Tim email uses `<br><br>`
between paragraphs, which Chrome serializes as `\r\n\r\n`. The gap survives. So the draft that
started this carried *pasted* margin-styled blocks, not Gmail's native structure — consistent with
the original diagnosis. The pipeline is needed for pasted-in and rendered content, not for text typed
directly into a compose box.

**Slack confirmed: no HTML on the clipboard.** The format list settles it — Slack offers
`UnicodeText,Chromium internal source RFH token,Chromium Web Custom MIME Data Format,Chromium internal source URL,Locale,Text,OEMText`
and no `HTML Format`. Not a failed read on our side. Slack's own rich representation presumably lives
in `Chromium Web Custom MIME Data Format`, but that is a Chromium-internal blob and the sibling
`Chromium internal source URL` already proved to read back empty from another process, so assume the
same until measured. **Slack is out of scope for the rich-text pipeline.**

**Sources are inconsistent within one app.** ChatGPT produced 244 chars of `<p data-pm-slice>` on one
run and no HTML at all on another. Whatever the pipeline does, it must degrade to the plain-text path
per run, not per app.

**Still no RTF sample.** No captured selection has offered it. The capture stays in — Word and
desktop Outlook are the expected sources and neither has been exercised — but nothing yet depends
on it.

**Privacy note:** `clipboard_html` from an email carries remote asset URLs, including tracking pixels
(`ci3.googleusercontent.com/...`). The logs already hold full message text, so this is not a new
category of content, but it is the first time remote URLs land in the corpus.

**Real editor copies are far leaner than the synthetic test suggested.** The option-A cost estimate
originally claimed ~2 KB of inlined CSS per paragraph. That number came from copying a *rendered
styled page*, where Chrome inlines every computed property. Copying from an editor gives almost
nothing:

```html
<!-- ChatGPT (ProseMirror), 244 chars including the CF_HTML header -->
<p data-pm-slice="0 0 []">output the following as a md docuemnt</p>
```

So raw-HTML cost depends entirely on where the selection came from, and the corpus has to answer it
per-source rather than in general. Gmail compose is the case that matters and is still unmeasured —
**no Gmail run has been captured on v0.8.0 yet**, so the bug that started this work has not been
observed through the new instrumentation.

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
  -> recapture current destination context
  -> BeforePaste hook
  -> write CF_HTML + CF_UNICODETEXT together                          [NEW]
  -> settle delay
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
no history-exclusion DWORDs (the corrected text is meant to stay in Win+V — see root `AGENTS.md`).
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
   `docs/watchlist.md`, `src/AGENTS.md`, and the root routing table.
