# Roadmap: Universal Spell Checker

## Overview

This roadmap hardens a working but fragile AHK v2 spell-checking script into a reliable, fast daily-driver tool. The work moves through six phases: first standing up a persistent background server to eliminate cold-start API latency, then fixing correctness issues that affect every invocation (clipboard loss, paste failures, stuck keys), then adding resilience for transient failures, then optimizing the persistent server with speculative decoding and connection tuning, then improving the parsing/replacements layer and tooling, and finally implementing the high-risk diff-based output mode that offers the largest theoretical speed gain. Speed first, then reliability, then optimization.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [ ] **Phase 1: Persistent Background Server** - Always-running local server that keeps a warm connection to the OpenAI API, eliminating cold-start TLS/HTTP overhead on every spell-check invocation
- [ ] **Phase 2: Reliability and UX Foundation** - Fix clipboard loss, paste failures, stuck keys, and add user feedback so every invocation is correct and visible
- [ ] **Phase 3: Error Handling and Resilience** - Graceful recovery from API failures with retries, readable error messages, and reduced log noise
- [ ] **Phase 4: Connection and Latency Optimization** - Persistent HTTP connections, Predicted Outputs for gpt-4.1, and client-side delay tuning
- [ ] **Phase 5: Parsing, Replacements, and Tooling** - Replace deprecated HTMLFile COM, add case-insensitive replacements, word dictionary, and log viewer improvements
- [ ] **Phase 6: Diff-Based Output** - Structured diff output returning only corrections instead of full rewritten text for maximum token reduction

## Phase Details

### Phase 1: Persistent Background Server
**Goal**: A basic always-running local server that maintains a warm connection to the OpenAI API, so the AHK script delegates requests through it instead of opening a new HTTP connection every invocation -- eliminating cold-start TLS/HTTP overhead
**Depends on**: Nothing (first phase)
**Requirements**: PERF-01
**Success Criteria** (what must be TRUE):
  1. A background server process starts automatically (or on first use) and stays running across spell-check invocations
  2. The AHK script sends spell-check requests to the local server instead of directly to the OpenAI API
  3. The second and subsequent spell checks in a session are measurably faster than a cold direct-to-API call (connection reuse eliminates TLS handshake)
  4. If the server is not running, the AHK script falls back to the current direct API path so spell checking never breaks
**Plans**: TBD

### Phase 2: Reliability and UX Foundation
**Goal**: Every hotkey invocation is correct, safe, and visible -- clipboard content is never lost, pastes never fail silently, modifier keys never stick, and the user always knows what is happening
**Depends on**: Phase 1
**Requirements**: REL-01, REL-02, REL-03, UX-01, UX-02, UX-03, UX-04, SEC-01, QUAL-01
**Success Criteria** (what must be TRUE):
  1. User's clipboard content is identical before and after a spell check invocation (ClipboardAll save/restore)
  2. Corrected text is pasted reliably every time without stale clipboard content appearing (no race condition)
  3. User sees a tooltip immediately after pressing the hotkey indicating processing is underway, and sees result status when complete
  4. Pressing the hotkey with no text selected shows a clear notification instead of silently hanging
  5. Pressing the hotkey twice rapidly does not cause double-paste or corrupted output
**Plans**: TBD

### Phase 3: Error Handling and Resilience
**Goal**: Transient API failures are handled automatically with clear user communication, and debug logging no longer pollutes the hot path
**Depends on**: Phase 2
**Requirements**: REL-04, REL-05, PERF-02
**Success Criteria** (what must be TRUE):
  1. A transient API failure (429, 500, 502, 503) is retried automatically and succeeds without user intervention when the service recovers
  2. When an API error is not recoverable, the user sees a readable tooltip message identifying the error type (network / auth / rate limit / server) instead of a raw status code
  3. Debug event logging can be toggled off, eliminating unnecessary string allocations during normal operation
**Plans**: TBD

### Phase 4: Connection and Latency Optimization
**Goal**: Optimize the persistent server from Phase 1 with speculative decoding for gpt-4.1, advanced connection tuning, and elimination of unnecessary AHK delays
**Depends on**: Phase 3, Phase 1
**Requirements**: PERF-03, PERF-05
**Success Criteria** (what must be TRUE):
  1. gpt-4.1 spell checks use Predicted Outputs via Chat Completions API, producing measurably faster responses for text with few corrections
  2. AHK internal delays (key delay, control delay) are set to minimum values and do not add latency to clipboard or paste operations
  3. Server connection pooling and keep-alive tuning produce measurably lower p50/p95 latency compared to the basic Phase 1 server
**Plans**: TBD

### Phase 5: Parsing, Replacements, and Tooling
**Goal**: The parsing layer uses no deprecated components, replacements require fewer manual variants, the AI has domain-specific vocabulary, and the log viewer detects stale data
**Depends on**: Phase 4
**Requirements**: QUAL-02, REPL-01, AI-01, LOG-01, LOG-02
**Success Criteria** (what must be TRUE):
  1. HTML clipboard content is stripped using a lightweight regex parser instead of the deprecated HTMLFile COM object, eliminating clipboard deadlock risk
  2. A single entry in replacements.json (e.g., "GitHub") matches all case variants without needing to list each one explicitly
  3. A word dictionary file provides domain-specific terms to the AI without bloating the main prompt
  4. Opening the log viewer HTML when data is outdated shows a visible warning with the generation timestamp and a way to regenerate
**Plans**: TBD
**UI hint**: yes

### Phase 6: Diff-Based Output
**Goal**: The spell checker can return only the specific corrections made, dramatically reducing output tokens and latency for text with few errors
**Depends on**: Phase 5
**Requirements**: PERF-04
**Success Criteria** (what must be TRUE):
  1. When diff mode is active, the API returns structured correction pairs instead of full rewritten text, and the original text is patched correctly
  2. The full-text output path remains available as a fallback when diff mode is disabled or produces unreliable results
**Plans**: TBD

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3 -> 4 -> 5 -> 6

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Persistent Background Server | 0/TBD | Not started | - |
| 2. Reliability and UX Foundation | 0/TBD | Not started | - |
| 3. Error Handling and Resilience | 0/TBD | Not started | - |
| 4. Connection and Latency Optimization | 0/TBD | Not started | - |
| 5. Parsing, Replacements, and Tooling | 0/TBD | Not started | - |
| 6. Diff-Based Output | 0/TBD | Not started | - |
