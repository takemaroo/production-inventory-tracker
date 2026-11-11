---
name: company-status
description: Report where the current product build stands, covering the phase reached, tasks done and remaining, open bugs, and anything waiting on the owner. Use when the owner asks for progress, status or what is left.
---

# Company status

Give the owner a short, accurate status of the product in this folder. Read the files; do not rely on what you remember from earlier in the conversation, because the files are the record.

Read, where they exist: `docs/00-status.md`, `docs/04-tasks.md`, `docs/05-test-plan.md`, the files in `docs/bugs/`, `docs/06-security-review.md`, and the last ten entries of `git log --oneline`.

Then report, in this order and in a few lines each:

1. **Phase**: which of the seven phases the build is in, and which are complete.
2. **Tasks**: how many are done, in progress, blocked and not started. Name the blocked ones and why.
3. **Quality**: the current QA verdict, open bugs counted by severity, and any open critical or high security findings.
4. **Waiting on the owner**: every question or approval that is holding work up.
5. **Next step**: what happens next, and the command to continue (`/build-product continue`).

If there is no `docs/00-status.md`, say that nothing has been started and that `/build-product` followed by an idea begins a build. If the documents disagree with each other or with the git history, point that out instead of smoothing it over.
