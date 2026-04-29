# Plan: Hot-Path Speed Optimization for Hotkey → Replace

## Context

The Windows tray app's core value is feeling instant: select text, press hotkey, corrected text replaces the selection. Today, `SpellcheckCoordinator.RunAsync` runs every step sequentially on the hot path — capture, network request, parse, post-process, paste-target check, paste, **and then** all logging serialization, JSONL write, and clipboard restore inline before returning. Settings (API key) and `replacements.json` are re-read each call. The HTTPS connection is cold on the first hotkey press. The loading overlay paints on the same WinForms UI thread that handles the rest of the app.

Goal: restructure so the hot path runs only what's strictly required to land the paste, capture every other piece of data into an immutable record, and finalize logging + cleanup after the paste fires. Plus startup pre-warm to remove cold-start cost from the first hotkey, and per-call micro-opts that don't change the request shape.

**Hard constraints (from brainstorm):**
- Do not change the OpenAI request body shape — match the AHK legacy exactly: `gpt-4.1`, `store=true`, `text.verbosity="medium"`, `temperature=0.3`, prompt instruction text from AHK.
- No streaming. The full response is awaited and pasted in one shot.
- The loading overlay shows instantly but on its own background UI thread.
- Every timing and log field the current `LogData` block emits must still be captured — fidelity is non-negotiable, only the *write* moves off the hot path.

## Architecture

**Two-phase split inside `SpellcheckCoordinator`:**

1. `ExecuteHotPathAsync(...)` — runs only what's required to land the paste. Builds an immutable `RunRecord` along the way. Returns the record.
2. `FinalizeAsync(RunRecord record)` — runs all logging serialization, JSONL write, clipboard restore, deferred replacements refresh, overlay hide.

`RunAsync` becomes:
```csharp
var record = await ExecuteHotPathAsync(...);
_ = Task.Run(() => FinalizeAsync(record));   // fire-and-forget, no await
```

The `RunRecord` is the structural enforcement: anything in the hot path must either do work for the paste, or stash data on the record. Failure paths (capture_failed, request_failed, paste_failed, run_failed) all build a record with a status and bail out the same way; finalize handles all of them uniformly.

## RunRecord Shape

Immutable record with these slots (every field maps to an existing `LogData` field):

- **Status:** `Status`, `ErrorCode`, `ErrorMessage`, `Events` (ordered list, mirrors the AHK events array)
- **Context:** `ActiveWindowAtStart`, `ActiveWindowAtPaste?`, `Model`, `PasteMethod="ctrl_v"`
- **Text:** `InputText?`, `OutputText?`, `RawAiOutput?`, `RawResponseBytes?` (byte buffer, not string), `RequestPayloadBytes?` (byte buffer), `TextChanged`
- **Counts:** `CopyAttempts`, `RequestAttempts`, `StatusCode?`
- **Timings (raw `Stopwatch.GetTimestamp()` ticks):** `T_HotkeyReceived`, `T_CaptureStart`, `T_CaptureEnd`, `T_RequestSendStart`, `T_RequestSendEnd`, `T_ResponseFirstByte`, `T_ResponseEnd`, `T_PostProcessStart`, `T_PostProcessEnd`, `T_PromptGuardStart`, `T_PromptGuardEnd`, `T_PasteTargetCheck`, `T_PasteIssued`, `T_PasteAck`, `T_HotPathReturned`. Conversion to ms happens in `FinalizeAsync`.
- **Tokens:** `TokenUsage` (input, output, total, cached, reasoning) — extracted via the parsed `JsonDocument`, not regex.
- **Post-process:** `ReplacementsApplied` (list reference from `TextPostProcessor`), `UrlsProtected`, `PromptLeak` (struct reference).
- **Cleanup:** `OriginalClipboard` (`IDataObject?`) captured before Ctrl+C, used by `FinalizeAsync` to restore.

**Capture rules:**
- Timings use `Stopwatch.GetTimestamp()` only — no `DateTime.UtcNow`, no string formatting on hot path.
- Text fields stored by reference. No copying, escaping, or JSON-encoding on hot path.
- Raw response is the unread byte buffer; `JsonDocument.ParseAsync(stream)` reads it once for output extraction.

## Hot-Path Order

Per the user's principle (mirror AHK ordering — fire request as early as possible, do other prep in parallel with it):

1. `T_HotkeyReceived` snapshot.
2. Capture `OriginalClipboard` backup.
3. `ActiveWindowInfo.Capture()` → `ActiveWindowAtStart`.
4. `BeginInvoke` show overlay onto overlay UI thread (sub-microsecond enqueue).
5. `ClipboardLoop.CaptureSelectionAsync()` → `InputText`. (`T_CaptureStart` / `T_CaptureEnd`.)
6. Build payload via pre-built UTF-8 prefix/suffix + JSON-escaped user text into pooled buffer.
7. Send HTTP request on warmed `HttpClient` (`T_RequestSendStart` → `T_ResponseEnd`).
8. `JsonDocument.ParseAsync(stream)` → extract `OutputText` and tokens in one walk.
9. `_postProcessor.Process(...)` (`T_PostProcessStart` / `T_PostProcessEnd`, plus `T_PromptGuardStart`/`End` if triggered).
10. `ActiveWindowInfo.Capture()` → `ActiveWindowAtPaste`. Compare to `ActiveWindowAtStart`.
11. `ClipboardLoop.ReplaceSelectionAsync(replacement)` (`T_PasteIssued` / `T_PasteAck`). Set clipboard + Ctrl+V only — **clipboard restore is removed from this step** and handled in finalize.
12. `T_HotPathReturned` snapshot. Hand `RunRecord` to `Task.Run(() => FinalizeAsync(record))` and return.

Failure branches (steps 5/7/10 fail) build a `RunRecord` with the appropriate status, fire the user-facing notify (synchronous, needed immediately), then hand off to finalize the same way.

## FinalizeAsync (off-thread)

Runs on a `Task.Run` task. Order doesn't matter for the user; do the cheap things first so the overlay disappears soon:

1. `BeginInvoke` hide overlay.
2. `ClipboardLoop.RestoreClipboard(record.OriginalClipboard)` — single point of restore for all paths (success + every failure branch).
3. Convert all timings (ticks → ms) using `Stopwatch.Frequency`.
4. Build the structured log object (mirrors current `_logger.LogData("spellcheck_detail", new { ... })` shape exactly).
5. Decode `RequestPayloadBytes` and `RawResponseBytes` to strings for the log record.
6. Write the human-readable `_logger.Log(...)` line and the structured `_logger.LogData(...)` line.
7. **Replacements refresh:** stat `replacements.json` (mtime + size); if changed, reload the in-memory cache and swap. No-op in steady state.
8. Catch and swallow exceptions inside finalize — log to a fallback diagnostic so a finalize crash never affects the next hotkey.

The semaphore gate (`_spellcheckGate`) is released *before* `Task.Run(FinalizeAsync)` is dispatched, so the next hotkey can fire while the previous run's finalize is still serializing logs.

## Startup Pre-Warm (one-time, app launch)

Done off the UI thread during tray app initialization:

1. **API key cache.** Add a `CachedSettings` service. Loads API key once at startup into a `volatile string?`. Reload only via event when the settings dialog saves. `OpenAiSpellcheckService` reads from this cache, never from disk.
2. **Replacements cache (AHK-style, simplified).** Call `LoadReplacements()` once at startup. Hot path uses in-memory pair list with zero file I/O. `FinalizeAsync` (post-paste, off-thread) does one mtime+size check and reloads if changed. Edge case acknowledged: the very first hotkey after a `replacements.json` edit uses the old cache; the second hotkey picks up the change. User accepts this — planned future storage migration will eliminate the gap.
3. **Pre-built payload buffers.** UTF-8 byte arrays for prefix and suffix:
   - Prefix: `{"model":"gpt-4.1","input":[{"role":"user","content":[{"type":"input_text","text":"instructions: Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text.\ntext input: `
   - Suffix: `"}]}],"store":true,"text":{"verbosity":"medium"},"temperature":0.3}`
4. **HttpClient via `SocketsHttpHandler`.** `PooledConnectionLifetime=10m`, `PooledConnectionIdleTimeout=5m`, `EnableMultipleHttp2Connections=true`, `AutomaticDecompression=All`. Request opts: `Version=Version20, VersionPolicy=RequestVersionOrHigher`.
5. **Connection warm.** Fire one throwaway HEAD/GET to `https://api.openai.com/v1/models` (no auth required for handshake; ignore response). Forces DNS + TCP + TLS + HTTP/2 negotiation; leaves a live socket in the pool.
6. **Re-warm timer.** Every 4 minutes, repeat the throwaway request off-thread. Prevents idle-closed sockets from biting the next hotkey.
7. **JIT-warm pass (optional, cheap).** Synthetic hot-path call at startup with a fake response (no network), to JIT-compile `ExecuteHotPathAsync` and the post-processor.

## Per-Call Request Changes (no body shape change)

In `OpenAiSpellcheckService`:

1. **Update `PromptInstruction`** to AHK text exactly: `"Fix the grammar and spelling of the text below. Preserve all formatting, line breaks, and special characters. Do not add or remove any content. Return only the corrected text."` (replaces current "Correct spelling and grammar..." text). This automatically updates the prompt-leak guard pattern in `TextPostProcessor` since it keys off the same constant.
2. **Keep `store=true`, `verbosity="medium"`, `temperature=0.3`.** Match AHK exactly.
3. **Payload construction:** write prefix bytes → JSON-escape user text directly to UTF-8 → write suffix bytes → `ByteArrayContent`. Skip `JsonSerializer.Serialize` of the anonymous object every call.
4. **Stream-parse the response.** `ReadAsStreamAsync` + `JsonDocument.ParseAsync(stream)`. Output text + token usage extracted in one walk.
5. **Drop regex token extraction.** Replace `ExtractInt`/5× `Regex.Match` with `JsonDocument` walk.
6. **Defer payload/response string materialization** to `FinalizeAsync`. Hot path holds byte buffer references on the record.

## Loading Overlay (background UI thread)

In `SpellCheckAppContext` / new `OverlayHost` class:

1. At startup, spin up one **STA-marked background thread** that owns its own `Application.Run` message loop and a single pre-constructed `LoadingOverlayForm`.
2. Show/hide via `BeginInvoke` from the hot path / finalize. Returns immediately.
3. The form is reused — never disposed and recreated per hotkey.
4. Bottom-center monitor calculation done cheaply on the hot path (one Win32 call) and passed in the show message.
5. Idempotent show/hide. Defense in depth even though `_spellcheckGate` already serializes.
6. Hide is queued from `FinalizeAsync` (first step of finalize), not the hot path. The hot path returns immediately after paste.

## Critical Files

To be modified:
- `src/SpellcheckCoordinator.cs` — split `RunAsync` into `ExecuteHotPathAsync` + `FinalizeAsync`, build `RunRecord`.
- `src/OpenAiSpellcheckService.cs` — new payload assembly, `SocketsHttpHandler` config, stream-parse, prompt instruction text update, `JsonDocument` token extraction.
- `src/SettingsStore.cs` (or new `CachedSettings.cs`) — in-memory API key cache with reload event.
- `src/TextPostProcessor.cs` — confirm `PromptInstruction` constant flows through to prompt-leak guard; pre-compile any regexes at construction.
- `src/LoadingOverlayForm.cs` + `src/SpellCheckAppContext.cs` — STA background-thread overlay host.
- `src/DiagnosticsLogger.cs` — confirm thread-safe append (already JSONL); finalize calls into it from `Task.Run` thread.
- `src/Program.cs` — wire startup pre-warm: `LoadReplacements()`, cached settings prime, `HttpClient` warm-up, re-warm timer.

To be added:
- `src/RunRecord.cs` — immutable record + nested types (`PromptLeakInfo` already exists).
- `src/OverlayHost.cs` (or similar) — owns the overlay STA thread.
- `src/ConnectionWarmer.cs` — the warm-on-startup + 4-minute re-warm timer.

To be read for reference (existing patterns to reuse):
- `src/ClipboardLoop.cs` — `CaptureSelectionAsync`, `ReplaceSelectionAsync`, `RestoreClipboard`. `ReplaceSelectionAsync` likely needs a variant that does set+paste only and does NOT restore (restore moves to finalize), or a parameter to skip restore.
- `src/ActiveWindowInfo.cs` — `Capture`, `HasSameProcess`.
- `src/AppPaths.cs` — log directory location for finalize JSONL writes.
- `.archive/ahk-legacy/Universal Spell Checker.ahk` — canonical reference for request shape (lines 32–62, 81, 1667–1677), replacements lifecycle (lines 514–654), prompt-leak guard text (line 81).

## Verification

End-to-end, on Dev channel (`Ctrl+Alt+D`, `dotnet run -c Dev`):

1. **Functional parity:** select text in Notepad, Word, browser, VS Code; press hotkey; confirm correct replacement. Test capture_failed (no selection), request_failed (invalid API key in settings), paste_failed (alt-tab during request).
2. **Log fidelity:** open `%LocalAppData%\UniversalSpellCheck\logs\spellcheck-{date}.jsonl`. Confirm every field present in the pre-change schema is still emitted: status, model, active_app, active_exe, paste_method, text_changed, input_text, input_chars, output_text, output_chars, raw_ai_output, raw_response, request_payload, tokens (all five), timings (all eight: clipboard_ms, payload_ms, request_ms, api_ms, parse_ms, replacements_ms, prompt_guard_ms, paste_ms, total_ms), replacements (count, applied, urls_protected), prompt_leak (all six), events. Compare a pre-change log line to a post-change one side by side.
3. **Timing improvement:** measure end-to-end latency (T_HotkeyReceived → T_PasteAck) over 20 runs cold and warm. Confirm cold-start (first hotkey after launch) is materially faster — primary target is removing TLS handshake from first run. Confirm steady-state hot-path duration drops by the cumulative ms previously spent on logging serialization, clipboard restore, settings disk read, and JSON serialization.
4. **Overlay behavior:** confirm overlay paints instantly on hotkey, never freezes the dashboard or settings window when they're open, and disappears reliably after every run including failure paths.
5. **Replacements cache:** edit `replacements.json`, hit hotkey once (should use old replacements per the accepted edge case), hit hotkey again (should use new replacements). Confirm finalize path swapped the cache.
6. **Re-warm timer:** leave the app idle for 10+ minutes, fire a hotkey, confirm latency is comparable to a hotkey fired 30s after launch (warm) — not the cold-start path.
7. **Side-by-side dev/prod:** confirm Prod (`Ctrl+Alt+U`) and Dev (`Ctrl+Alt+D`) instances both work concurrently, both write to the unified log directory, and entries carry the right `channel` stamp.
8. **Rebuild discipline:** every test cycle requires stop process → `dotnet build src/UniversalSpellCheck.csproj -c Dev` → `dotnet run -c Dev` (per CLAUDE.md hard rule #7).
