# OpenAI API log backfill audit

**Outcome (2026-09-23):** No specific missing local spell-check record was confirmed. No API records were imported. A complete ID-by-ID comparison was unavailable, and Elliot chose to stop the backfill investigation. This is a historical audit, not an open claim that local history is missing.

Checked the authenticated OpenAI Platform Logs page on 2026-09-22.

- The `Universal Computer Spell Check` project showed 1,545 Responses records.
- The other six projects `Default project`, `Avi Blind People Project`, `Better AI Chat Extension`, `BetterBook Extension`, `E-Brain`, and `Model Advisor - Drel Solutions` showed zero Responses records.
- The Completions source showed no stored Chat Completions in any of the seven projects.
- The page offered search, filters, and paginated `Load more`; it exposed no bulk export/download control.
- OpenAI's public Responses API reference documents retrieving a response by known ID. It does not document a list-all-responses endpoint. The Platform notice says Responses logs older than 30 days will soon no longer be available.
- Existing appdata logs cover 2026-07-03 through 2026-09-22. The local reader reports 4,118 runs, 4,047 successful checks, and 3,954 changed-text corrections overall.
- For 2026-08-24 through 2026-09-22, the local reader reports 1,567 runs, 1,542 successes, and 1,494 changed-text corrections. This is close to the Platform's 1,545 Responses records, but IDs and records could not be bulk-exported for exact reconciliation.

No API records were appended. The three-record count difference was not reconciled, but counts alone do not establish missing records; importing anything without the actual records would risk duplication or incorrect timestamps. The app's current statistics continue to come from its existing local log corpus.

Recommended extraction scope: `Universal Computer Spell Check` → Responses only. Exclude the other projects and every Completions source because the review showed no records there.

## Retention boundary follow-up

On a follow-up dashboard check on 2026-09-22, the Responses count had risen to 1,550. The dashboard date picker allowed filtering from 2026-08-23 onward; dates before 2026-08-23 were disabled. This is the earliest queryable date offered by the UI, not a confirmed timestamp of the oldest individual response. The exact oldest record remains unverified. The dashboard continued to show the notice that Responses logs older than 30 days will soon no longer be available.

## Bulk export research

Open-source repository review found no maintained exporter for historical request and response bodies from the Platform Logs dashboard. `openai-spend-collector` and the official OpenAI CLI expose paginated or date-ranged usage and cost aggregates, not prompts or corrected text. `openai-orgs` exposes organization audit events, not model request content. Community approaches that call undocumented dashboard-session endpoints are fragile and were not identified as maintained exporters.

The official List Chat Completions endpoint can page through Chat Completions saved with `store=true`; it cannot recover unstored calls. The published Responses API reference exposes retrieval by known response ID, but no list-all-Responses method. Sources reviewed: https://github.com/hameddk/openai-spend-collector, https://github.com/klauern/openai-orgs, https://github.com/openai/openai-cli, https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/list/, https://developers.openai.com/api/reference/resources/responses/methods/retrieve, and https://developers.openai.com/api/docs/guides/your-data.

## 2026-09-23 follow-up

- Authenticated `GET /v1/chat/completions?limit=10&order=asc` with the user-supplied project key. The request succeeded and returned zero stored Chat Completions with `has_more=false`. The key was not written to disk or sent to support.
- The Responses Logs dashboard now shows 1,569 results for `Universal Computer Spell Check`, up from 1,550 on 2026-09-22. The API key did not provide a documented way to enumerate Responses IDs or retrieve those records in bulk.
- The existing OpenAI Help Center conversation first distinguished Audit Logs from per-request prompt/response logs. I clarified that the need is spell-check payload history and asked for a human API specialist. The widget confirmed escalation and said a specialist may reply in the coming days, including by email. The support tab remains open.
- No remote log payloads have been saved or appended to the local spell-check history. The project dashboard remains the only currently verified place to read the Responses payloads.

## 2026-09-23 dashboard and support follow-up

- Reopened the OpenAI Help Center and confirmed the same support conversation is present. It remains escalated to a human specialist and still says a reply may take several days. The support thread is left open for follow-up.
- Reopened one Responses record in the Platform Logs UI. Its detail view shows the full response ID, exact created timestamp, model, input, and output. There is no visible export/download action on that detail view. The inspected record is not imported because a single un-reconciled row would make desktop totals inaccurate or duplicate existing history.
- The UI list is paginated with `Load more`; this is the only verified way to enumerate Response IDs in the dashboard. A full 1,569-row local capture has not been achieved.
- Rechecked the official API reference on 2026-09-23. Responses has documented create/retrieve/delete and list-input-items operations, but no list-all-responses operation. Chat Completions has a documented paginated list operation, but it only returns calls created with `store=true`. The authenticated list call for this project returned zero records.
- No OpenAI API payloads have been added to the desktop app's local log corpus. The original/corrected/timestamp backfill remains unperformed, and all-time desktop totals are unchanged.
- The API key was pasted into this conversation. It is not included in this file. Revoke it and create a replacement after retrieval work; do not leave the exposed key active until its scheduled expiry.

## 2026-09-23 local presence check

- Queried the local JSONL corpus with the `read-logs` CLI for 2026-08-24 through 2026-09-23. It reports 1,607 spellcheck runs and 1,582 successes. The Responses dashboard showed 1,569 records.
- Queried 2026-09-23 alone. The local corpus has 26 runs and 26 successes.
- Checked one full response ID from a dashboard detail view against the local `spellcheck_detail` output. The ID is present locally.
- These counts do not prove complete or missing records because run counts are not an ID-level comparison and may include local failures or records outside the dashboard's exact filter window. No specific missing dashboard record is confirmed. A full reconciliation still requires all dashboard response IDs or an OpenAI bulk export.

## 2026-09-28 clarification

The later apparent gap in the dashboard activity feed was a separate display-order issue fixed in commit `7981db3`; its underlying local spell-check log entries were present. It did not require an API log backfill.
