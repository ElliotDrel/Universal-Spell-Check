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

## Step 6 — Verify tag reached GitHub and CI triggered

**6a — Confirm the tag is on the remote.**

```powershell
gh api repos/{owner}/{repo}/git/refs/tags/vX.Y.Z
```

Replace `{owner}/{repo}` with the actual repo slug (e.g. `ElliotDrel/Universal-Spell-Check`).
The response must contain a `"ref"` field equal to `"refs/tags/vX.Y.Z"`. If it
returns 404, the tag push did not land — push it again and re-check before
continuing. Do not proceed if the tag is absent from GitHub.

**6b — Confirm the workflow triggered.**

```powershell
gh run list --workflow release.yml --limit 5 --json databaseId,displayTitle,status,headSha,createdAt
```

Wait up to 60 seconds (check once at 30 s, once at 60 s) for a run whose
`displayTitle` contains the tag name OR whose `headSha` matches the tagged
commit. If no matching run appears after 60 seconds:

- This is the **workflow-never-triggered failure mode**. It happens when a tag
  is pushed but GitHub Actions does not fire (rare, but observed in this repo
  for `v0.1.5`).
- Stop and tell the user: "The release.yml workflow did not trigger for
  `vX.Y.Z`. The tag is on GitHub but CI never started. Options: (1) delete and
  re-push the tag, or (2) manually trigger the workflow via
  `gh workflow run release.yml --ref vX.Y.Z`."
- Do not declare done.

Get and share the run URL:

```powershell
gh run list --workflow release.yml --limit 1 --json url --jq '.[0].url'
```

**6c — Poll until the run completes.**

Check run status every 30 seconds until `status` is `completed`:

```powershell
gh run view <run-id> --json status,conclusion,url
```

Do not declare done while `status` is `queued` or `in_progress`. Keep polling.
When `status=completed`:

- If `conclusion=success` → proceed to Step 7.
- If `conclusion=failure` → run the failure diagnosis below before stopping.

**Failure diagnosis (conclusion=failure):**

```powershell
gh run view <run-id> --log-failed 2>&1 | Select-String -Pattern "ERR|FTL|error" | Select-Object -First 30
```

Check which step failed:

| Failed step | What happened | What to do |
|---|---|---|
| `Upload to GitHub Releases` with "account suspended" | GitHub API transient error; all assets uploaded but draft never published | See Step 7 — draft may exist with all assets |
| `Upload to GitHub Releases` with other error | Asset upload failed mid-way | Check if partial draft exists; may need to delete and re-run |
| `Pack` or `Publish` | Build/packaging error | Fix the underlying code issue; delete the tag and re-release |
| `Restore` or `Setup .NET` | Environment/infra error | Retry the workflow run via `gh run rerun <run-id>` |

---

## Step 7 — Verify the GitHub Release is published (not a Draft)

This step is **mandatory** regardless of whether CI reported success. A known
failure mode is the workflow completing its asset uploads but dying on the
draft-to-published conversion, leaving a stuck Draft that Velopack cannot see.

```powershell
gh release view vX.Y.Z --json isDraft,tagName,assets --jq '{isDraft,tagName,assetCount: (.assets | length)}'
```

**Case A — Release exists and `isDraft=false`:** Release is live. Proceed to Done.

**Case B — Release exists and `isDraft=true`:** Draft is stuck unpublished.
This is the **draft-stuck failure mode** (caused by `"Sorry. Your account was
suspended"` or similar transient GitHub API errors during `vpk upload github`).
All assets are present; only the publish step failed. Fix it:

```powershell
gh release edit vX.Y.Z --draft=false --repo {owner}/{repo}
```

Verify the result:

```powershell
gh release view vX.Y.Z --json isDraft --jq '.isDraft'
```

Must return `false`. Then proceed to Done.

**Case C — Release does not exist (`release not found`):** The workflow failed
before creating any release, or the tag has no associated release. Do not
declare done. Tell the user what step failed (from Step 6c diagnosis) and what
action is needed.

---

## Quality Bar

A good prod release satisfies **all** of the following:
- Branch was `main`, clean, up to date with origin before tagging.
- `dotnet build -c Release` passed with no new errors.
- The tag is strictly higher than the previous latest tag.
- Approval was given via `AskUserQuestion` after the exact version and commit list were shown.
- Tag confirmed present on GitHub remote via API.
- CI `Release` workflow run confirmed triggered (matching run found within 60 s).
- CI run completed with `conclusion=success` OR a stuck Draft was manually published.
- `gh release view vX.Y.Z --json isDraft` returns `false`.

If any of these are not true, the release is not complete. Do not end the conversation.

---

## Done

Report to the user: the tag pushed (`vX.Y.Z`), the GitHub Actions run URL, confirmation
that `isDraft=false`, and that installed prod copies (Ctrl+Alt+U) will auto-update
on next launch or within the 4-hour periodic check.
