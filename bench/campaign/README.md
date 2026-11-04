# Live performance campaign

Status: aggregate send cap and independent editor owner implemented. Live baseline
is blocked on required Chrome tool app approval and a passing rich-fixture pilot.
Zero live API requests and zero optimization loops have run. No speed win is claimed.

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

## Desktop owner and current blockers

Build the Release binary with `cargo build --release --manifest-path rust/Cargo.toml`.
`run.py` owns one disposable Chrome profile/editor and the native `--campaign-worker`
process. It uses only synthetic fixtures. Saved credentials are loaded read-only by
the native protected store; no plaintext key is exported. The runtime rejects a
captured selection differing from the authorized fixture before creating a payload.
Only a native window titled USC disposable editor may be triggered.

The editor observes actual paste/input and snapshots destination text/HTML/structure
in a separate process. QPC ties the coordinator trigger to receipt of the editor's
snapshot, including the loopback delivery overhead. That is a conservative boundary;
never substitute SendInput success or requested clipboard HTML for destination proof.
The owner stops its processes before restoring the complete clipboard and checking
production settings, encrypted credential and startup hashes/registration unchanged.

Chrome app approval through Computer Use timed out. Idle-desktop authorization does
not substitute for that gate. No more desktop runs are allowed until it is granted;
`--desktop-app-approved` asserts an actual external grant, never grants it itself.
A benchmark-owned Chrome launch is not a workaround for the outstanding gate.

The retained recorded-response pilot verified 9/11 fixtures. Rich-list nested another
list on native paste; structured preserved the DOM but its observer omitted text
inside a wrapper. The observer serialization is corrected in source, unvalidated
on desktop. Rich-list remains unresolved. These offline timings are diagnostics,
not live performance evidence. Preserve all failed pilots, including private
unmatched-capture artifacts locally; never publish those raw artifacts.

After approval, run a fresh `--pilot`; require all eleven cases and negative controls.
Live mode additionally requires `--validated-pilot <directory>`, a matching binary,
and frozen cases/schedule/evaluator/editor/settings/prompt hashes. Those hashes bind
the persistent campaign before live API use. Output paths and benchmark allocations
are exclusive. A fixture failure stops the block and remains explicit.

After each completed loop: preserve attempts, commit/push, and post attributed
progress on PR #33 with source/binary hashes, request counts, per-case before/after
scores, failures, correctness, decision and next hypothesis. Incorporate the parent's
read-only findings only after a valid baseline. No merge, release or PR watch.
