# Install local production after publishing

Publishing includes updating the installed production app on this computer.
After the approved release workflow completes successfully and the release is
public, run the programmatic installer:

```powershell
.agents/skills/deploy/scripts/install-local-prod.ps1 -ExpectedVersion 0.10.2
```

Substitute the approved release version. Use normal machine access; sandbox
LocalAppData redirection is not the installed app's location. Pass `-InstallRoot`
when the actual installation is elsewhere.

The command verifies the public release and successful workflow, identifies the
installed Prod process, and restarts it to invoke the existing
`UpdateService.CheckAsync(Launch)` download. It waits up to five minutes for the
staged full package and verifies its size/SHA256 against GitHub's release asset.
It then stops only that installed process, applies the staged package with the
installed `Update.exe apply --silent --package`, and verifies the restarted
binary/process and fresh Prod startup log through the read-logs skill.

This uses the app's existing downloader and Velopack staged-update mechanism;
it does not copy checkout builds into the install root, reinstall over user data,
or introduce a second download/feed path. An already-current installation is
verified and left running. A newer installed version is never downgraded.

The script waits for Update.exe itself, not its restarted app's entire process
tree. Keep giving progress updates while it runs. If it fails, inspect the app's
update/startup logs and report local installation as incomplete, even if the
public release succeeded. Do not delete staged packages or user data as cleanup.

For a separate read-only check, run:

```powershell
.agents/skills/deploy/scripts/verify-local-prod.ps1 -ExpectedVersion 0.10.2
```

After a restart, also supply `-StartedAfter` with its install start time and check
that the fresh Prod `started` event matches the version/PID. Complete publishing
closeout after installation: leave updated Prod running, stop Dev/helpers, and
remove only session-created scratch.
