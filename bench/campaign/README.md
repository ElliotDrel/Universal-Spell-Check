# Live performance campaign

Status: setup; no fresh baseline or optimization accepted.

This campaign starts at `711ff37`. Historical experiments are evidence, not
accepted improvements. Keep production and the primary checkout unchanged.

Acceptance is the equal-weight arithmetic mean of each frozen case's median
trigger-to-verified destination insertion latency, below 1000 ms. Every case
must have complete coverage, correct destination text and required formatting.
Failed, missing, timed-out, and unsupported coverage prevents acceptance;
successful-only timings may be reported only with their denominator.

Use disposable plain/native and Chrome synthetic rich editors only. Never open
real LinkedIn, Codex, or documents. Preserve and restore clipboard in a finally
guard and verify production settings, encrypted credentials and startup unchanged.
The current prompt/model and 50 ms minimum settle remain fixed.

Budget: baseline at most 100 actual HTTP send attempts, then at most 20 candidate
benchmarks of at most 100 each, including warmups and retries (2100 total).
If baseline passes, use a separate 100-request confirmation and stop. Winners
also need independent confirmation within the campaign ceiling. Reserve budget
before each native HTTP send; counting successful coordinator messages is unsafe.
No live requests may run before this boundary and its durable ledger are verified.

Freeze versioned cases before optimization: short, paragraph, long, unchanged,
protected literals, Unicode, rich paragraph/link and structured list/bold/link/path.
Use a seeded balanced order and retain the exact schedule and hashes. Interleave
candidate/control within the same 100-request allocation where practical; report
each arm's coverage rather than mixing arms into one score.

Prerequisites before baseline:

1. Enforce and test the native send-attempt cap including retries.
2. Validate out-of-process destination observation, including unchanged paste,
   nested messages, text and rich structure checks; reject injected wrong output.
3. Freeze evaluator, cases, settings/prompt hashes and run schedule.
4. Read saved credentials only through the native protected store. Never export keys.

After every completed loop, preserve failed attempts, commit and push, then comment
on the same campaign PR with model attribution, hashes, request counts, before/after
per-case results, failures, correctness verdict, decision and next hypothesis.
Do not merge, release, or close independent UI/audit work.

Resume state is `state.json`. No incomplete experiment counts as a completed loop.
