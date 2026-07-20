# Plan / Spec: App- and Site-Specific Formatting Customizations

## Status

Phase 1 implemented on 2026-07-17. The shared two-hook pipeline, terminal-rule migration, stronger
desktop destination identity, protected before-paste hook path, telemetry, tests, and no-match
microbenchmark are in place. Phases 2 and 3 remain intentionally gated on the first named target and
its exact input/output examples; no Chrome bridge is built until a real URL-scoped rule requires it.

Implementation commit: `269ce7f feat: add target-specific formatting pipeline`.

## Next-Chat Handoff

### What exists now

Phase 1 is complete and committed. The production pipeline has one real rule,
`TerminalFormattingRule`, registered in the explicit ordered list owned by
`TargetFormattingPipeline`. No other app rule, site rule, Chrome extension, native-messaging host,
or browser-context cache exists yet.

The implemented runtime path is:

```text
capture PID/HWND/root-owner target identity
  -> back up clipboard
  -> copy selection
  -> exclude captured text from clipboard history
  -> resolve and freeze one formatting rule
  -> guarded after-copy hook
  -> protect literals
  -> AI request
  -> replacements/prompt guard/literal restoration
  -> recapture and validate target identity
  -> guarded before-paste hook with formatter-neutral literal protection
  -> write corrected clipboard text
  -> settle delay
  -> final target validation
  -> paste
  -> asynchronous formatting telemetry
```

The target-formatting framework is not a dynamic plugin system. Adding a target means adding one
small deterministic class and one explicit ordered-list entry, then extending the focused test
executable. Do not add target-specific branches to `SpellcheckCoordinator`.

### Implementation map

| Concern | Current source of truth |
|---|---|
| Target and optional browser snapshots, process/hostname match helpers | `src/TargetFormatting/TargetContext.cs` |
| Rule contract, result/failure shape, frozen match | `src/TargetFormatting/TargetFormattingRule.cs` |
| Ordered matching, hook guards, destination validation, before-paste literal safety | `src/TargetFormatting/TargetFormattingPipeline.cs` |
| Existing terminal transformation and counters | `src/TargetFormatting/TerminalFormattingRule.cs` |
| Foreground PID/HWND/root-owner capture | `src/ActiveWindowInfo.cs` |
| Four coordinator integration points and asynchronous telemetry | `src/SpellcheckCoordinator.cs` |
| App-lifetime pipeline construction | `src/SpellCheckAppContext.cs` |
| Standard and formatter-neutral literal placeholders | `src/ProtectedText.cs` |
| Formatting timestamps and per-run metadata | `src/RunRecord.cs` |
| Matching, precedence, hook, identity, literal, parity, and microbenchmark coverage | `tests/TargetFormattingTests/Program.cs` |
| E2E benchmark fields and comparison support | `bench/` |

### Proven behavior

Automated verification completed during Phase 1:

- Release product build passed.
- Release bench build passed.
- `TargetFormattingTests` passed matcher, precedence, hook-order, hook-failure, browser-freshness,
  destination-identity, literal-safety, placeholder-corruption, and terminal-parity coverage.
- The no-match resolver averaged 0.365 microseconds per call over 250,000 calls in the final run.
- `ProtectedTextTests` passed 100,000 extraction/restoration iterations.
- `--dashboard-smoke` and the Dev `--startup-smoke` passed.
- The relevant text-postprocessor Python regression tests passed.

Manual Dev verification used real Windows Terminal runs and the bundled `read-logs` skill:

- Five terminal spellchecks across 2026-07-18 and 2026-07-19 all succeeded with no Dev failure
  events.
- The terminal rule emitted all three stable operations: `normalize_double_break`,
  `normalize_list_item`, and `collapse_soft_wrap`.
- URLs and Windows paths survived normalization, AI processing, and restoration byte-for-byte.
- Clipboard-history exclusion, corrected-text clipboard state, and paste-back all succeeded.
- First matched formatting in two newly started Dev processes cost 183 ms and 91 ms respectively;
  subsequent warmed runs logged 0 ms. This is consistent with one-time compiled-regex/JIT startup
  cost. Logging rounds phase timings to whole milliseconds, so 0 ms means below that resolution.

The terminal integration proves the desktop executable matcher and after-copy production path. It
does not manually prove a before-paste transformation or any Chrome integration. Before-paste
behavior is currently proven by focused synthetic-rule tests, including protected-literal corruption
failure. Chrome remains intentionally unimplemented.

### Before writing the first new rule

Collect all six inputs. Do not infer any of them:

1. Desktop executable name or Chrome URL.
2. A real copied-text sample, including the unwanted artifacts.
3. The exact text that should reach the AI after `AfterCopy`.
4. A representative corrected AI output.
5. The exact text that should be pasted after `BeforePaste`.
6. Whether the behavior applies to the whole app/site or only one page/editor/path.

Then choose the smallest integration:

| Target can be identified by | Required work |
|---|---|
| Desktop executable | Add one app rule; no browser work |
| Chrome executable alone, with behavior valid on every site | Add one app rule; be explicit about the broad scope |
| Real hostname or hostname + path | Build Phase 3 first; never infer the site from a Chrome title |

Choose hooks from the transformation boundary, not convenience:

- Use `AfterCopy` only for artifacts in captured source text that should not reach the AI.
- Use `BeforePaste` only for destination syntax that should be added after AI correction.
- Use both only when the target genuinely requires independent input cleanup and output adaptation.
- Set `HasBeforePasteTransform` to `false` when the before-paste hook is inactive, so the run avoids
  the second literal-protection scan.

### Current rule contract

The live interface is:

```csharp
internal interface ITargetFormattingRule
{
    string Id { get; }
    TargetFormattingMatchType MatchType { get; }
    bool HasBeforePasteTransform { get; }
    bool Matches(TargetContext context);
    FormattingResult AfterCopy(string text, TargetContext context);
    FormattingResult BeforePaste(string text, TargetContext context);
}
```

Rules must remain synchronous, deterministic, side-effect-free, and free of captured text in
operation IDs or counters. They must not access files, clipboard, browser APIs, registry, network,
logging, or UI. Return `FormattingResult.NotApplied(text)` when unchanged so the original string
reference is retained.

Register rules in precedence order inside `TargetFormattingPipeline`:

1. Hostname + path site rules.
2. Hostname-only site rules.
3. Desktop executable rules.
4. Existing terminal rule wherever its executable precedence is intended.

If any site rule is registered, the pipeline constructor requires an explicit browser freshness
limit. Do not invent that duration: measure extension-to-tray propagation during Phase 3 and pass the
measured bound.

### Required first-rule tests

Add focused fixtures before implementation, then keep them beside the existing
`TargetFormattingTests` cases:

- Exact positive examples for every active hook.
- Negative example for a different executable/hostname/path.
- Case-insensitive executable matching or hostname label-boundary coverage as applicable.
- Precedence coverage for every overlap introduced by the new ordered-list entry.
- Unchanged input retains the original string reference.
- Stable operation IDs and exact character/counter telemetry.
- URL, Windows path, token, UUID, and opaque-ID preservation.
- Hook exception retains the unmodified text.
- Before-paste missing/duplicated placeholders abort safely when that hook is active.
- Destination change prevents paste.

### Verification gate for each new target

Run, in order:

```powershell
dotnet run --project tests/TargetFormattingTests/UniversalSpellCheck.TargetFormattingTests.csproj -c Release
dotnet run --project tests/ProtectedTextTests/UniversalSpellCheck.ProtectedTextTests.csproj -c Release
dotnet build src/UniversalSpellCheck.csproj -c Release
dotnet build bench/UniversalSpellCheck.Bench.csproj -c Release
dotnet run --project src/UniversalSpellCheck.csproj -c Release -- --dashboard-smoke
```

For startup smoke, stop an existing instance of the channel whose hotkey would collide, then run the
built executable with `--startup-smoke`. For manual acceptance, rebuild and relaunch Dev, test a
positive target example and an unmatched control app, then inspect the Dev logs only through the
`read-logs` skill. Require:

- Correct rule ID and match type.
- Exact expected normalized/request/pasted text.
- Expected stable operations and counters.
- Protected literals restored byte-for-byte.
- No hook failure, paste failure, or target-identity failure.
- No measurable ordinary-target regression; a repeatable E2E regression at or above 5% blocks the
  rule.

### Copy-paste starter for the next chat

Replace every bracketed field before sending this request:

```text
Implement the first real target-formatting rule using
.planning/app-site-formatting-customizations.md as the authoritative handoff. Read the repo routing
docs and current implementation before editing. Preserve the Phase 1 architecture: one isolated
rule, one explicit ordered-list entry, no target-specific coordinator branches, deterministic hooks,
literal safety, target identity validation, telemetry, focused tests, and manual Dev verification.

Target: [desktop executable or exact Chrome URL]
Scope: [whole app/site or exact editor/path]
Copied input: [verbatim sample]
Expected text sent to the AI: [verbatim sample]
Representative corrected AI output: [verbatim sample]
Expected pasted output: [verbatim sample]

Decide whether the rule belongs in AfterCopy, BeforePaste, or both from those examples. If the target
requires real hostname/path matching, implement the documented Chrome bridge first; otherwise do not
add browser integration. Finish when focused tests, Release/bench builds, smoke checks, real Dev
acceptance, log verification, and latency comparison pass. Keep unrelated files untouched.
```

---

## Goal

Add modular formatting customizations to Universal Spell Check without adding measurable latency
to ordinary spell-check runs.

Each matched target may transform text at two points:

1. **After copy**: clean target-specific artifacts from the captured selection before literal
   protection and the AI request.
2. **Before paste**: adapt the corrected output to the destination immediately before it is written
   to the clipboard and pasted.

Targets may be:

- A Windows desktop app, identified by executable and active window.
- A website in Google Chrome, identified by the active tab's real URL.
- A specific section of a website, identified by hostname plus path.

Adding a new target should require one rule file, one ordered-list entry, and focused tests. It must
not require another conditional branch in `SpellcheckCoordinator`.

---

## Non-Goals

- Preserving or reconstructing arbitrary rich-text clipboard formats in the first version.
- Injecting scripts into website pages or modifying website DOM content directly.
- Guessing the active website from a Chrome window title.
- Querying Chrome, UI Automation, the filesystem, or the network during a hotkey run.
- A user-facing rule editor or dynamically loaded third-party plugins.
- Changing the AI prompt or model per target in the first version.
- Applying target formatting to headless benchmark runs unless a synthetic target context is
  explicitly supplied by a test.

---

## Required Pipeline Order

```text
Hotkey received
  -> capture original Windows target context
  -> back up clipboard
  -> Ctrl+C and read selected text
  -> exclude captured incorrect text from clipboard history
  -> resolve and freeze matching formatting rule
  -> AFTER-COPY hook
  -> protect literals
  -> AI request
  -> existing replacements and prompt-leak cleanup
  -> restore protected literals
  -> recapture and validate destination context
  -> BEFORE-PASTE hook
  -> write final text to clipboard
  -> existing clipboard-settle delay
  -> final destination validation
  -> Ctrl+V
  -> asynchronous logging/finalization
```

The clipboard-history exclusion stays immediately after capture because it is timing-sensitive.
Target-specific cleanup begins only after that step.

The rule selected after copy is frozen in the run record. Before paste, the coordinator recaptures
the live target and re-evaluates that frozen rule; it does not select a different rule mid-run. The
live context must still represent the same destination and rule. A rule chosen for one Chrome site
must never be applied after the user switches to another tab or site.

---

## Core Design

### Directory

The implemented Phase 1 directory contains:

```text
src/TargetFormatting/
|-- TargetContext.cs
|-- TargetFormattingRule.cs
|-- TargetFormattingPipeline.cs
`-- TerminalFormattingRule.cs
```

Future rule examples:

```text
TargetFormatting/SlackFormattingRule.cs
TargetFormatting/GoogleDocsFormattingRule.cs
TargetFormatting/ClaudeFormattingRule.cs
```

### `TargetContext`

Immutable snapshot containing only already-available or pre-cached information:

```csharp
internal sealed record TargetContext(
    string ProcessName,
    int ProcessId,
    IntPtr WindowHandle,
    IntPtr RootOwnerWindowHandle,
    string WindowTitle,
    BrowserTargetContext? Browser);

internal sealed record BrowserTargetContext(
    string Browser,
    bool Focused,
    int WindowId,
    int TabId,
    string Scheme,
    string Host,
    string Path,
    long ReceivedAtStopwatchTicks,
    long ExtensionObservedAtUnixMs);
```

Override `BrowserTargetContext.ToString()` so diagnostics can emit only the hostname and matched rule
ID or coarse path category. It must never render the raw path held in memory.

`ActiveWindowInfo.Capture()` will retain its current cheap Win32 lookup and add process ID and the
foreground window handle. If the process is Chrome, `TargetContext` attaches the latest valid
in-memory browser snapshot. It performs no synchronous browser request.

`ReceivedAtStopwatchTicks` is stamped by the tray process on native-message receipt and is the only
value used for the initial freshness gate. The extension-provided wall-clock timestamp is retained
for diagnostics only, so an NTP or manual wall-clock adjustment cannot make a stale context eligible.

### `TargetFormattingRule`

Rules are small, deterministic classes:

```csharp
internal interface ITargetFormattingRule
{
    string Id { get; }
    TargetFormattingMatchType MatchType { get; }
    bool HasBeforePasteTransform { get; }
    bool Matches(TargetContext context);
    FormattingResult AfterCopy(string text, TargetContext context);
    FormattingResult BeforePaste(string text, TargetContext context);
}
```

Rules:

- Hooks must be synchronous and deterministic.
- Hooks may not perform file, clipboard, browser, network, registry, logging, or UI operations.
- A hook that has nothing to change returns `FormattingResult.NotApplied(text)`.
- An unchanged result must retain the original string reference when practical.
- Rules own transformation logic only. They do not own capture, target validation, literal
  protection, clipboard writes, or paste behavior.

### `FormattingResult`

```csharp
internal sealed record FormattingResult(
    string Text,
    bool Applied,
    int CharsAdded,
    int CharsRemoved,
    IReadOnlyList<string> Operations,
    IReadOnlyDictionary<string, int>? Counters = null,
    string? FailureCode = null,
    string? FailureType = null,
    bool AbortPaste = false);
```

`Operations` contains stable identifiers such as `collapse_soft_wrap` or
`normalize_markdown_breaks`, never captured user text. `Counters` preserves rule-specific numeric
telemetry such as the existing terminal normalizer's three per-pass counts without adding fields to
the common result for every future rule.

### Matching

`TargetFormattingPipeline` owns one explicit ordered list of rules constructed at startup. It uses a
linear scan. The expected rule count is tiny, so dictionary indexes, reflection, assembly scanning,
duplicate-matcher validation, and a separate registry type are deferred until measured need exists.

The current version contains only the terminal rule. Add named app/site rules individually; do not
build a plugin platform before measured need exists.

Precedence:

1. Browser hostname plus path rule.
2. Browser hostname rule.
3. Desktop executable rule.
4. No rule.

Only one rule may own a run in version one. Ordering is explicit in the short list, and tests cover
the precedence of every overlap that actually exists.

Hostname matching must use parsed, normalized hostnames and label boundaries. A rule for
`docs.google.com` may match exactly that host or an explicitly permitted subdomain, but never
`docs.google.com.example.com`.

### Pipeline service

`TargetFormattingPipeline` owns matching and guarded execution:

```csharp
FormattingMatch? Resolve(TargetContext context);
FormattingResult ApplyAfterCopy(FormattingMatch match, string text);
FormattingResult ApplyBeforePaste(FormattingMatch match, string text, TargetContext liveContext);
```

If a hook unexpectedly throws, log it during finalization and retain the unmodified text. Formatting
cleanup is optional and must not turn a valid generic spell-check result into a failed run.

Destination mismatch is different: if the foreground window or Chrome tab/site changed, abort the
paste and restore the original clipboard using the existing failure path.

---

## Existing Terminal Behavior

Phase 1 moved the behavior of the former `TerminalInputNormalizer` into
`TerminalFormattingRule`; the old file no longer exists. Its process list, transformation order,
output, and telemetry were preserved.

This migration proves the framework against a real customization and prevents two parallel
app-specific systems from existing in `SpellcheckCoordinator`.

The migration must pass parity fixtures containing:

- Double CRLF paragraph breaks.
- Bulleted and numbered list items.
- Soft-wrapped terminal lines.
- Bare CRLF that should remain unchanged.
- Non-terminal process names.
- URLs and paths near wrapped text.

---

## Chrome Site Identification

### Decision

Build the Chrome bridge only when the first named site rule is specified and its matcher requires the
real URL. Do not implement it merely to prepare for hypothetical rules. When required, use a minimal
Manifest V3 Chrome extension plus Chrome Native Messaging. The extension pushes active tab metadata
in the background; the tray app reads only an in-memory snapshot during the hotkey.

Do not use synchronous UI Automation against Chrome's address bar. It would introduce variable
latency and rely on browser UI structure. Do not infer sites from window titles.

Official references:

- Chrome Tabs API: <https://developer.chrome.com/docs/extensions/reference/api/tabs>
- Chrome Native Messaging: <https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging>
- Extension service-worker lifecycle: <https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle>

### Extension files

```text
integrations/chrome/
|-- manifest.json
`-- service-worker.js
```

Minimum permissions:

- `tabs`, to read the active tab URL.
- `nativeMessaging`, to send context to the installed Windows app.

Pin the extension ID before registering a native host. For local development, commit a stable public
manifest `key` and record the resulting extension ID in `BuildChannel`; if the extension is later
published, replace that value with the stable store-issued ID in the same constants location. Both
channel manifests use the resulting single `chrome-extension://<id>` origin in `allowed_origins`.

The extension listens to:

- `chrome.tabs.onActivated`
- `chrome.tabs.onUpdated`
- `chrome.tabs.onRemoved`
- `chrome.windows.onFocusChanged`
- `chrome.runtime.onStartup`

On each relevant event it sends a compact message:

```json
{
  "version": 1,
  "browser": "chrome",
  "focused": true,
  "windowId": 14,
  "tabId": 231,
  "url": "https://docs.google.com/document/d/example/edit",
  "observedAtUnixMs": 1784300000000
}
```

The extension sends no page contents, selected text, cookies, document HTML, or browsing history.

Use `runtime.connectNative()` as a long-lived connection. Reconnect from `onDisconnect` with bounded
backoff so restarting or updating the app does not permanently disable site detection.

### Native side

Add:

```text
src/ChromeIntegration/
|-- ChromeContextCache.cs
|-- ChromeContextMessage.cs
|-- ChromeNativeHostMode.cs
`-- ChromeIntegrationRegistration.cs
```

The installed Universal Spell Check executable supports a special native-host mode. Immediately after
the required first-line Velopack bootstrap in `Program.Main`, detect that mode from Chrome's
`chrome-extension://...` origin argument, rather than expecting an arbitrary mode flag in the
native-host manifest. The helper branch must run before `AppPaths.EnsureDataMigration()`, the
single-instance mutex, tray construction, logging, and UI setup; it only validates stdio messages,
forwards them to the existing tray process over a named pipe, and exits. Confirm the exact Chrome
launch arguments against the cited Native Messaging documentation during Phase 3. This avoids a
second custom executable while accepting the helper process required by Chrome's Native Messaging
protocol.

The tray process owns `ChromeContextCache`. Updates happen off the hot path and publish immutable
snapshots atomically. URL parsing and normalization happen when messages arrive, not when the hotkey
is pressed.

Each channel's named-pipe server is restricted with `PipeSecurity` to the current Windows user SID
with read/write access only. The bridge must reject any connection outside that ACL before parsing a
message. This prevents another local account from injecting a spoofed browser context.

Native-host manifest registration:

- Store the generated manifest in the channel's durable app-data directory.
- Register it under HKCU, never HKLM, so installation requires no elevation.
- Refresh the executable path at app startup because Velopack updates may move the installed binary.
- Keep channel-specific host name, registry key, pipe name, and extension origin in canonical
  constants rather than scattering strings through call sites.

Dev and Prod have distinct native-host names, manifests, registry keys, and named-pipe endpoints. The
extension attempts both host names and sends the same compact context message to each connected
channel. An unavailable channel is ignored. This prevents last-writer-wins registry clobbering while
allowing both app channels to run side by side.

### Cache validity

A Chrome site rule is eligible after copy only when:

- The foreground executable is Chrome.
- The cache says a Chrome window is focused.
- The snapshot's tray-stamped monotonic receive time is within a short, measured freshness limit at
  initial resolution.
- The URL is an allowed `http` or `https` URL.

Before paste, do not require a new wall-clock-fresh event. A normal multi-second AI request may
produce no tab event while the user remains on the same page. Instead, read the latest cached
snapshot and require it to remain focused with the same Chrome window ID, tab ID, and matching rule
identity. If a newer event exists, it must still describe that same destination. Initial freshness
answers "was this context trustworthy when the run began"; paste-time identity answers "did the
destination change during the run."

If initial resolution fails, skip site-specific formatting and continue with ordinary spellchecking.
If a site rule was already applied after copy and paste-time identity changes, abort the paste rather
than asymmetrically dropping the before-paste hook. Never guess. Log a small reason code
asynchronously, such as `missing`, `stale_at_start`, `not_focused`, `identity_changed`, or
`unsupported_url`.

---

## Coordinator Integration

Update `SpellCheckAppContext` to construct one `TargetFormattingPipeline` for the app lifetime and
inject it into `SpellcheckCoordinator`. Add `ChromeContextCache` only when a named Chrome site rule
requires it.

Update `SpellcheckCoordinator.ExecuteHotPathAsync` in four narrow locations:

1. Capture the richer starting `TargetContext` before Ctrl+C.
2. After successful capture and clipboard-history exclusion, resolve the rule and run
   `AfterCopy`.
3. After the existing `TextPostProcessor.Process`, recapture the destination and run target
   validation plus `BeforePaste`.
4. After the clipboard-settle delay, perform one final cheap destination validation before Ctrl+V.

Do not add app/site conditionals directly to the coordinator.

### Target identity

Desktop validation requires the same process ID and root-owner window. Resolve both captured HWNDs
through `GetAncestor(hwnd, GA_ROOTOWNER)` before comparing them so IME, autocomplete, and transient
owned windows do not cause false target-change failures. Browser validation additionally requires
the same matched rule, Chrome window ID, and active tab ID. A browser navigation that stays inside
the same rule's allowed hostname/path may remain valid; switching to a different rule or unmatched
site must abort.

This intentionally strengthens the current process-name-only check, which cannot distinguish two
windows or tabs owned by the same executable.

---

## Literal Safety

The after-copy hook runs before `ProtectedText.Protect`, matching the existing terminal-normalization
order. Every profile needs fixtures proving that its input cleanup does not corrupt URLs, paths,
tokens, UUIDs, or opaque IDs.

The before-paste hook runs after the existing literal restoration. To prevent destination formatting
from altering protected values:

1. Only invoke this extra protection path for a matched rule with a before-paste transformation.
2. Protect the post-processed output again using formatter-neutral placeholders, not the existing
   underscore-delimited `__USC_LITERAL_...__` form. Use collision-checked private-use Unicode
   delimiters that Markdown cleanup cannot interpret as emphasis or syntax.
3. Apply the rule hook to the placeholder-bearing text.
4. Restore literals and require the same missing/duplicate-placeholder safety check.

Unmatched runs do not pay for this second protection pass.

---

## Run Record and Logging

Add raw `Stopwatch` timestamps to `RunRecord`:

- `T_AfterCopyFormatStart`
- `T_AfterCopyFormatEnd`
- `T_BeforePasteFormatStart`
- `T_BeforePasteFormatEnd`

Add formatting metadata:

- Rule ID or empty string.
- Match type: `app`, `site`, or `none`.
- Browser context state/freshness reason.
- Whether each hook applied.
- Character additions/removals.
- Stable operation identifiers.
- Hostname and rule ID only. Never log a raw browser path, query, fragment, document ID, or page
  title. If a rule needs path diagnostics, it declares a fixed coarse category such as `document` or
  `compose`; the matcher may inspect the path in memory but logs only that allow-listed token.

Add timings to `spellcheck_detail.timings`:

- `after_copy_format_ms`
- `before_paste_format_ms`

Add a sibling `target_formatting` JSON object. Do not log extension messages on every background tab
event; log connection state changes and per-run resolution results only. All serialization remains in
the existing asynchronous finalization path.

During the terminal migration, retain the existing `terminal_normalization` JSON object and its
`passes.double_break_count`, `passes.list_item_count`, and `passes.soft_wrap_count` fields unchanged.
`target_formatting` is additive telemetry, not a schema replacement.

---

## Latency Contract

For an unmatched desktop app, the added hot-path work is limited to:

- Capturing process ID and window handle as part of the existing foreground-window lookup.
- A linear scan of the short explicit rule list.
- A null/rule branch.

It must perform:

- No filesystem access.
- No registry access.
- No browser or extension call.
- No URL parsing.
- No logging serialization.
- No reflection or profile discovery.
- No additional literal-protection scan.

Matched rules pay only for their own deterministic string transformation. Chrome URL parsing and
cache publication happen on background message receipt.

Acceptance gate:

- The no-match formatting resolver/hook path must remain sub-millisecond in a focused local
  microbenchmark.
- Existing E2E benchmark comparison must show no measurable ordinary-target regression. The repo's
  current benchmark treats changes below 5% as noise; any repeatable regression at or above 5% blocks
  the feature.
- Formatting timings must be visible separately so they cannot hide inside request or paste time.

---

## Failure Behavior

| Condition | Behavior |
|---|---|
| No rule matches | Continue existing pipeline unchanged |
| Chrome extension unavailable | Skip site customization; continue generic spellcheck |
| Browser context missing/stale at initial resolution | Skip site customization; continue generic spellcheck |
| Browser snapshot ages during an unchanged AI request | Keep the frozen rule; validate latest identity, not timestamp |
| Formatting hook throws | Keep input text unchanged; record failure asynchronously |
| Before-paste literal restore fails | Abort paste using protected-text failure behavior |
| Window/tab/site changes during request | Abort paste and restore original clipboard |
| Native messaging disconnects | Reconnect in background; never delay hotkey |
| Malformed native message | Reject message, preserve last valid cache snapshot, log bounded diagnostic |
| Chrome tab switch event is still propagating at paste time | Accept the bounded same-window formatting race; do not add a synchronous browser round trip that violates the latency contract |

Diagnostics and background integrations must never crash the tray app or block the UI thread.

---

## Testing Plan

### Unit tests

Add a focused C# console test project following the existing test-project pattern.

Cover:

1. App executable matching is case-insensitive.
2. Hostname matching respects label boundaries.
3. Path-specific site rule wins over host-only rule.
4. Site rule wins over browser executable rule.
5. No-match returns unchanged text and no operations.
6. After-copy and before-paste hooks execute in the required order.
7. Each hook can be independently inactive.
8. A thrown hook retains unchanged text.
9. Missing/stale Chrome context never triggers a site rule.
10. Switching tabs or rules before paste fails target validation.
11. Navigating within an allowed path remains valid when the rule permits it.
12. Protected literals survive before-paste formatting byte-for-byte.
13. Duplicate/missing protected placeholders abort before paste.
14. Terminal rule output matches the current normalizer fixtures.

### Chrome integration tests

- Validate every message field and maximum length.
- Reject non-HTTP(S) URLs for matching.
- Verify query strings and fragments are discarded before storage/logging.
- Verify atomic cache reads while messages update concurrently.
- Verify disconnect/reconnect behavior.
- Verify Dev and Prod endpoints can coexist.

### Regression tests

- Notepad multiline input remains unchanged except for AI corrections.
- Ordinary Chrome textarea on an unmatched site behaves exactly as before.
- No selection still produces no paste.
- Rapid double-hotkey still rejects the second run.
- Clipboard-history exclusion still occurs before formatting work.
- Corrected text remains on the clipboard after success.
- Failed runs restore the original clipboard on the STA thread.

### Performance tests

1. Capture the existing E2E baseline before implementation.
2. Add a local resolver microbenchmark for no match, app match, and site match.
3. Run E2E after the core framework with no additional target rule enabled.
4. Run E2E after Chrome integration is connected.
5. Compare median and p95 phase timings.
6. Run correctness checks against every result file.

---

## Implementation Sequence

### Phase 1: Minimal two-hook seam plus terminal rule (implemented)

- Add `TargetContext`, the rule/result contract, and the pipeline with one short ordered list.
- Move `TerminalInputNormalizer` behavior into `TerminalFormattingRule` as the first real rule instead
  of building an empty framework or retaining a parallel coordinator path.
- Preserve its output and all existing per-pass telemetry through `FormattingResult.Counters`.
- Enrich `ActiveWindowInfo` with PID, window handle, and root-owner resolution.
- Integrate both hooks into the coordinator.
- Add run-record fields and asynchronous telemetry.
- Add matcher, hook-order, focus-validation, and literal-safety tests.
- Add the no-match microbenchmark.
- Update replacements/logging and architecture docs to describe the generalized hooks.

Exit condition: terminal behavior remains byte-for-byte and telemetry-equivalent, ordinary app
behavior is unchanged, and the performance gate is green.

### Phase 2: First named app/site rules (deferred pending target inputs)

For each requested target:

1. Write exact input/output examples before implementation.
2. State whether the rule belongs after copy, before paste, or both.
3. Add one isolated rule file and one entry in the ordered list.
4. Add positive, negative, boundary, counter, and literal-safety fixtures.
5. Test the real app/site manually.
6. Compare formatting timing and total E2E timing.

If a site rule can be reliably scoped without a URL, do not add browser integration. Never guess from
a title. If it genuinely requires hostname/path matching, proceed to Phase 3 before enabling it.

Do not bundle unrelated targets into one transformation file.

### Phase 3: Chrome context bridge, only when required (not implemented)

- Add the Manifest V3 extension.
- Pin and verify the extension ID before writing either native-host manifest.
- Add native-host mode after Velopack bootstrap but before migration/mutex, HKCU registration,
  current-user-SID-restricted named-pipe forwarding, and in-memory cache.
- Add per-channel host registration, initial-freshness, identity, and message-validation behavior.
- Add Dev diagnostics for connected/disconnected/stale state.
- Verify Chrome integration adds no synchronous hotkey work.

Exit condition: a Dev run reliably matches the actual active host/path in memory and validates tab
identity without logging raw paths/page content or delaying capture.

### Phase 4: Documentation and release readiness (partially complete)

- Update `docs/architecture.md` with the final pipeline and Chrome bridge.
- Update `docs/replacements-and-logging.md` with hook semantics and JSONL fields.
- Add Chrome extension/native-host failure cases to `docs/watchlist.md`.
- Update `src/CLAUDE.md` with manual acceptance checks.
- Add a routing row to root `CLAUDE.md` if a permanent focused subsystem doc is created.
- Run Release build, product tests, startup smoke, E2E benchmark, and manual Dev acceptance.

---

## Definition of Done

Current status is intentionally split because the original definition spans conditional future work:

| Requirement | Status |
|---|---|
| Shared after-copy/before-paste framework | Implemented |
| Explicit ordered rule list | Implemented with terminal rule only |
| No target branches in coordinator | Implemented |
| PID + root-owner desktop destination validation | Implemented and unit tested |
| Before-paste formatter-neutral literal protection | Implemented and unit tested with synthetic rules |
| Terminal migration and parity telemetry | Implemented, automated and manually verified |
| Per-hook telemetry and benchmark fields | Implemented |
| No-match microbenchmark | Implemented and green |
| First named non-terminal app/site rule | Deferred until target inputs are supplied |
| Chrome extension/native-host/cache | Deferred until a URL-scoped rule requires it |
| Manual before-paste rule acceptance | Deferred until the first such rule exists |
| Post-change E2E comparison for a named target | Deferred until that rule is implemented |

- Both formatting hooks exist at the specified pipeline boundaries.
- App and Chrome-site rules use one common short ordered list and pipeline.
- No app/site conditional logic lives in `SpellcheckCoordinator`.
- Chrome rules that need URL scoping use the real active URL from an extension-fed in-memory cache.
- No browser, disk, network, registry, reflection, or logging work occurs synchronously on the
  ordinary hotkey path.
- Switching the destination window, Chrome tab, or formatting rule prevents a wrong-target paste.
- Existing terminal normalization is represented as a rule with parity coverage.
- Protected literals survive both hooks byte-for-byte.
- Missing browser integration degrades to normal generic spellchecking.
- Per-hook telemetry proves the formatting cost independently.
- No-match microbenchmark and E2E latency gates pass.
- Notepad and unmatched Chrome textarea baseline behavior remains intact.
- Adding a new target requires only one rule file, one ordered-list entry, tests, and its manual
  acceptance case.

---

## Inputs Needed Before Phase 2

For each first customization, collect:

- Desktop app executable or Chrome site URL.
- A real copied-text example.
- The exact text that should be sent to the AI after the after-copy hook.
- A real corrected-output example.
- The exact text that should be pasted after the before-paste hook.
- Whether the rule applies to the whole app/site or only a particular page/editor.

These inputs determine whether Chrome integration is needed at all. They do not change the minimal
two-hook seam.
