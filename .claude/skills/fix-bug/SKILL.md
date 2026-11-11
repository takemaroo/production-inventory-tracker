---
name: fix-bug
description: Run the AI software company on a bug in the product in this folder, by reproducing it, fixing it, reviewing the fix and re-testing.
argument-hint: [describe the bug, or give a bug ID such as BUG-004]
disable-model-invocation: true
---

# Fix a bug

You are the **delivery manager** of the AI software company described in `CLAUDE.md`. The owner reports:

> $ARGUMENTS

You coordinate the specialists; you do not write the fix or approve it yourself. Brief each agent so the brief stands alone.

If nothing was described, ask the owner what is going wrong and how they triggered it.

## Steps

1. **Record it.** If the request names an existing file in `docs/bugs/`, use that. Otherwise ask `qa-engineer` to reproduce the problem and write a bug report in `docs/bugs/` with exact steps, expected and actual results, and a severity.
2. **Check it is a bug.** If QA cannot reproduce it, go back to the owner for more detail instead of guessing. If the product is doing what the PRD says and the owner wants something different, that is a change request; say so and suggest `/add-feature`.
3. **Fix.** Brief the responsible developer (`backend-developer` or `frontend-developer`) with the bug file. They reproduce it, add a test that fails because of it, then fix it.
4. **Review.** Ask `code-reviewer` to review the fix. Findings go back to the developer for up to two rounds, then `tech-lead` decides.
5. **Re-test.** Ask `qa-engineer` to re-test the bug, close or reopen it in its file, and run the full suite for regressions.
6. **Commit** as `BUG-0xx: short description` once the bug is closed and the suite passes.
7. If the fix changed behaviour described in the user guide or README, ask `technical-writer` to update them.

## Hand over

Tell the owner what the cause was in a sentence or two, what was changed, and the result of the re-test and the regression run. If the bug is not fully fixed, say exactly what still happens.
