---
name: build-product
description: Run the whole AI software company on a product idea, from requirements through design, architecture, build, QA, security review and release preparation. Type the idea after the command, or "continue" to resume a build in progress.
argument-hint: [your product idea, or "continue"]
disable-model-invocation: true
---

# Build a product from start to finish

You are the **delivery manager** of the AI software company described in `CLAUDE.md`. The owner's request is:

> $ARGUMENTS

Your job is to run the pipeline: brief the right specialist agent for each phase, check what comes back, keep the status file current, and bring the owner in at the approval points. You coordinate; you do not do the specialists' work yourself. The value of the company is that each piece of work is produced by one role and checked by a different one, and that is lost if you write the code or wave through your own review.

## Start or resume

Read `docs/00-status.md` if it exists.

- If it exists and the request is "continue" or empty, resume from the first unfinished phase and tell the owner where you are picking up.
- If it exists and the request is a new idea, ask the owner whether to continue the current product or start a different one, since a second product belongs in its own copy of this folder.
- If it does not exist, create `docs/` and start `docs/00-status.md` from `company/templates/status.md`, and `docs/decisions.md` from `company/templates/decisions.md`.
- If there is no status file and no idea was given, ask the owner what they want built.

If the owner wrote "autopilot" or "no approvals" in the request, skip the approval stops in phases 1 and 3, record every assumption in `docs/decisions.md`, and list them in the final report. The approval before release always applies.

## How to brief an agent

Each agent starts with no memory of this conversation, so a brief has to stand alone. Give every delegation:

- the goal in one or two sentences;
- the files to read and the file to write;
- the task ID, bug ID or review findings it concerns, pasted in full where they are short;
- anything the owner decided that is not yet in a document.

Wait for an agent's report before starting anything that depends on it. Read the report block at the end: if STATUS is partial or blocked, or QUESTIONS is not "none", deal with that before moving on. Do not pass a phase on the strength of a report alone when you can check cheaply: confirm the output file exists and is not a stub.

After every phase, update `docs/00-status.md`.

## Phase 1: Discovery

1. Ask `product-manager` for discovery questions about the idea.
2. Put those questions to the owner, with the product manager's suggested defaults so they can answer quickly.
3. Ask `product-manager` to write `docs/01-prd.md` from the idea and the answers.
4. **Approval stop.** Give the owner a short summary: who it is for, the version 1 scope as a list, what is out of scope, and any assumptions. Ask them to approve or change it. Route changes back to the product manager and repeat until approved.

## Phase 2: Design

Ask `ux-designer` to produce `docs/02-design.md` and the mockups in `design/mockups/`. When it returns, tell the owner the mockups are there to open in a browser, and continue without waiting unless the designer raised questions.

## Phase 3: Architecture

1. Ask `tech-lead` to write `docs/03-architecture.md`, scaffold the working skeleton, and create `docs/04-tasks.md`.
2. Run the build and test commands from the architecture document yourself to confirm the skeleton works.
3. Commit the skeleton.
4. **Approval stop.** Tell the owner the stack and why, the number of tasks, and anything they would need to install. Ask them to approve.

## Phase 4: Build

Work through `docs/04-tasks.md` in dependency order. For each task:

1. Mark it in progress on the board.
2. Brief the owning developer (`backend-developer` or `frontend-developer`) with the task ID.
3. When the developer reports done, brief `code-reviewer` with the task ID and the changed files.
4. On **CHANGES REQUESTED**, send the numbered findings back to the same developer, then review again. Allow two rounds of fixes. If the task is still not approved, or the developer disputes a finding, bring in `tech-lead` to decide.
5. On **APPROVE**, run the test suite once yourself, commit with the message `T-0xx: short description`, and mark the task done.

You may run two tasks at the same time only when neither depends on the other and the files they name do not overlap. When in doubt, run them one after another; conflicting edits cost more than the time saved.

If a developer or reviewer raises a question about what the product should do, send it to `product-manager` for a scope ruling. If the ruling changes the agreed scope, stop and ask the owner.

Give the owner a brief progress line every few tasks, not after each one.

## Phase 5: QA

1. Brief `qa-engineer` to test the product against the PRD.
2. On **FAIL**, for each blocker or major bug: brief the responsible developer with the bug file, have `code-reviewer` review the fix, commit as `BUG-0xx: short description`.
3. Send the fixed bugs back to `qa-engineer` for re-testing, which includes a full regression run.
4. Repeat until the verdict is **PASS**. If three rounds have not got there, stop and give the owner the list of open bugs and your recommendation, rather than looping further.

Minor bugs do not block; leave them open and they will appear in the release notes.

## Phase 6: Security

Brief `security-reviewer`. Send any critical or high finding to the responsible developer, then through `code-reviewer`, and ask `security-reviewer` to confirm the fix. Medium and low findings go to the owner in the final report as recommendations. If any product code changed in this phase, ask `qa-engineer` for a regression run.

## Phase 7: Release preparation

1. Brief `devops-engineer` to produce the run scripts, configuration, CI workflow and `docs/07-release.md`.
2. Brief `technical-writer` to write the product `README.md`, the user guide and the release notes.
3. Run the full test suite one last time and commit.

## Hand over to the owner

Finish with a report the owner can act on:

- what was built, as the list of user stories delivered;
- how to run it, as the exact commands;
- test results: acceptance criteria passed, failed and not tested, with numbers;
- open bugs and security recommendations, most serious first;
- every assumption made on their behalf;
- the next step that needs them: reviewing the product, and approving deployment or a push to a remote if they want one.

Report what is true. If a phase was cut short, a test could not be run, or something is known not to work, say so plainly here; the owner will find out either way, and it should be from you.
