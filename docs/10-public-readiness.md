# Public-readiness review: secrets and privacy in the full git history

Reviewer: `security-reviewer`. Date: 2026-11-26. Scope: the whole repository history before the owner changes the GitHub repository from private to public.

This document never quotes a secret, a password or a personal value. Each one is described and its location given.

## Summary

I scanned all 84 commits reachable from every local ref (`main`, `origin/main`, `origin/HEAD`; there are no tags, stashes or other branches), plus 3 unreachable local commits and their trees. I also checked every blob that ever existed in those commits, including files that were later deleted. **No real secret is in the history.** There is no private key, certificate, cloud or GitHub token, credential-bearing connection string, real JWT signing key, `.env` file or user-secrets file. The only `.env`-pattern file ever tracked is `.env.example`, and its variables are empty in all five versions. No database, backup, publish output, test result, log or IDE settings file was ever committed. The issues are privacy and tidiness. The biggest is the owner's **personal email address in the author and committer fields of 82 commits**. Hiding it needs a history rewrite, and only the owner can approve that. Two more items are smaller: the owner's **Windows home path** in `docs/06-security-review.md`, and some **policy-passing sample passwords**, now public by design, in old commits and in two current docs. Everything else can be fixed with an ordinary new commit, or needs no action. Recommendation: **ready to make public once the owner has decided on finding PR-01**. Nothing found is critical or high.

## How the scan was done

- `git for-each-ref`, `git stash list`, `git rev-list --all` (84 commits) and `git fsck --full --unreachable --dangling --no-reflogs`. That found 3 unreachable commits and 5 unreachable trees, and no dangling blobs.
- I wrote `git log --all -p --full-history --text` (81 MB) and the patches of the 3 unreachable commits to the session scratchpad and searched them with grep. Patterns: PEM private keys and certificates, GitHub (`ghp_`, `gho_`, `github_pat_`), AWS, Google, Slack, OpenAI and Anthropic key formats, JWTs (`eyJ...`), `AccountKey=`, SAS signatures, `Password=`/`Pwd=`/`User Id=` in connection strings, `*KEY*=`/`*PASSWORD*=`/`*SECRET*=`/`*TOKEN*=` assignments with values, and Windows, Unix and macOS home paths.
- A Python script walked every blob from `git rev-list --objects --all` plus the unreachable commits. For each path it reported email addresses, home paths, the owner's user and account names, `KEY=value` assignments, long Base64-like strings, and quoted literals of 8 or more characters with an upper-case letter, a lower-case letter and a digit (the app's password policy, `src/InventoryTracker.Api/Auth/AuthSetup.cs:65-73`) on lines that mention a password, key, secret or token. All values were masked in the output.
- A second script flagged high-entropy strings (Shannon entropy above 4.2, mixed case, 3 or more digits) on added lines. Every hit was a file path, a migration id, a test method name, or the standard JWT header in a design example (`docs/02-design.md:523`, header only, no payload or signature).
- I read the identities in commit metadata and commit-message trailers (`%an <%ae>`, `%cn <%ce>`, `Co-Authored-By`, `Signed-off-by`) and counted them per category.
- `git ls-files`, `git status --short --ignored` and `git check-ignore -v` against a list of sensitive file names.
- I did not open the owner's database, user-secrets, the real `src/InventoryTracker.Api/.env` or the saved user-scope signing key. `git status --ignored` lists no `.env` file in the working tree.

## Findings

### PR-01 (medium): the owner's personal email address is in the metadata of 82 commits

**Where.** The author email of 82 of the 84 commits (every commit except the 2 Dependabot ones), and the committer email of 79. The first is `49dc2ab`, the latest `62bf4fb`. The same address is the `user.email` in the owner's global git configuration, so every future commit will carry it too.

**Identities in the history, by category** (de-duplicated across author, committer and message trailers):

| Identity | Category | Where it appears | Commits | Public after the switch? |
|----------|----------|------------------|---------|--------------------------|
| The owner's name with a personal address | personal address | author | 82 | yes |
| The same | personal address | committer | 79 | yes |
| "GitHub" (web edits and merges) | GitHub noreply address | committer | 5 (3 web edits to README in October, 2 Dependabot) | yes, harmless |
| Dependabot bot account | GitHub noreply address | author, and a `Co-authored-by` trailer | 2 (`b226783`, `70096c3`) | yes, harmless |
| Dependabot sign-off | GitHub service (support) address, not personal | `Signed-off-by` trailer | 1 (`b226783`) | yes, harmless |
| Claude | Anthropic noreply address | `Co-Authored-By` trailer | 77 | yes, harmless |

All 84 commits have the time zone +0900, so the history also shows the owner's time zone.

**How it could be misused.** Once the repository is public, anyone can read the address from GitHub's commit pages, from `.patch` URLs and from the API. Address harvesters routinely scrape public commits for spam and phishing.

**What to do.**
- **Now, with no rewrite (recommended in every case):** set future commits to the GitHub-provided noreply address (`git config --global user.email <id+username>@users.noreply.github.com`). Then turn on GitHub's "Keep my email addresses private" and "Block command line pushes that expose my email" under Settings, Emails. That is an account setting, so the owner does it. A `.mailmap` file does **not** help: GitHub ignores it on commit pages and in the API.
- **Hiding the address in the existing 82 commits needs a history rewrite.** These are options for the owner only, because the standing rule forbids rewriting pushed history without explicit approval:
  - **Option A: accept.** If the owner is happy to give this address to hiring companies anyway, leave the history as it is. Cost: none. The address becomes public.
  - **Option B: rewrite and force-push this repository.** Rewrite the author and committer emails (for example `git filter-branch --env-filter`, built into git), then force-push `main`. Every commit hash changes, and every hash quoted in the docs (such as `60ba6e4`, `9e49d42`) stops matching. GitHub also keeps the old commits reachable through the two Dependabot pull requests (`refs/pull/1`, `refs/pull/2`, whose bases contain the address) and in its caches. A force-push alone is therefore **not enough**: removing them needs a GitHub Support request. This is the least reliable option.
  - **Option C: publish a fresh repository.** Keep this private repository as it is. Create a new public repository and push a rewritten copy of the history into it (or a single snapshot commit). Nothing from the old repository leaks, and the private one stays the record. This needs the owner to create the repository (an account action) and approve the push.

### PR-02 (low): the owner's Windows home path, and with it the Windows user name, is in the security review

**Where.** `docs/06-security-review.md:28` and `:676` at `HEAD`. Both give the full path of an agent scratchpad folder under the owner's user profile. That path includes the Windows user name and Claude Code session ids. The path is in that file in 46 commits, from `60ba6e4` (2026-11-21) to `HEAD`, and in no other file in any commit.

**How it could be misused.** It reveals the local account name and folder layout. The name matches the first name already shown in commit metadata, so little new is exposed. Still, a public portfolio piece should not hold machine-specific paths.

**What to do.** In a **normal new commit**, replace both paths with a neutral description such as "a temporary scratch folder outside the repository". Old commits keep it. Removing it there needs a rewrite (PR-01 options B or C, which fix both at once). On its own it is not worth a rewrite. Owner: `security-reviewer` owns that document, but the delivery manager routes the edit.

**Status (2026-11-26): fixed in the working tree, not yet committed.** `devops-engineer` replaced both paths with `%USERPROFILE%\AppData\Local\Temp\claude\<session>\scratchpad\...`, which keeps the folder layout and drops the user name and the session ids. A search of every tracked file found no other Windows, Unix or macOS home path and no other copy of the Windows user name. Old commits still hold it (PR-01 options B or C).

### PR-03 (low): sample passwords that pass the password policy are in old commits and in two current documents

**Where.**
- **History only:** the former first-Manager sample password from the architecture guide's `user-secrets` example (16 characters, passes the policy). It was added in `684d5f2` and was last present in `43b3934`/`8dbdbe7`; it was removed by `9e49d42` (decision #135, SEC-12). It is not in any file at `HEAD`.
- **History:** four passwords in the sample requests file `src/InventoryTracker.Api/InventoryTracker.Api.http`, sent for the sample user `warehouse.user@example.com`, all passing the policy. They were added in `d09c6db`, were still present in `43b3934`, and were replaced by `$dotenv` reads in `9e49d42`.
- **Still at `HEAD`:** three of those values, and one more design-example value, are quoted in `docs/02-design.md:516`, `:536`, `:555`, `:559` (API reference examples) and in `docs/06-security-review.md:300`, `:304`, `:305`, `:890` (the review's own table about them).

**How it could be misused.** These are not the passwords of any real system unless someone used them on a real database. If the owner ever ran the old walk-through, or used the old architecture sample, against the database at `%LOCALAPPDATA%\InventoryTracker`, then the first Manager or `warehouse.user@example.com` could have one of these passwords. Anyone who reads the repository would know it. The API listens only on localhost (decision #119), which limits the exposure. But public is permanent, and the product may later move to the cloud (PRD Later list).

**What to do.**
- **Owner check, no commit needed:** make sure that no account on the real database uses any of the old sample values. If unsure, change the first Manager's password, and change or deactivate `warehouse.user@example.com` if it exists there. This is the step that matters.
- **Normal new commit (optional, tidiness):** in `docs/02-design.md`, replace the example passwords with values that fail the policy (for example `<password>`), as SEC-21 already plans. In `docs/06-security-review.md`, refer to them as "the former samples" without quoting them, as the review already does for the architecture sample.
- **History:** needs a rewrite to remove. Not recommended for this finding alone. Once the owner's check is done, treat the values as public.

**Status (2026-11-26): fixed in the working tree, not yet committed.** `docs/02-design.md:516`, `:536`, `:555` and `:559` now show `<sample password>` and `<new sample password>`, which fail the policy. `docs/06-security-review.md:300`, `:304`, `:305` and `:890` describe the values without quoting them. Left on purpose: the test fixtures under `tests/`; the QA boundary records (`docs/05-test-plan.md:228`, `docs/bugs/BUG-001-text-limits-count-code-units.md:50`, `docs/06-security-review.md:301`) and the common-password examples (`docs/06-security-review.md:302`, `:352`), all judged "Keep" in SEC-21; and the weak signing-key example at `docs/06-security-review.md:313`, which is not a password. The delivery manager reports that the owner confirmed no real account ever used the sample values.

### PR-04 (low): a few example email addresses use real, registered domains

**Where.** At `HEAD`: `docs/05-test-plan.md:216` uses a Spanish-language "example" domain that is a real registered domain, not a reserved one. `src/InventoryTracker.Api/Auth/SignInService.cs:67` has a code comment with two spellings of an address at a well-known real social-network domain. In history only, the deleted `design-taste-frontend` skill (see PR-07) contains demo addresses at two real domains. The addresses themselves are invented; they are not real people.

Every other address in every commit is on a reserved domain: `example.com`, `.example` (`yourcompany.example`), or `.test` (`inventory.test`, `example.test`, `v1.test`). That includes the person-like sample names in the docs and tests (for example `maria.lopez@example.com`). They are fine.

**What to do.** In a **normal new commit**, change the two `HEAD` occurrences to reserved domains (for example `example.test`). The test plan line describes a QA probe. No file under `tests/` uses either address (`git grep` at `HEAD`), so only these two lines change. No rewrite needed.

**Status (2026-11-26): fixed in the working tree, not yet committed.** `docs/05-test-plan.md:216` now uses `ejemplo.test`, and the comment at `src/InventoryTracker.Api/Auth/SignInService.cs:67` uses `example.com`. Only the comment text changed in the code. A search of every tracked file found no other example address on a real domain.

### PR-05 (low): `.gitignore` misses a few sensitive file types

**Where.** `.gitignore`. Checked with `git check-ignore`. **Not ignored today:**
- `.claude/settings.local.json`: Claude Code's per-machine permission file. It often holds absolute paths, user names and allowed commands. It does not exist yet, but one `git add -A` after it appears would publish it.
- `CLAUDE.local.md`: Claude Code's personal, not-for-sharing instructions file.
- `*.pem`, `*.key`, `*.p12`: only `*.pfx` is ignored.
- `*.trx` outside `TestResults/`, `*.bak`, and `*.zip`. A zipped `publish/` folder or a renamed database backup would be picked up.
- `.vscode/settings.json` is deliberately un-ignored by the standard template. It is not in the repository and rarely holds anything private, so it can stay.

**Already covered and verified:** `.env`, `.env.*` (with `!.env.example`), `*.db`, `*.db-wal`, `*.db-shm`, `*.db-journal`, `*.sqlite`, `publish/`, `TestResults/`, `bin/`, `obj/`, `*.log`, `logs/`, `.vs/`, `.idea/`, `*.user`, `*.pfx`, `*.pubxml`. Database backups default to `%LOCALAPPDATA%\InventoryTracker\backups`, outside the repository, and are `.db` files anyway.

**What to do.** In a **normal new commit**, add `.claude/settings.local.json`, `CLAUDE.local.md`, `*.pem`, `*.key`, `*.p12`, `*.trx`, `*.bak` and `*.zip`, with a comment for each. Owner: `devops-engineer`. No rewrite needed.

**Status (2026-11-26): fixed in the working tree, not yet committed.** All eight patterns, and `*.pfx` again, are in a commented block at the end of `.gitignore`. `git ls-files -i -c --exclude-standard` lists no file, so no tracked file matches an ignore rule, and `git check-ignore -v` confirms each new pattern.

### PR-06 (info): local-only unreachable commits

**Where.** Three unreachable commits exist only in this clone: `2430c2a` (a first "Add AI company setup", reset before it was pushed) and `afb94cf`, `01f66a7` (the October project commits as they were before a `pull --rebase`; their rebased copies `598acd2`, `d08e01f` are on `main`). I scanned them too. They hold only files that are also in the reachable history, and no secret. The `origin/main` reflog shows none of them was ever pushed.

**What to do.** Nothing. Making the GitHub repository public does not send local unreachable objects anywhere. Git deletes them by itself once they expire.

### PR-07 (info): third-party content in the repository and its history

**Where.**
- `.claude/skills/design-taste-frontend/`: a website-design skill of about 1,630 files and 36 MB. It includes photographs and icons. It was added in `39ac0c6` and stopped being tracked in `5f1251e` (decision #136), so it is in history only. Its upstream licence file is included (MIT). Its photograph folders have credit files that name Unsplash-licence and CC0 sources. Those licences allow redistribution, so publishing the history is allowed. The cost is a large clone.
- `.claude/skills/grill-me/`, `.claude/skills/grilling/` and the copies under `.agents/skills/`: two skills installed from another author's public GitHub repository (recorded in `skills-lock.json`). They are tracked at `HEAD` with no licence or attribution file. I could not check the upstream licence without going online.

**What to do.** For the design skill, nothing is required. A rewrite only to shrink the history is not worth it (and if the owner chooses PR-01 option C, the skill can be left out of the new history). For the two tracked skills, the owner should confirm the upstream licence. Then, in a **normal new commit**, add a short attribution (source repository and licence) next to them or in the README. No rewrite needed.

**Status (2026-11-26): attribution added in the working tree, not yet committed.** The upstream `LICENSE` is MIT, copyright 2026 Matt Pocock (fetched over HTTPS on 2026-11-26). `.claude/skills/NOTICE.md` names the source repository, the author and the licence, includes the licence text as MIT requires, and covers both copies of each skill. The skill folders and their `SKILL.md` files are unchanged, so the hashes in `skills-lock.json` still match.

### PR-08 (info): what `.claude/` and the company files show

**Where.** `CLAUDE.md`, `company/templates/`, `.claude/agents/` (10 agent definitions), `.claude/skills/` (6 skills), `.agents/skills/`, and the internal status and decision logs `docs/00-status.md` and `docs/decisions.md`.

**What I checked.** No absolute paths, emails, account names, tokens or machine-specific settings. No `.claude/settings.json` or `.claude/settings.local.json` exists or was ever tracked. The owner's Claude Code auto-memory lives in the user profile, outside the repository, and is not tracked. The agent definitions and skills are fine to publish.

**Worth knowing before showing this to hiring companies.** The repository openly shows that an AI agent team built the product. README line 288 says so, 77 commits carry an AI co-author trailer, and the status log has internal notes such as a usage-limit entry (`docs/00-status.md:70`) and the owner's standing approvals in `CLAUDE.md`. None of this is a leak, and nothing in it is private, but the owner should present the project in a way that matches it. `docs/06-security-review.md` is also public. It lists known limits and Later items (for example no sign-in rate limit, SEC-22). For a localhost-only product that is fine, and it shows diligence.

**What to do.** Nothing is required. Optionally, the README's opening section could say plainly how the project was built and what the owner's role was.

### PR-09 (info): things on GitHub that also become public, outside git

**Where.** GitHub, not this clone: the two Dependabot pull requests (#1, #2) and their comments, the GitHub Actions run logs for every CI run, any issues, and the repository's description and topics. I could not check these, because the review was read-only and local.

**What I checked locally.** The CI workflow (`.github/workflows/ci.yml`) uses no secrets, has a read-only token (`permissions:` at line 16), uploads no artifacts and prints no environment values. Its logs should therefore hold nothing private.

**What to do.** Before switching visibility, the owner looks through the Actions logs, the pull requests and any issues on GitHub. Afterwards, the owner can turn on secret scanning and push protection, which are free for public repositories. That is a repository setting, so only the owner changes it.

## What needs a history rewrite and what does not

| Finding | Normal new commit | Needs a history rewrite to remove completely |
|---------|-------------------|----------------------------------------------|
| PR-01 personal email | Future commits only (git config and GitHub email settings; no file change) | **Yes**: options B or C, owner approval required |
| PR-02 home path | Yes: edit `docs/06-security-review.md` | Old copies, in 46 commits: only with B or C |
| PR-03 sample passwords | Yes: edit `docs/02-design.md` and `docs/06-security-review.md` (optional) | Old copies: only with B or C; not recommended for this alone |
| PR-04 real domains in examples | Yes: two lines | No |
| PR-05 `.gitignore` gaps | Yes | No |
| PR-06 unreachable commits | Not needed | No |
| PR-07 third-party content | Yes: attribution for the two skills | No (size only) |
| PR-08 `.claude/` and company files | Optional README wording | No |
| PR-09 GitHub-side content | Not a git change; the owner reviews it on GitHub | No |

## Release recommendation for the visibility change

**Clear to make public once the owner has decided on PR-01.** There is no critical or high finding, and no real secret anywhere in the history. If the owner accepts the email address (option A), the new-commit fixes for PR-02 to PR-05 and PR-07 can go in first, and the PR-03 database check should be done first. If the owner does not want the address public, choose option C before switching visibility, because the switch cannot be undone for anyone who has already cloned the repository.
