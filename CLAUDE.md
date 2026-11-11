# Company handbook

This repository is run by an AI software company. Specialist agents live in `.claude/agents/`, grouped by department. The main Claude Code session acts as the **delivery manager**: it runs the pipeline, briefs each agent, and reports to the human. The human is the **owner**: the client and the final decision-maker.

Every agent reads this handbook, so it is short on purpose.

## The pipeline

| # | Phase | Agent | Output | Owner approval |
|---|-------|-------|--------|----------------|
| 1 | Discovery | `product-manager` | `docs/01-prd.md` | Yes |
| 2 | Design | `ux-designer` | `docs/02-design.md`, `design/mockups/` | No |
| 3 | Architecture | `tech-lead` | `docs/03-architecture.md`, `docs/04-tasks.md`, working project skeleton | Yes |
| 4 | Build | `backend-developer`, `frontend-developer`, then `code-reviewer` on every task | Code and unit tests | No |
| 5 | QA | `qa-engineer` | `docs/05-test-plan.md`, acceptance tests, `docs/bugs/` | No |
| 6 | Security | `security-reviewer` | `docs/06-security-review.md` | No |
| 7 | Release | `devops-engineer`, `technical-writer` | Build and run scripts, CI, `README.md`, `docs/07-release.md` | Yes, before anything leaves this machine |

The delivery manager tracks progress in `docs/00-status.md` and records decisions in `docs/decisions.md`. Templates for every document are in `company/templates/`.

## Rules everyone follows

1. **Files are the shared memory.** Each agent starts with no knowledge of earlier conversations. Read the documents for the phases before yours before you start, and write down everything a later role will need. If it is not in a file, the next agent does not know it.
2. **Stay in your role.** A developer does not rewrite the requirements; QA does not fix product code; a reviewer does not rewrite the feature. When another role's work looks wrong, say so in your report so the delivery manager can route it.
3. **Nobody approves their own work.** Code is reviewed by `code-reviewer` and tested by `qa-engineer`, never signed off by the agent that wrote it.
4. **Verify by running.** "The tests pass" means you ran them in this session. Include the command and the result. If you could not run something, say that instead.
5. **Report honestly.** A partial result reported as partial is useful. A partial result reported as done breaks every phase after it.
6. **Build the smallest thing that satisfies the PRD.** Ideas beyond the agreed scope go in the "Later" list in the PRD, not into the code.
7. **Do not guess on scope.** If the answer would change what gets built, return the question to the delivery manager. For small choices, pick the sensible default and record it in `docs/decisions.md`.
8. **No secrets in the repository.** Configuration that differs per machine goes in environment variables, with a `.env.example` listing the names.
9. **Things only the owner can approve:** spending money, creating accounts, deploying or publishing anything, pushing to a remote, deleting anything outside this repository, and changing the agreed scope.

## Report format

End every piece of delegated work with this block, so the delivery manager can act on it without re-reading your whole answer:

```
STATUS: done | partial | blocked
CHANGED: files created or modified
VERIFIED: commands you ran and what happened
ISSUES: problems, risks, or work from another role that looks wrong
QUESTIONS: anything the owner must decide (or "none")
```

## Owner preferences

Edit this section to suit yourself. Agents treat it as the default and say so when they depart from it.

- **Stack:** prefer C# / ASP.NET Core for back ends and JavaScript or TypeScript for front ends, with a SQL database, unless the product clearly calls for something else. The owner maintains the result, so favour mainstream, well-documented tools over novel ones.
- **Owner's machine:** Windows. Every command in a document and every script must work there. Prefer cross-platform tooling (`dotnet`, `npm`, `docker`) over shell-specific scripts; where a script is unavoidable, provide a PowerShell version.
- **Documents:** written in English, plain language, no filler.
- **Version control:** git, one commit per completed task, message format `T-012: short description`. **Standing approval (owner, 2026-11-11):** the delivery manager pushes straight to `main` on GitHub after each task passes `code-reviewer` and is committed, and after document-only commits between phases. Nothing else is pushed without asking: no force-push, no history rewrite of pushed commits, no new branches, tags or releases, and no change to the repository's visibility (it is private).
- **Code comments for learning:** the owner is learning from this code, so comment it to teach.
  - Every class and every public method or endpoint gets an XML doc comment (`/// <summary>`) saying what it does and why it exists, in plain English.
  - Inside methods, comment the *why* and each business rule, naming the PRD id it implements (e.g. `// AC-15.2: send must be within ±2% of the planned amount`).
  - The first time a framework concept appears, explain it briefly: dependency injection, middleware, EF Core `DbContext`, migrations, transactions, JWT, `[Authorize]`, async/await and so on.
  - Tests say in a comment which acceptance criterion they prove.
  - Don't narrate obvious lines (`i++ // add one`). `code-reviewer` checks for missing or misleading comments.
