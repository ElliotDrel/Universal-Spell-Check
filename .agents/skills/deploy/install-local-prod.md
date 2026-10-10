# Install local production after publishing

After the approved release workflow completes successfully and the release is
published, invoke the installed production executable with its native command:

```powershell
& "$env:LOCALAPPDATA/UniversalSpellCheck/current/UniversalSpellCheck.exe" --update
```

Use the actual installed path with normal machine access; sandbox LocalAppData
redirection is not the installation. The command asks the existing instance to
check/download through `UpdateService.CheckAsync(CommandLine)` and automatically
apply/restart through `ApplyUpdatesAndRestartAsync`. If no instance is running,
it starts the app with the same update intent. An already-current app remains
running. No repository script, GitHub CLI, Python, or UI automation is needed to
trigger the update; those remain deployment verification tools only.

For a running instance, exit 0 means the request was acknowledged, not that
installation has finished. Exit 1 means delivery failed or the Dev channel was
invoked. Observe completion through the shared logs with the read-logs skill:
`update_command_received`, `update_download_done`, `update_apply_now`, and a fresh
Prod `started` event at the expected release version. Verify the installed process
PID/path and binary version match that startup event. An up-to-date check requires
no restart. Poll in bounded intervals; after five minutes without convergence,
inspect update failure logs and report local installation as incomplete.

Do not independently download releases, hash staged packages, stop/restart the
app from PowerShell, or invoke `Update.exe` during normal publishing. The app owns
that sequence. Complete closeout after verifying installation: leave Prod running,
stop Dev/helpers, and remove session scratch.

**Bootstrap:** installed versions through 0.10.2 do not recognize `--update`.
For the one release that introduces it, use the existing app's downloaded update
prompt (or the installed updater to apply its already-staged package) once. Never
pass `--update` to an older binary and claim it performed an update. Subsequent
publishes use only the native command above.
