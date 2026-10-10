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

## Step 3 — Verify the affected behavior

Start the Dev channel:

```powershell
dotnet run --project src/UniversalSpellCheck.csproj -c Dev
```

`src/AGENTS.md` holds the current manual acceptance checklist. Run the checks
relevant to the changed behavior, plus focused automated tests. For a logging
or log-reader-only change, verify the reader against a date-boundary fixture
and inspect one Dev run's new fields when a live run is available. Do not claim
that unrun manual checks passed. If a relevant check fails, fix it, rebuild,
and retest before pushing.

Confirm a new `started` event appears in the shared log after launch. A Dev
process started from a restricted workspace sandbox may run without permission
to write the normal log directory; a running process alone does not verify
logging. Stop that test process and use a launch with normal filesystem access
for a live-log check.

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

## Step 5 — Verify publishing closeout

Complete all three checks in [Required publishing closeout](SKILL.md#required-publishing-closeout)
before ending the turn: every session change pushed, Dev stopped and cleared, and
session-created scratch/processes cleaned up. Verify remote HEAD and the final local
working tree after cleanup. Include the results in the completion report.

---

## Quality Bar

A good dev deploy satisfies all of the following:
- Build succeeded with no errors or warnings that weren't present before.
- Relevant acceptance checks passed; report any manual checks that were not run.
- The branch was `main` with a clean working tree before push.
- `git log origin/main --oneline -1` SHA matches local HEAD after push.
- All required publishing closeout checks are verified.

If any of these are not true, the deploy is not complete.

---

## Done

Dev channel is updated. Report to the user: branch (`main`), commit SHA that
landed on origin, and that `git pull` + `dotnet run -c Dev` will pick up the changes.
