# Prod Deploy

Ship a production release by pushing a semver tag to origin. This triggers
`.github/workflows/release.yml` → `dotnet publish` → `vpk pack` → `vpk upload github`
→ GitHub Release created. All installed prod copies auto-update via Velopack.

For pipeline details, read `.github/workflows/release.yml`. For version constants
and channel configuration, read `src/BuildChannel.cs`.

**This is irreversible.** A pushed tag immediately starts the CI pipeline and
creates a public GitHub Release. Do not proceed past Step 4 without explicit
human approval delivered via `AskUserQuestion` in this session.

---

## Step 1 — Verify the branch is clean and up-to-date

```powershell
git status
git branch --show-current
git fetch origin
git status
```

- Must be on branch `main`. If not, stop and ask the user before proceeding.
- No uncommitted changes. If there are any, stop — use the `commit` skill and
  push to dev first, then return here.
- Must be up to date with `origin/main` (no commits behind).

---

## Step 2 — Build check (Release config)

Before determining a version or seeking approval, confirm the code compiles in
Release config:

```powershell
dotnet build src/UniversalSpellCheck.csproj -c Release
```

If the build fails, stop immediately. Do not propose a version. Do not proceed.
Fix the build error first.

---

## Step 3 — Determine the next version

Read the last tag and recent commit messages to propose the next semver:

```powershell
git tag --sort=-version:refname | Select-Object -First 5
git log --oneline $(git describe --tags --abbrev=0)..HEAD
```

Apply standard semver rules:
- **Patch** (`x.y.Z+1`): bug fixes, small corrections, no new behavior.
- **Minor** (`x.Y+1.0`): new user-visible features, backwards-compatible.
- **Major** (`X+1.0.0`): breaking changes or significant rewrites.

The proposed tag must be strictly higher than the current latest tag. Do not
propose a version equal to or lower than the last tag.

---

## Step 4 — Get explicit human approval (HARD STOP)

**The message that triggered this skill does not constitute approval — even if
the user said "ship it", "yes", or "go ahead" before this step. You must stop
here and ask via `AskUserQuestion` regardless of what was said earlier.**

Show the user the exact proposed tag and the commits it will include (paste the
one-line log output from Step 3). Then use `AskUserQuestion` to ask:

> "Ready to release **vX.Y.Z** to production?
>
> Commits included: [paste git log output]
>
> This will push tag `vX.Y.Z` to origin, trigger GitHub Actions (release.yml),
> build a Velopack installer, create a public GitHub Release, and push the update
> to all installed users automatically. There is no undo."

Provide **Yes** / **No** options only. If the user says No, stop completely.
Do not tag anything. Do not push anything.

Approval from a prior conversation turn, a prior session, or any message before
this `AskUserQuestion` does not count as approval.

---

## Step 5 — Tag and push

Only after explicit Yes from Step 4:

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

---

## Step 6 — Verify CI started

```powershell
gh run list --limit 3
```

The top entry must show workflow name `Release` and status `queued` or
`in_progress` for the tag just pushed. If it does not appear, wait 30 seconds
and run the command once more. If still absent after the retry, warn the user
that CI may not have triggered and they should check GitHub Actions manually.

Get the run URL:

```powershell
gh run list --limit 1 --json url --jq '.[0].url'
```

Share the URL with the user.

---

## Quality Bar

A good prod release satisfies all of the following:
- Branch was `main`, clean, up to date with origin before tagging.
- `dotnet build -c Release` passed with no new errors.
- The tag is strictly higher than the previous latest tag.
- Approval was given via `AskUserQuestion` after the exact version and commit list were shown.
- CI `Release` run is confirmed queued or in progress within 60 seconds of push.

If any of these are not true, the release is not complete.

---

## Done

Report to the user: the tag pushed (`vX.Y.Z`), the GitHub Actions run URL, and
that installed prod copies (Ctrl+Alt+U) will auto-update on next launch or within
the 4-hour periodic check.
