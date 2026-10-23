# Settlement wait investigation — October 23, 2026

The 50-ms minimum remains. The candidate removes avoidable native-wait overshoot;
it does not eliminate clipboard settlement or change models, transports, paste
delivery, capture gates, formatting, or diagnostic policy.

## Cause and change

A wait-only ctypes probe calls the same Win32 wait and message-pump APIs, without
clipboard access, focus/input, network, or timer-resolution changes. Two 50-sample
blocks of the old timeout had medians 62.780/62.600 ms. An ordinary waitable timer
had median 62.908 ms. Two high-resolution timer blocks measured 50.256/50.368 ms.
All 260 attempts, including ten warmups, remained above 50 ms. The measured extra
time occurs inside the native wait/wakeup, rather than clipboard or editor work.
The exact scheduler internals and behavior on other machines were not measured.

`clipboard::settle_publication` now uses a high-resolution waitable timer and the
same message queue. Its monotonic deadline is authoritative after every wake;
relative timer durations round up to 100-ns units. Creation/arming/wait failure
uses the original timeout path. The handle is closed by RAII. No busy spinning,
global timer-resolution request, or new thread is introduced.

Microsoft references consulted this session:
[timer creation](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw),
[relative timer semantics](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-setwaitabletimerex),
[message-aware waiting](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex).

## Matched desktop comparison

A = CI baseline f60aacd; B = candidate 9b6033a. Candidate executable SHA-256:
`55956f12b81ed1bf10fe6efb478e707455b1153d7df171c18e1b682e50ceb080`.
[Candidate CI passed](https://github.com/ElliotDrel/Universal-Spell-Check/actions/runs/39975684966):
format, lint, Dev/Prod regressions (including minimum/fallback/message-pump tests),
optimized build, native startup and packaging. The first experimental commit had
a binding constant compile error, corrected before any candidate desktop run.

Order: off-A1, on-A1, off-B1, on-B1, off-B2, on-B2, off-A2, on-A2. Each block has
ten frozen cases, five measured attempts and one retained warmup per case.
All blocks passed 50/50 measured deliveries and 10/10 warmups: 480/480 total.
Every settlement sample is at least 50 ms; measured candidate minimum 50.0404 ms.
Handwritten quality remains 45/50 per block because the frozen rich-list output
has the known two trailing spaces before LF (#16), identically in both variants.

| Block | Completion median / p95 ms | Settlement median / p95 ms |
|---|---:|---:|
| off-A1 | 81.160 / 93.375 | 59.903 / 64.631 |
| on-A1 | 83.216 / 94.536 | 62.292 / 65.079 |
| off-B1 | 70.253 / 85.775 | 50.346 / 50.602 |
| on-B1 | 69.957 / 78.519 | 50.368 / 50.527 |
| off-B2 | 71.015 / 85.271 | 50.292 / 50.562 |
| on-B2 | 71.081 / 84.048 | 50.295 / 50.570 |
| off-A2 | 82.240 / 97.433 | 62.386 / 65.184 |
| on-A2 | 82.610 / 89.478 | 63.178 / 65.251 |

This is roughly 10–13 ms less median local completion, not an AI request speedup.
Keep modes and blocks separate; no significance or universal tail guarantee is
claimed. Posted registered hotkeys exclude physical-key/modifier-release latency.
The native editor checks full text and requested CF_HTML, not browser rendering.
Both variants used unchanged cases/fixtures/replacements, Ctrl+Alt+F10 and a
30-second unmeasured activation allowance. Foreground/selection gates stayed intact.
Each logging-on block used a fresh isolated evidence store. `worktree_commit` is
launch-time checkout state; `binary_source_commit` identifies the actual CI build.

## Real editor acceptance

Seven candidate corrections used the actual Ctrl+Alt+U hotkey with live gpt-4.1:
five in Chrome and two in Notepad. All delivered complete text. Candidate waits
were 50.075–50.493 ms. Pipeline logs were exported only through the approved reader.

- Chrome plain text preserved URL, Windows path and UUID exactly.
- A declared synthetic rich producer (`data-pm-slice`) in a real contenteditable
  preserved corrected bold text, italic text, exact link href, blank paragraph,
  list/order and full text. Two changed attempts and one unchanged attempt had
  exact matching editor HTML; paste counts incremented once per correction.
- Notepad corrected multiline text and retained its blank line and protected
  URL/path; a second correction retained the complete visible result.
- One single-paragraph rich fixture lost bold/link formatting: the native copied
  selection was classified `unsupported_editor` and plain output was requested.
  Installed Production 0.13.2 reproduced the identical result. This is an existing
  limitation, not a timer regression or a passing formatting test; see
  [#18](https://github.com/ElliotDrel/Universal-Spell-Check/issues/18).

Chrome is a real rendering acceptance test with synthetic source markup, not
Google Docs or the actual ChatGPT composer. The unsupported-selection case and
all observations remain in browser-results.json. Native copied markup can differ
from editor DOM; do not treat the fixture's marker as proof it was in the fragment.

The temporary candidate used isolated settings and a DPAPI-encrypted credential
copy, then was stopped. Installed managed Production 0.13.2 was restored; original
settings, encrypted key, startup marker and Run target were hash/value verified
unchanged. No production release or automatic-update test was performed.

## Recheck the record

Run `python bench/records/2026-10-desktop-latency/settlement/verify.py` for all
retained attempts and native wait probes. `live-pipeline.json` contains only
synthetic correction fields from approved-reader exports. `editor.html` is the
disposable fixture; screenshots are PR attachments, not committed review media.
The implementation is retained based on these bounded reliability checks.
Investigation: [#17](https://github.com/ElliotDrel/Universal-Spell-Check/issues/17).
