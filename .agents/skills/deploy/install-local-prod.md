# Install local production after publishing

A request to publish includes updating the installed production app on the current
computer after the public release is ready. Keep installation inside the existing
`UpdateService.CheckAsync(UpdateTrigger)` / `ApplyUpdatesAndRestartAsync` flow.
Do not create a second downloader, copy build outputs into the installer root,
or reinstall over user data.

1. Verify the approved tag's release workflow finished and the release is published
   with its installer/package assets. Record the expected semantic version.
2. Identify the installed Prod process by its executable path under the actual
   Velopack installation, normally `%LocalAppData%/UniversalSpellCheck/current/`.
   Record its PID and version. Do not mistake a checkout/Dev process for Prod.
3. Load the computer-use skill and use its supported Windows UI tools to select
   the installed app's tray menu **Check for Updates** (or its dashboard update
   check). This invokes the existing `UpdateService.CheckAsync` entry point.
   If Prod is stopped, launch its installed executable first. Do not launch a
   second instance while it is already running.
4. Wait for `update_download_done version=<expected>` using the read-logs skill.
   The **Update ready** notification opens the existing update prompt when clicked.
   Verify that the prompt names the expected version, record the install start
   timestamp, and click **Install now**. This runs `ApplyUpdatesAndRestartAsync`.
   For an already-current installation, skip the install and verify it instead.
5. Wait for the old PID to exit and the installed app to restart. Poll in bounded
   intervals and give progress updates; after five minutes without convergence,
   inspect update failure logs and report local installation as incomplete.
6. Run the read-only verifier below with the expected version and, after an
   actual restart, the timestamp recorded before clicking **Install now**:

   ```powershell
   .agents/skills/deploy/scripts/verify-local-prod.ps1 -ExpectedVersion 0.10.2 -StartedAfter $installStartedAt
   ```

   The version is an example; substitute the approved release version. Pass
   `-InstallRoot` when the actual installation is elsewhere. The verifier requires
   one installed process and the matching installed/running binary version, plus
   a fresh process when `-StartedAfter` is supplied. Run with normal machine access;
   sandbox-redirection of LocalAppData or unavailable process access is not success.
7. Read the latest Prod `started` event through the read-logs skill and verify
   its version and PID match the verifier. Check update/startup failures for that
   update. Report the installed version and PID only after these checks pass.
8. Complete publishing closeout. Leave the updated installed Prod app running;
   stop Dev/test/helper processes and clean only session-created scratch.

If the desktop UI is unavailable, report that local installation is blocked while
preserving the published release. Do not claim CI success alone installed it.
