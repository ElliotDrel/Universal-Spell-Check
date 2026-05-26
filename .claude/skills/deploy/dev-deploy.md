# Dev Deploy

**Scope:** Push current commits to `origin/main` only. This does NOT bump version
numbers, create tags, trigger CI, or affect the Prod channel in any way.

Push current changes to `origin/main` so the Dev channel can be updated via
`git pull` + `dotnet run -c Dev`. No versioning, no approval gate, no CI pipeline.

---

## Step 1 — Check working tree and branch

```powershell
git status
git diff --stat
git branch --show-current
```

- Must be on branch `main`. If not, stop and ask the user before proceeding.
- No uncommitted changes. If there are any, use the `commit` skill before
  continuing. Do not push a dirty working tree under any circumstances, even if
  the user asks to skip the commit.

---

## Step 2 — Build check (Dev config)

Confirm the code compiles before testing anything:

```powershell
dotnet build src/UniversalSpellCheck.csproj -c Dev
```

If the build fails, stop. Fix the build error first. Do not proceed to testing a
broken build.

---

## Step 3 — Launch Dev channel and run acceptance checks

Start the Dev channel:

```powershell
dotnet run --project src/UniversalSpellCheck.csproj -c Dev
```

Then walk through all 10 manual acceptance checks. The user must physically
interact with the running app — the AI cannot automate them.

> **Note:** This checklist is reproduced from `src/CLAUDE.md` ("Manual Acceptance
> Checks" section). If `src/CLAUDE.md` is updated, update this list to match.

**Acceptance checklist:**

1. Launch with no saved API key → trigger hotkey → verify Settings opens (no paste).
2. Save a valid API key in Settings.
3. Select misspelled text in Notepad → press Ctrl+Alt+D → verify corrected text replaces selection.
4. Select misspelled text in a browser textarea → press Ctrl+Alt+D → verify replacement.
5. During a request, verify the bottom-center loading overlay appears after copy, does NOT steal focus, and disappears after replacement or failure.
6. Press the hotkey with no selected text → verify stale clipboard text is NOT pasted.
7. Press the hotkey twice rapidly → verify only one replacement attempt runs (`guard_rejected reason=already_running` appears in log).
8. Select `open ai and github` → press Ctrl+Alt+D → verify output contains `OpenAI` and `GitHub`, and `replacements_count > 0` in log.
9. Quit from the tray menu → verify the hotkey stops firing.
10. Run Prod (Ctrl+Alt+U) and Dev (Ctrl+Alt+D) simultaneously → verify both appear in the tray with distinct icons and hotkeys, and both write entries to the same daily log with correct `channel=` stamps.

Use `AskUserQuestion` to confirm all 10 checks pass before proceeding:

> "Have you run through all 10 acceptance checks and they all pass?"

If any check fails, stop. Do not push failing code. Fix the issue, rebuild
(`dotnet run --project src/UniversalSpellCheck.csproj -c Dev`), retest the
failed check, then resume.

---

## Step 4 — Push to origin/main

```powershell
git push origin main
```

After the push completes, verify it landed:

```powershell
git log origin/main --oneline -1
git log --oneline -1
```

Both commands must return the same commit SHA. If they differ, the push did not
land — do not declare done.

---

## Quality Bar

A good dev deploy satisfies all of the following:
- Build succeeded with no errors or warnings that weren't present before.
- All 10 acceptance checks passed, confirmed via `AskUserQuestion`.
- The branch was `main` with a clean working tree before push.
- `git log origin/main --oneline -1` SHA matches local HEAD after push.

If any of these are not true, the deploy is not complete.

---

## Done

Dev channel is updated. Report to the user: branch (`main`), commit SHA that
landed on origin, and that `git pull` + `dotnet run -c Dev` will pick up the changes.
