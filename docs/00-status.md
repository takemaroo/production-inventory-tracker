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
| 4 | Build | in progress | code | tasks done: 9 of 22 (T-001 to T-008, T-014; T-008a added) |
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
- 2026-11-12 T-014 approved by `code-reviewer` after three rounds (password hashing moved out of the write lock, owner decisions #71/#72), committed and pushed. Next: shared test-helper chore, then T-007 and T-013.
- 2026-11-12 Chore: shared test helpers moved to tests/.../Infrastructure (ApiAssert, ApiJson, ExpectedMessages, Seed, BeforeSaveInterceptor) and clock-test race fixed; approved by `code-reviewer`, committed and pushed. Started T-007 and T-008 in parallel.
- 2026-11-13 T-007 approved by `code-reviewer` (after one fix round), committed and pushed. Owner set quantity limits (#80); design, PRD, architecture and board updated (T-008a added before T-009; #81-#84). T-008 in review.
- 2026-11-13 T-008 approved by `code-reviewer` after three rounds (stray-movement guard, link rules, snapshot reads), committed and pushed. Started T-008a and T-013 in parallel.

## Follow-ups to route

- security-reviewer (phase 6): from T-003, 5 wrong passwords can lock any account (a denial of service inherent in AC-01.4; owner may want to revisit). The timing signal and the lost failure counts were fixed in T-003 (decisions #61, #62); re-verify them.
- qa-engineer (phase 5): displayed percentages follow round(round(x, 6), 2) (decision #58), which can differ from a single rounding in rare cases (e.g. 1.0049996 shows 1.01). Test against the two-step formula. Threshold checks use the unrounded value.
- security-reviewer (phase 6): sign-in has no rate limit; each request costs about 45 ms of CPU. Belongs with the lock-out denial-of-service note.
- ux-designer (small): design 2.4 says the limits keep every computed amount inside the stored range; soften to match architecture risk 12 (an extreme stock total from about a million maximum-size movements is the one exception).
- tech-lead: decide whether AppDbContext.SaveChanges should refuse any Added Movement not written by StockLedger (full 'only writer' guarantee, decision #86). Test helpers that insert movements directly (ItemsTests in-use theory, StockTests AC-10.3) would then need to go through the ledger.
- qa-engineer (phase 5): a location with movements but no change shows 0.0 in stock, while an item with no movements shows 0; both valid per design 2.4 (T-008 review).
- tech-lead (next doc update): add decision #87 (ledger link rules) and #86 (stray-movement refusal) to architecture 3.2/5.1 'contract as built'. BEFORE T-019: decide whether a received return's ReturnedToWarehouse movement copies the return's linked ProductionOrderNumber (affects GET /movements?orderNumber=).
