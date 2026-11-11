# Project status: Production & Inventory Tracker API

Kept current by the delivery manager. Last updated: 2026-11-11

**Idea as the owner gave it:**

> A backend API that tracks raw materials and finished products through production and flags discrepancies, to reduce waste. (From `README.md`; refined in the owner interview of 2026-11-11.)

**Mode:** approvals on

## Phases

| # | Phase | Status | Output | Notes |
|---|-------|--------|--------|-------|
| 1 | Discovery | done | `docs/01-prd.md` | Approved by owner 2026-11-11. |
| 2 | Design | done | `docs/02-design.md` | API spec: 48 endpoints, ProblemDetails errors, flows. Owner settled 2 questions (decisions #17, #18). |
| 3 | Architecture | done | `docs/03-architecture.md`, `docs/04-tasks.md` | Skeleton builds (0 warnings), 4 of 4 tests pass (re-run by delivery manager). 21 tasks. |
| 4 | Build | in progress | code | tasks done: 3 of 21 (T-001, T-002, T-004) |
| 5 | QA | not started | `docs/05-test-plan.md` | round 0 |
| 6 | Security | not started | `docs/06-security-review.md` | |
| 7 | Release | not started | `docs/07-release.md`, `README.md` | README is out of date; update it here. |

Status values: `not started`, `in progress`, `waiting on owner`, `done`.

## Waiting on the owner

- nothing

## Next step

T-001 and T-002 in progress in parallel. Each is reviewed by `code-reviewer`, then committed. Next: T-003 and T-004.

## Log

- 2026-11-11 Owner interview (grilling session) settled the design.
- 2026-11-11 `product-manager` wrote the PRD draft: 23 user stories and 4 questions for the owner.
- 2026-11-11 Owner confirmed the defaults for all 4 questions. Decisions logged in `docs/decisions.md`.
- 2026-11-11 Owner approved the PRD. Phase 2 started with `ux-designer`.
- 2026-11-11 `ux-designer` finished `docs/02-design.md`. Owner answered its 2 questions. Phase 3 started with `tech-lead`.
- 2026-11-11 `tech-lead` finished phase 3. Delivery manager re-ran build (0 warnings, 0 errors) and tests (4 of 4 passed).
- 2026-11-11 Owner approved phase 3. Committed setup (39ac0c6) and phases 1-3 (684d5f2). Started T-001 and T-002.
- 2026-11-11 T-002 approved by `code-reviewer` (after one fix round), committed and pushed.
- 2026-11-11 T-001 approved by `code-reviewer` (after one fix round), committed and pushed. Started T-003 and T-004 in parallel.

- 2026-11-11 T-004 approved by `code-reviewer` (after one fix round plus owner decision #49), committed and pushed. T-003 in review.

## Follow-ups to route

- T-011: round UsageVariancePercent and OutputVariancePercent to 6 places in code before saving; SQLite does not apply HasPrecision on write (T-004 review).
- T-011 / QA: check negative LeftoverRemaining (decision #48) end to end.
- tech-lead: reword architecture 4.1 item 11 to match decision #46 (only UNIQUE/PK give 409).
- T-005: UsageVariance notification wording when the percentage is null ("percentage not applicable"), per decision #49. T-011: store a null percentage in that case.
- Design doc: `docs/02-design.md` 5.7, 5.10 and 7.3 need a note that usageVariancePercent can be null (decision #49). Route to ux-designer or tech-lead with the next doc update.
- security-reviewer (phase 6): from T-003, (a) an unknown email answers faster than a wrong password (timing signal for which emails exist); (b) 5 wrong passwords can lock any account (inherent in AC-01.4); (c) two simultaneous wrong passwords can lose one failure count.
- tech-lead: architecture 3.2 says NormalizedEmail is unique, but Identity's EmailIndex is not; uniqueness holds through UserName = Email. Correct the document. T-014 must keep UserName equal to Email.
