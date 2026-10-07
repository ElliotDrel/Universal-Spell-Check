---
name: formatting-troubleshoot
description: Diagnose and fix Universal Spell Check clipboard and paste formatting problems using local logs, developer evidence, and native replay. Use when a correction changes styling, spacing, links, or lists, loses content, or pastes incorrectly.
---

# Formatting troubleshooting

Turn Elliot's brief formatting report into a reproduced, verified fix with as little repeated work from him as possible.

Work in the Universal Spell Check repository. On Elliot's laptop, the primary checkout is `C:\Users\2supe\All Coding\Universal Spell Check`. Read its `AGENTS.md`; the mechanics live in the repository's `read-logs` skill, `docs/debugging-principles.md` (Local formatting replay), and `docs/replacements-and-logging.md` (Optional developer evidence).

## Required outcomes

- Identify the reported run using the available time, app, text, channel, and version. Use the log-reader tool rather than parsing the daily logs yourself. Ask one narrow question only if the evidence leaves materially different incidents plausible.
- Establish what happened from the original Unicode/HTML/RTF, requested paste, clipboard readback, editor identity/selection, and automatic before/after images. Check completeness, hashes, capture timing, changed destinations, and failure statuses. A missing or late capture is not proof that the editor lacked formatting. Ordinary records can reference a pending manifest; the separate completion event gives its final status.
- Freeze complete inputs into an ignored local replay case, then reproduce with the actual native mapper and recorded corrected output. Preserve the original evidence. An exact historical match reproduces behavior; it does not establish that the behavior is correct.
- Trace the root cause and fix it. Verify the intended content and formatting, retain a sanitized regression that would catch the failure, and rebuild/relaunch the Dev app for native checks. Iterate independently while the available evidence supports meaningful verification.
- Use existing local/native/browser testing tools for behavior beyond the mapper. Replay does not reproduce the AI request, target hooks, RTF rendering, or the destination editor. Use the lightest test that covers the actual failure; do not add a browser extension, reporting UI, or general sandbox infrastructure.
- Report the change, its evidence, what was tested, and any remaining uncertainty. Make the fix available for one final retest in the original editor and selection context. Follow the deployment skill when a release is requested.

Developer logging is opt-in under Settings → Logs and applies to the next correction, independently per channel. It automatically gathers context locally; do not require Elliot to attach an Appshot or screenshot manually. If necessary evidence was never captured, use what is available and ask for one focused reproduction with logging enabled. Do not invent missing clipboard data or claim a complete editor environment was reconstructed.
