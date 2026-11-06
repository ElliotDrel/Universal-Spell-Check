# Universal Spell Check

A Windows-wide AI spell checker. Select text in any app, press the hotkey, and the corrected text
replaces the selection in place. It is a native Rust tray app with a small dashboard, shipped
through Velopack and GitHub Releases. The user is Elliot, who runs it all day.

**Speed is the product.** The hotkey path must feel instant. Prefer the simplest, lowest-level
implementation that measurably improves the complete correction path while staying correct.
Question unnecessary work before tuning it. Every PR is gated on request size and end-to-end
latency (see Verifying).

Collaboration: speed first, simplicity second, minimal UI third. Be concise and decide fast.

## Channels

Prod and Dev run side by side from the same code:

| | Prod | Dev |
|---|---|---|
| Build | `--no-default-features` | default (`dev` feature) |
| Hotkey | Ctrl+Alt+U | Ctrl+Alt+D |
| Settings and API keys | `%LocalAppData%\UniversalSpellCheck.Data` | `%LocalAppData%\UniversalSpellCheck.Dev` |
| Logs (shared) | `%LocalAppData%\UniversalSpellCheck.Data\logs` | same |

## Hard rules

1. **Channel constants live in `rust/src/channel.rs`.** Never hardcode a hotkey, mutex, data folder or display string elsewhere.
2. **Logs are shared, settings are isolated.** Both channels append to one daily JSONL corpus, stamped per line with `channel`, `app_version` and `pid`. Never split logs by channel; the unified corpus is the fine-tune dataset.
3. **User data never lives in Velopack's install root** (`%LocalAppData%\UniversalSpellCheck`), which reinstalls replace. Keep the legacy-data migration intact.
4. **One update flow.** Launch, periodic, tray and dashboard checks all go through the single `Updater` worker in `rust/src/update.rs`.
5. **Releases ship via tag.** A `v*.*.*` tag triggers `.github/workflows/release.yml`. Use the `deploy` skill for Dev pushes and Prod releases, including its closeout checklist. Never tag or run `vpk` by hand.
6. **Never mix model parameter families.** Standard models take `temperature`; reasoning models take `reasoning.effort`. See `docs/model-config.md`.
7. **Debug before fixing.** When the cause is unclear, add logging, reproduce, read the log, then fix.
8. **A change is not running until it is rebuilt and relaunched.**
9. **A failed correction never pastes over the user's selection.** Rich formatting may fall back to plain text; a completed correction is never dropped just because formatting failed.
10. **The dashboard never blocks the hotkey.** Log I/O and scans stay off the UI thread, and the first render is one 30-row page.

## Where things live

- `rust/src/` is the app. `runtime.rs` runs a correction (capture, protect, request, post-process, rich mapping, paste); `app.rs` owns the tray, hotkey and lifetime; `dashboard.rs` is the GUI; `http.rs` is WinHTTP; `core.rs` holds the prompt, payload, literal protection and replacements; `rich.rs`, `rich_links.rs` and `docs_native.rs` rebuild formatting; `bench.rs` is the end-to-end harness.
- `rust/vendor/native-windows-gui/` is a patched dependency; see its `PROVENANCE.md` before touching it.
- `bench/` holds the frozen cases (`cases.json`), recorded model answers (`replay.json`), the request-size budget (`perf-budget.json`) and `perf.py`.
- `replacements.json` is the user's brand and casing fixes, applied after the model.
- `.agents/skills/` holds `deploy`, `read-logs` and `formatting-troubleshoot`.
- `_archive/` holds retired code kept for history (AHK app, fine-tune tooling). It is not built or maintained.

## Docs

Read the matching doc before working in its area:

- `docs/architecture.md`: the correction pipeline, threading, clipboard, updates and the dashboard, plus traps that broke before.
- `docs/formatting.md`: literal protection, replacements, terminal normalization and rich-text (CF_HTML, Google Docs) reconstruction.
- `docs/logging.md`: the JSONL format, `spellcheck_detail` fields, timings and developer evidence.
- `docs/testing.md`: the `--bench` harness, the performance gate and how to verify a change.
- `docs/model-config.md`: models, payloads and API keys.
- `DESIGN.md` and `PRODUCT.md`: read before any dashboard or visual change.

For runtime logs, use the `read-logs` skill (`python .agents/skills/read-logs/scripts/logs.py --help`); never parse log files by hand.

## Verifying

- `cargo test --manifest-path rust/Cargo.toml` (Dev) and the same with `--no-default-features` (Prod). Add `cargo clippy --all-targets -- -D warnings` and `cargo fmt --check` before pushing.
- Processing changes: run `--transform-json` or `--rich-json` over stdin for a deterministic replay, and extend the existing table tests (`formatting-contracts.jsonl`, `bench/replay.json`) rather than adding new ones.
- Hot-path changes: build `--release` and run `universal-spell-check.exe --bench`. It takes the foreground for about 10 seconds, so run it on an idle desktop. Compare builds with `python bench/perf.py --base <old.exe> --candidate <new.exe>`.
- If you change the prompt or payload, run `USC_UPDATE_BUDGET=1 cargo test perf_budget` and expect CI to fail if requests grow. Only Elliot accepts that, with the `perf-regression-accepted` label.
- After changing docs, structure or config, update the doc that owns the fact in the same change. Don't add a doc when an existing one can own it.

---
*Maintenance: this file is a letter — short, intent-only, grown one earned
line at a time. Agent: as you work, watch for things this letter should
learn — new or ambiguous vocabulary (glossary candidates), corrections the
user has had to repeat, anything that changes the intent, rules, or routing
written here. When you notice one, invoke the `estack-claude-md-optimizer`
skill (quick capture): ask the user in one line whether to add it, and if yes do
it right then — under 5 minutes, do it now — then return to your task.
Never silently append, and never let a noticed improvement get lost. For
humans: run the skill directly — refine to audit, session-capture after
working sessions, scale-check before adding any routing.*
