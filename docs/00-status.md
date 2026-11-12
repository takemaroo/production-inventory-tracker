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
| 4 | Build | in progress | code | tasks done: 6 of 21 (T-001 to T-006) |
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
- 2026-11-11 T-005 approved by `code-reviewer` (after one fix round), committed and pushed. T-003 being reworked after review (lock-out race).
- 2026-11-11 Docs brought in line with build decisions: architecture and task board (tech-lead, decisions #63-#65), design notes for #49/#57/#58 (ux-designer), PRD row 19.
- 2026-11-11 T-003 approved by `code-reviewer` after three rounds (lock-out race fixed, timing leak closed), committed and pushed. Started T-006 and T-014 in parallel.
- 2026-11-12 Laptop restart: nothing lost. T-006 and T-014 finished by fresh developers from the files on disk.
- 2026-11-12 T-006 approved by `code-reviewer` (after one fix round), committed and pushed. T-014 fixes in progress; owner Q43/Q44 open.

## Follow-ups to route

- security-reviewer (phase 6): from T-003, 5 wrong passwords can lock any account (a denial of service inherent in AC-01.4; owner may want to revisit). The timing signal and the lost failure counts were fixed in T-003 (decisions #61, #62); re-verify them.
- qa-engineer (phase 5): displayed percentages follow round(round(x, 6), 2) (decision #58), which can differ from a single rounding in rare cases (e.g. 1.0049996 shows 1.01). Test against the two-step formula. Threshold checks use the unrounded value.
- tech-lead (next doc update): architecture 5.2 "Equal timing" bullet must match corrected decision #62 (dummy hash runs after the account lock is released; the reason is equal cost, not hiding locked accounts).
- security-reviewer (phase 6): sign-in has no rate limit; each request costs about 45 ms of CPU. Belongs with the lock-out denial-of-service note.
- Small fix (give to the next developer with a free slot, e.g. T-007): T-001 tests `ClockTests.Clock_on_the_system_time_provider_is_close_to_real_time` and `CommonFoundationsTests.App_registers_a_clock_on_the_system_time` race on a second boundary (DateTime.UtcNow - clock.UtcNow can be slightly negative). Read clock.UtcNow first, or compare the other way round. Found by reading in T-006.
- BEFORE T-007 (small chore after T-006 and T-014 are committed): move the copied test helpers (AssertProblemAsync, ReadJsonAsync, Json, Messages, Ids, the 401/403 message constants, unit/item seeding, and T-006's SaveChangesInterceptor) into tests/.../Infrastructure/ (e.g. ApiAssert, Seed), and update UnitsTests, ItemsTests, UsersTests, AuthTests and ErrorHandlingTests to use them. Do the clock-test race fix in the same chore. (T-006 review finding 2.)
- tech-lead (next doc update): architecture 5.2 needs the exception from decision #70 (password checks outside the write transaction, re-confirm inside), next to the #61 sign-in note. Also note the general gap between token check and write transaction on every endpoint (T-014 review finding 3): a request can complete just after the user is deactivated.
