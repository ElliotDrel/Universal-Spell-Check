# Project Overview

Full project context. Routed from root `AGENTS.md` when overview, stack, or repo map is needed.

## What this is

**Universal Spell Check** — a Windows-wide AI spell checker. The native Rust tray app and dashboard under `rust/` are the product; `src/` retains the C# reference and shared build constants: select text, press the hotkey, corrected text replaces the selection in place. The legacy AutoHotkey path is archived under `.archive/ahk-legacy/` for history only.

**Core value:** spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

## Stack

- Native Rust / Windows GUI tray app and dashboard (`rust/`); C# reference under `src/`
- Velopack for auto-update + GitHub Releases for distribution
- OpenAI Responses API
- Windows only
- Python 3 for regression, benchmark and fine-tune tooling
- Build: `cargo build --locked --manifest-path rust/Cargo.toml --release --no-default-features`; package with `rust/package.ps1`

## Developer setup

Install the pinned Rust toolchain from `rust-toolchain.toml` and the Windows MSVC
build tools. From the checkout root:

```powershell
cargo build --locked --manifest-path rust/Cargo.toml --release
cargo test --locked --manifest-path rust/Cargo.toml
```

The default build is Dev. Add `--no-default-features` for Prod. The native app needs
no Node.js, npm, AutoHotkey or .NET runtime. The retained C# reference and differential
formatting oracle use the .NET SDK selected by `global.json`; Python regression and
benchmark tooling uses the ignored `.venv/` with `requirements-dev.txt`.

Keep the Velopack library and packaging CLI on the same version. Use `rust/package.ps1`
for local packaging and the deploy skill for publishing. Enter live API credentials
through Settings; ordinary offline tests need no real key.

## Channels

- **Prod** (Rust `--no-default-features`) — Ctrl+Alt+U, mutex `UniversalSpellCheck`, settings under `%LocalAppData%\UniversalSpellCheck.Data\`. Auto-updates from GitHub Releases.
- **Dev** (default Rust features) — Ctrl+Alt+D, mutex `UniversalSpellCheck.Dev`, settings under `%LocalAppData%\UniversalSpellCheck.Dev\`, tray icon golden yellow. Never auto-updates; updated via `git pull` + `cargo run --manifest-path rust/Cargo.toml --release`.

**Logs are unified across channels:** both write to `%LocalAppData%\UniversalSpellCheck.Data\logs\spellcheck-{date}.jsonl`. Every line is stamped with `channel`, `app_version`, and `pid` so downstream tooling can filter while keeping the corpus together for fine-tune use. `%LocalAppData%\UniversalSpellCheck` is reserved for Velopack installation files and must never hold durable user data.

All channel constants live in `src/BuildChannel.cs` — that file is the canonical source. Never hardcode channel-conditional values elsewhere.

## Primary files and repo map

- `rust/src/main.rs`, `app.rs` — bootstrap, tray, hotkey and application lifetime.
- `rust/src/runtime.rs` — persistent correction worker and capture/request/paste pipeline.
- `rust/src/clipboard.rs` — Win32 clipboard, modifier waits and input injection.
- `rust/src/http.rs` — persistent native WinHTTP client and Responses API.
- `rust/src/core.rs`, `rich.rs`, `docs_native.rs` — protection, post-processing and rich reconstruction.
- `rust/src/timing.rs` — monotonic local-operation measurements, including failed stages.
- `rust/src/dashboard.rs`, `activity.rs` — native dashboard and activity feed.
- `rust/src/store.rs`, `platform.rs` — settings, DPAPI keys, paths and data migration.
- `rust/src/update.rs` — unified updater.
- `src/BuildChannel.cs` — canonical shared channel constants, generated into Rust at build time.
- `src/` — retained C# WinForms/WPF reference and formatting oracle.
- `.github/workflows/release.yml` — tag-triggered Rust packaging and production release.
- `.github/workflows/rust-checks.yml` — native checks and candidate artifacts.
- `replacements.json` — shared replacement rules, packaged beside the executable.
- `bench/` — benchmark harness, public fixtures and portable historical records.
- `benchmark_runs/` — ignored local artifacts; never transfer private data through Git.
- `tests/`, `rust/tests/` — regression suites and differential tooling.
- `docs/`, `.planning/` — focused subsystem guidance and research/handoffs.
- `.agents/` — canonical agent instructions and helper skills.
- `.archive/ahk-legacy/` — archived AHK/Python implementation.

## Tone when collaborating

Speed first, simplicity second, minimal UI/overhead third. Treat every added abstraction or fallback as a cost. Concise, action-oriented responses. Decide fast.

## Native Rust product

- Find the native Rust product in `rust/`; keep `src/` as the C# reference.
- Use native Windows controls through `native-windows-gui`, direct Win32 clipboard/keyboard/DPAPI calls, native asynchronous WinHTTP, and the matching Velopack Rust SDK.
- Keep the dashboard on its own thread; the resident tray has no GPU, WebView, asynchronous runtime or continuous render loop.
- Build Dev by default; add `--no-default-features` for Prod. Keep the pinned toolchain in `rust-toolchain.toml` and dependencies in `rust/Cargo.lock`.
- Read `rust/AGENTS.md` for native commands and `docs/bench.md` for comparison boundaries.
- Production packaging uses Rust through the tag-triggered release workflow; the Rust checks workflow uploads a candidate without publishing a production release.

Python tooling setup from a fresh checkout:

```powershell
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements-dev.txt
```
