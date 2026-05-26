---
name: deploy
version: 1.0.0
description: >-
  Use when ready to push code to the Dev channel or release to Production.

  Triggers on: "push to dev", "deploy", "push to development", "ready to ship",
  "release to prod", "push to production", "publish a release", "ship it",
  "tag a release", "release v*.*.*", or any intent to get the current working
  code running in Dev or shipped to end users as a production release.

  Two paths: Dev (fast, no approval needed — push whenever changes are stable
  enough to test) and Prod (requires explicit human approval before tagging —
  a semver tag triggers a GitHub Release and is irreversible).
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

- **What it means:** Push a semver tag (`v*.*.*`) to origin. This triggers
  `.github/workflows/release.yml` — `dotnet publish` → `vpk pack` → `vpk upload github`
  → GitHub Release created. All installed prod copies auto-update via Velopack on
  next launch or 4-hour periodic check.
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
- Leaves a new semver tag and a running CI job after a prod deploy.
- Always reports back: what was pushed, the commit SHA, and next steps.

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
- The branch or tag pushed.
- The commit SHA that landed on origin.
- Next steps (Dev: how to pull and relaunch; Prod: CI run URL and auto-update timeline).
