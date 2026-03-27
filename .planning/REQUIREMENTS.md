# Requirements: Universal Spell Checker

**Defined:** 2026-03-27
**Core Value:** Spell checking must feel instant and invisible — select, hotkey, done. Speed is the product.

## v1 Requirements

Requirements for this milestone. Each maps to roadmap phases.

### Reliability

- [ ] **REL-01**: Script saves and restores user's clipboard content after each invocation (ClipboardAll)
- [ ] **REL-02**: Paste race condition eliminated — ClipWait or Sleep between clipboard set and Ctrl+V
- [ ] **REL-03**: Modifier key sticking prevented — KeyWait at handler start, Send("{Ctrl Up}") in finally block
- [ ] **REL-04**: Transient API failures retried automatically with exponential backoff (max 2 retries)
- [ ] **REL-05**: API errors classified and shown to user as readable messages (network / auth / rate limit / server)

### Performance

- [ ] **PERF-01**: WinHTTP COM object persisted across invocations (eliminate 50-150ms TLS handshake per call)
- [ ] **PERF-02**: Debug event logging gated behind a flag (eliminate 16+ string concatenations on hot path)
- [ ] **PERF-03**: Predicted Outputs enabled for gpt-4.1 via Chat Completions API (40-70% latency reduction)
- [ ] **PERF-04**: Diff-based structured output returns only corrections, not full text (5-10x output token reduction)
- [ ] **PERF-05**: AHK delay settings optimized (SetKeyDelay(-1), SetControlDelay(-1))

### UX

- [ ] **UX-01**: Visual feedback tooltip shown while waiting for API response
- [ ] **UX-02**: Paste skipped when AI output matches input (preserve cursor position and undo history)
- [ ] **UX-03**: Empty selection handled gracefully with user notification instead of error
- [ ] **UX-04**: Re-entry guard prevents double-fire if hotkey pressed twice quickly

### Security

- [ ] **SEC-01**: API key moved from hardcoded source to environment variable (OPENAI_API_KEY)

### Code Quality

- [ ] **QUAL-01**: Magic numbers extracted to named constants
- [ ] **QUAL-02**: HTMLFile COM replaced with lightweight regex HTML stripper (eliminates clipboard deadlock risk)

### Replacements

- [ ] **REPL-01**: Case-insensitive replacement matching to reduce variant count in replacements.json

### AI Enhancement

- [ ] **AI-01**: Word dictionary system for AI to correct domain-specific terms with correct formatting (without bloating prompt)

### Log Viewer

- [ ] **LOG-01**: Log viewer staleness detection — show popup when HTML data is outdated with generation timestamp
- [ ] **LOG-02**: One-click re-run capability to regenerate viewer from the HTML page itself

## v2 Requirements

Deferred to future milestone. Tracked but not in current roadmap.

### Native App

- **APP-01**: Windows native app replacing AutoHotkey for always-on networking and faster requests
- **APP-02**: Built-in UI for spell check history and stats
- **APP-03**: HTML formatting round-trip for apps like Google Docs (spell check preserving rich text)

### Diff UI

- **DIFF-01**: Spell check diff view showing what was changed (Wiser Flow-style bar)
- **DIFF-02**: Hotkey hold or click to reveal diff details

### Integrations

- **INT-01**: Terminal/CLI integration for spell checking inputs to Claude Code and Codex CLI
- **INT-02**: Auto spell check on Claude send (hook integration)

### Local Model

- **LOCAL-01**: Investigate and implement local model for instant response times

## Out of Scope

| Feature | Reason |
|---------|--------|
| Multi-user / OAuth | Personal tool, single user |
| Mobile app | Windows-only scope |
| Real-time checking (as-you-type) | Violates core "speed is the product" philosophy — adds latency to typing |
| GUI settings window | Over-engineering for a hotkey tool |
| SSE streaming | Not viable with WinHTTP COM object; no benefit for paste-back workflow |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| REL-01 | — | Pending |
| REL-02 | — | Pending |
| REL-03 | — | Pending |
| REL-04 | — | Pending |
| REL-05 | — | Pending |
| PERF-01 | — | Pending |
| PERF-02 | — | Pending |
| PERF-03 | — | Pending |
| PERF-04 | — | Pending |
| PERF-05 | — | Pending |
| UX-01 | — | Pending |
| UX-02 | — | Pending |
| UX-03 | — | Pending |
| UX-04 | — | Pending |
| SEC-01 | — | Pending |
| QUAL-01 | — | Pending |
| QUAL-02 | — | Pending |
| REPL-01 | — | Pending |
| AI-01 | — | Pending |
| LOG-01 | — | Pending |
| LOG-02 | — | Pending |

**Coverage:**
- v1 requirements: 21 total
- Mapped to phases: 0
- Unmapped: 21

---
*Requirements defined: 2026-03-27*
*Last updated: 2026-03-27 after initial definition*
