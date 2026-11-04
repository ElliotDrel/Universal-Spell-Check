# Live performance campaign

Status: frozen Release correction benchmark goal independently confirmed and stopped.
Loop 2 scored 956.29 ms; separate same-binary confirmation scored 936.59 ms,
each with 99/99 verified corrections (nine/case) and 100 actual HTTP sends.
Campaign total 288 sends, two optimization loops and one confirmation. Earlier
baseline/loop failures remain rejected and retained. No speedup over the incomplete
baseline is claimed. See [final results](records/README.md) for per-case data and scope.

## Acceptance and frozen inputs

Score the equal-weight arithmetic mean of each case's median trigger-to-verified
destination insertion latency, below 1000 ms. Every scheduled repetition must have
correct text, required rendered formatting, a real live response and independent
observation. Failed/missing/unsupported coverage prevents acceptance. The scorer
requires the exact seeded order, repetition identity, explicit warmup marker and
integer process identities. Its CLI exits zero only when the goal is verified.

Keep all eleven case IDs in `cases-v1.json`: short, paragraph, long, unchanged,
brands, literals, unicode, multiline, rich-link, rich-list and structured. The HTML
is browser source (fragment or CF_HTML); Chrome's copy event generates Windows
CF_HTML. Do not pass this campaign file to the generic native clipboard producer.
Never trim text, weaken expected output, skip unchanged paste or reduce 50 ms settle.
Keep model/prompt fixed initially. No actual LinkedIn, Codex or real documents.

`schedule-v2.json` supersedes v1 before any optimization: one real startup HEAD
request plus 99 corrections, nine repetitions per case, in frozen seeded order.
This fixes the initial 100-correction proposal's missing warmup allocation without
changing any text, expected output, formatting requirement or score weighting.
Retries and periodic warmups consume remaining slots and can make a block incomplete.
Do not replenish an incomplete block or erase its failures.

## Native and aggregate caps

`request_budget.rs` durably reserves immediately before every native send. Each
benchmark permits at most 100 sends including retries/warmups. The shared campaign
ledger uses an exclusive Windows file handle for each reservation, validates all
prior records and caps the entire campaign at 2100. A partial/corrupt ledger or
budget error fails closed. Reservations conservatively consume a crash gap before
send and are not billing receipts. Native desktop and request-probe entry points
require the aggregate ledger; normal installed application requests are unaffected.

Baseline and at most 20 post-baseline benchmark allocations fit the 2100 ceiling.
Reserve the final allocation for independent confirmation (normally at most 19
candidate screens plus confirmation). If baseline passes, run independent confirmation
immediately and stop. No unconfirmed winner satisfies the goal.

## Desktop owner and verification

Build the Release binary with `cargo build --release --manifest-path rust/Cargo.toml`.
`run.py` owns one disposable Chrome profile/editor and the native `--campaign-worker`
process. It uses only synthetic fixtures. Saved credentials are loaded read-only by
the native protected store; no plaintext key is exported. The runtime rejects a
captured selection differing from the authorized fixture before creating a payload.
Only a native window titled USC disposable editor may be triggered.

The editor observes actual paste/input and snapshots destination text/HTML/structure
in a separate process after two animation frames, matching the historical browser
observer's stable-state boundary. QPC ties the coordinator trigger to completion
of independent text/format verification, including snapshot delivery, native
coordinator receipt and polling overhead. Snapshot receipt is recorded separately. That is a conservative boundary;
never substitute SendInput success or requested clipboard HTML for destination proof.
The owner stops its processes before restoring the complete clipboard and checking
production settings, encrypted credential and startup hashes/registration unchanged.

The benchmark-only coordinator uses the actual Release correction worker. Tray,
dashboard, loading overlay and physical hardware hotkey delivery are excluded;
the boundary begins at an accepted synthetic coordinator trigger. Bare single-block
span HTML retains the known `unsupported_editor` limitation for changed and unchanged
text. No allowlist expansion or unsupported-editor pass is introduced.

Chrome app approval initially timed out, then normal app access succeeded after the
user granted it. Idle-desktop authorization does not substitute for that gate;
`--desktop-app-approved` asserts an actual external grant, never grants it itself.
A benchmark-owned Chrome launch is never a workaround for a missing gate.

The earlier retained recorded-response pilot verified 9/11 fixtures. Rich-list nested another
list on native paste; structured preserved the DOM but its observer omitted text
inside a wrapper. Fresh granted desktop pilots verified the corrected serializer.
Paste-time evidence showed flat clipboard HTML reaching Chrome but default paste
retaining an outer list item. The disposable rich editor now deletes exactly a
whole-root selection before Chrome performs its normal default paste; it neither
injects expected output nor cancels paste. All eleven unchanged expectations pass.
The fixture owner maintains the authorized idle focus during API latency. These
fixture semantics do not certify arbitrary bare contenteditable editors. Offline timings are diagnostics,
not live performance evidence. Preserve all failed pilots, including private
unmatched-capture artifacts locally; never publish those raw artifacts.

After approval, run a fresh `--pilot`; require all eleven cases and negative controls.
Live mode additionally requires `--validated-pilot <directory>` and
`--validated-negative <directory>`, a matching frozen binary snapshot,
and frozen cases/schedule/evaluator/editor/settings/prompt hashes. Those hashes bind
the persistent campaign before live API use. Output paths and benchmark allocations
are exclusive. A fixture failure stops the block and remains explicit.

After each completed loop: preserve attempts, commit/push, and post attributed
progress on PR #33 with source/binary hashes, request counts, per-case before/after
scores, failures, correctness, decision and next hypothesis. Incorporate the parent's
read-only findings only after a valid baseline. No merge, release or PR watch.

The prepared primary checkout must retain its historical
`benchmark_runs/experiment-evaluation-2026-10-31/desktop.py` clipboard guard. The
runner locates it through the shared Git directory and checks it before creating
output or starting a run. A clean clone without that artifact is not sufficient.
Pilot gates recompute observations, process identities and restoration checks;
caller-supplied passed flags are insufficient. Formatting references in
`formats-v1.json` require exact link labels/destinations, bold content, code
literals and ordered list depth/item structure. Session-bound fixture posts and
matching native HWND/browser PID checks reject stale or unrelated observers.

Live evidence additionally requires native send timing, positive API token usage,
live request mode and matching request-reservation exposure. Offline responses
cannot satisfy that gate. An actual insertFromPaste event and collapsed selection
are required even for unchanged text. No extra live calls are authorized by a
missing fixture or failed correctness gate; the block stops and remains incomplete.

Historical rare native paste no-ops remain causally unresolved. This fixture pilot
is not evidence of a product delivery fix or certification of every editor.

The runner exits nonzero for an incomplete/incorrect pilot or an unmet live block
latency goal. Its binding also freezes the runner itself and the encrypted-store
fingerprint locally; it never exports plaintext credentials. A zero exit from one
live block still requires separate independent confirmation for campaign acceptance.

Headless diagnosis: `python -m unittest discover -s bench/campaign -p "test_*.py"`
requires Python and Node.js 22 or newer on PATH; CI explicitly provisions Node 22.
also exercises the actual observer text function against retained synthetic DOM
trees through a minimal adapter; this does not simulate browser editing or layout.
`python bench/campaign/check_fixture_replay.py --exe rust/target/release/universal-spell-check.exe --output <fresh-directory>`
replays all three frozen rich cases through the actual mapper, without clipboard,
credentials or API access. See `records/fixture-diagnosis.json`: the structured
paragraph serialization is covered; the later granted desktop diagnosis and fixed
whole-document fixture semantics are recorded in `records/fixture-after-grant.json`.
No source/expected workload was weakened. `records/sourcery-audit.json` preserves
the actual failed security-check output and the bounded subprocess audit rationale.

`records/rich-binding-scaling.json` records the bounded later-node search candidate.
An earlier covering match is impossible at the current cursor or when its node is
no longer than the current node. Otherwise searching only through
`min(source.len(), candidate - 1 + later_len)` retains every possible earlier
match and the original first-match semantics, including paragraph gaps. Native
tests compare the original predicate on 40,000 deterministic UTF-16 cases; fresh
C# differential tests pass 76/76 cases. Matched headless full-result parity passes
on nine cases. Process-inclusive scaling timings are diagnostic, not a campaign
score; the three frozen rich cases show no established practical benefit.

The first generated 1000-span all-nodes correction was refused by both binaries
with `alignment_budget_exceeded`, and remains a rejected diagnostic. A separate
single sparse-edit stress case applies correctly; no frozen campaign input,
expected output or correctness gate changed. Reproduce the diagnostic with
`python bench/campaign/check_rich_scaling.py --control <saved-control.exe> --candidate <candidate.exe> --output <fresh-directory>`.
`resume.json` contains commands and required gates. Do not run its desktop commands
until the actual Chrome tool app grant exists; the CLI flag asserts that grant and
cannot create it.
