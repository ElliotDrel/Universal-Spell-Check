# Rust app

- Read root routing, `docs/architecture.md`, `docs/bench.md`, and `.planning/rust-port-performance-handoff.md` before changing the port.
- Build development with `cargo build --manifest-path rust/Cargo.toml --release`; build production with `--no-default-features`.
- Keep Windows calls behind native modules; keep processing and rich reconstruction free of Windows state.
- Derive channel constants from `src/BuildChannel.cs` at build time until the C# reference is retired.
- Run `cargo test --manifest-path rust/Cargo.toml` and the C# differential suite in `rust/tests/parity.py` after processing changes.
- Run `--transform-json <replacements.json>` or `--rich-json` over stdin for deterministic processing without clipboard, UI, credentials, or network access.
- Require an explicit idle desktop window before launching any clipboard benchmark or UI acceptance test.
