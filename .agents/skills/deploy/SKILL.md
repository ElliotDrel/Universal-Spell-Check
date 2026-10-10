---
name: deploy
description: 'Use when ready to push code to the Dev channel or release to Production.

  Triggers on: "push to dev", "deploy", "push to development", "ready to ship", "release
  to prod", "push to production", "publish a release", "ship it", "tag a release",
  "release v*.*.*", or any intent to get the current working code running in Dev or
  shipped to end users as a production release.

  Two paths: Dev (fast, no approval needed — push whenever changes are stable enough
  to test) and Prod (requires explicit human approval before tagging — a semver tag
  triggers a GitHub Release and is irreversible).'
metadata:
  version: 1.0.1
  triggers:
  - deploy
  - push to dev
  - push to development
  - ready to ship
  - release to prod
  - push to production
  - publish a release
  - ship it
  - tag a release
  - release v
  tools:
  - Bash
  - PowerShell
  - AskUserQuestion
  mutating: true
---

# Deploy — Overview

Two deployment paths. This file is the router — read it first, then follow it
to the right sub-file. **Never read `prod-deploy.md` unless the routing table
below explicitly directs you there AND the user's intent unambiguously names
shipping to production users.**

---

## The two paths

### Dev channel

- **What it means:** Push code to `origin/main`. The Dev channel is updated by
  doing `git pull` + `dotnet run -c Dev`. No versioning, no CI, no installer.
- **Hotkey when running:** Ctrl+Alt+D (orange-tinted tray icon)
- **Auto-update:** None — Dev never auto-updates. It's always a manual pull + relaunch.
- **Approval needed:** No. Push whenever changes are stable enough to test.
- **File to read next:** Read `dev-deploy.md` in full, then follow it.

### Production release

- **What it means:** Push a semver tag (`v*.*.*`) to origin, **along with the
  `main` branch** so `origin/main` is never left behind the released commit. The
  tag triggers `.github/workflows/release.yml` — `dotnet publish` → `vpk pack` →
  `vpk upload github` → GitHub Release created. All installed prod copies
  install updates via Velopack on startup/restart; the 4-hour periodic check prepares downloads for the next restart.
- **Hotkey when running:** Ctrl+Alt+U
- **Auto-update:** Yes — every installed copy picks up the release automatically.
- **Approval needed:** **YES. Hard stop. You must get explicit human confirmation
  via AskUserQuestion before pushing the tag.** A pushed tag starts the CI pipeline
  immediately and cannot be undone cleanly.
- **File to read next:** Read `prod-deploy.md` in full, then follow it.

---

## Routing decision

| User intent | Route to |
|---|---|
| Push current changes to dev / test on Dev channel | `dev-deploy.md` |
| Ship a release / publish to users / tag a version | `prod-deploy.md` |
| Unclear | Ask the user which channel before loading either sub-file |

Load only the sub-file you need. Do not pre-load both.

---

## Contract

- Routes to exactly one sub-file based on user intent. Never loads both.
- Never reads `prod-deploy.md` unless explicitly routing a production release.
- Never pushes a git tag without `AskUserQuestion` approval in the current session.
- Leaves the repo clean after a dev deploy.
- Leaves a new semver tag, the `main` branch pushed alongside it (`origin/main` not behind the released commit), a verified published release, and the local installed Prod app updated and running at that version.
- Always reports back: what was pushed, the commit SHA, and next steps.

---

## Required publishing closeout

Before ending any publishing turn, verify all three checks and report their actual status:

1. **All session changes are pushed.** Commit finished code and instruction/documentation changes, push the intended branch, and fetch/compare local HEAD with the remote branch. For production, also verify the approved tag and published release. If release approval is still pending, push the prepared commits through the Dev path without creating a production tag; report production as pending. A failed or blocked push must be reported as incomplete.
2. **The Dev app/server is fully stopped and cleared.** Shut down the development instance used for verification and any child processes, watchers, or helpers started for it. Verify its PID is gone and any session-owned listeners or PID files are cleared. This project uses a native Dev tray app, so check that executable rather than assuming a web server exists. Keep the installed Prod app running, except for the restart needed to install the newly published release.
3. **Session-created scratch and processes are cleaned up.** Remove disposable scratch/replay outputs and temporary build/check artifacts created for this work, stop remaining session-owned helpers, and archive a managed worktree once its changes are committed and pushed. Verify the final working tree and process state. Identify ownership first; preserve pre-existing files/processes, captured source evidence, retained project tooling, and backups (`backup`, `BEFORE-`, `.bak`) unless separately authorized.

Production publishing also requires updating the local installed Prod app after CI completes and the release is published; follow [Install local production](install-local-prod.md). The user's request to publish authorizes that local update and restart.

Run cleanup even when production approval or an external failure prevents publication; do not claim a pending release is published. Track temporary files and process identities while working so cleanup does not rely on broad deletion or process-name guesses.

---

## Anti-Patterns

- Loading both sub-files on ambiguous intent — ask the user instead.
- Treating a casual "yes" or "ship it" in the triggering message as prod approval.
- Pushing a dirty working tree under any circumstances, even if the user asks.
- Pushing a prod tag without a preceding `dotnet build -c Release` passing.
- Skipping the acceptance checklist because "the build passed."
- Pushing from any branch other than `main`.

---

## Output Format

On completion, report to the user:
- Which path was taken (Dev push or Prod release).
- The branch and/or tag pushed (a prod release pushes `main` + the tag together).
- The commit SHA that landed on origin.
- Closeout results: changes pushed, Dev stopped/cleared, and session scratch/processes cleaned up; name anything incomplete.
- Local production installation result, running version/PID, and CI/release URLs for production; Dev pull/relaunch instructions for a Dev push.
