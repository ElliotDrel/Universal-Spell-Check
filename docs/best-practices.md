# Best Practices

## Paste into the current app

A spell-check run pastes into whichever app is active when the corrected text is ready. Changing
windows, processes, or browser tabs after capture must not cancel the run.

- Freeze target-specific formatting selection at capture time so the request remains deterministic.
- Recapture the foreground context before the optional before-paste hook for telemetry and hook
  context, without treating a changed destination as an error.
- Keep protected-placeholder validation. Missing or duplicated placeholders indicate corrupted
  output and still abort the paste.
- Record both capture and paste targets in telemetry because they may intentionally differ.
