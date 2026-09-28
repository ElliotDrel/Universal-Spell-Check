# Tooling Gaps — Debugging Workflow

The three gaps from the `Competitionetition` investigation shipped on 2026-06-01. The two gaps from the 2026-09-28 ChatGPT formatting incident are now addressed: `logs.py --event spellcheck_detail --last 2` searches across midnight and shows the rich-text decision, while `spellcheck_detail` records the final requested `paste_text` and `paste_html` payloads. The log still cannot prove how a destination website rendered a paste. Add further instrumentation only if a real incident shows a remaining gap.
