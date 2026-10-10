# Install local production after publishing

After the approved release workflow completes successfully and the release is
published, invoke the installed production executable with its native command:

```powershell
& "$env:LOCALAPPDATA/UniversalSpellCheck/current/UniversalSpellCheck.exe" --restart
```

Use the actual installed path with normal machine access; sandbox LocalAppData redirection is not the installation. On builds with `--restart`, it requests a graceful restart through Velopack; normal startup checks/downloads/installs the newest release automatically. It sends one progress notice; failure sends an additional notice and leaves the current version available. `--update` remains a compatibility alias. No repository script, GitHub CLI, Python, or UI automation is needed to trigger it.

For a running instance, exit 0 acknowledges the request, not installation completion. Exit 1 means delivery failed or Dev was invoked. Verify completion using the read-logs skill: `restart_command_received`, `restart_requested`, a fresh Prod `started`, and `update_check_done` for the new PID. When an update exists, also expect `update_download_done` and `update_apply_now` before the final restart. Verify the running process and installed DLL version match the expected release. A current app still restarts when requested. Poll in bounded intervals; after five minutes without convergence, inspect failure logs and report installation as incomplete.

Do not independently download releases, hash staged packages, stop/restart the
app from PowerShell, or invoke `Update.exe` during normal publishing. The app owns
that sequence. Complete closeout after verifying installation: leave Prod running,
stop Dev/helpers, and remove session scratch.

**Compatibility:** installed v0.11.0 supports only `--update`; use that alias to upgrade it to the restart-based flow. Installed versions through 0.10.2 do not recognize `--update`.
For the one release that introduces it, use the existing app's downloaded update
prompt (or the installed updater to apply its already-staged package) once. Never
pass `--update` to an older binary and claim it performed an update. Subsequent
publishes use only the native command above.
