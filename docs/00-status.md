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
| 4 | Build | done | code | tasks done: 22 of 22 (T-001 to T-021, T-008a) |
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
- 2026-11-13 T-008a (quantity limits, #80) approved by `code-reviewer`, committed and pushed. Started T-009. T-013 in progress.
- 2026-11-14 T-013 approved by `code-reviewer` (after one fix round), committed and pushed. T-009 in progress. Owner: switch to one agent at a time after the next usage-limit stop.
- 2026-11-14 T-009 approved by `code-reviewer` after two rounds (single-snapshot estimate, shared DisplayPercent, unsaved-changes guard), committed and pushed. Started T-010.
- 2026-11-14 T-010 approved by `code-reviewer` after two rounds (shared waiting-order queue #97, body-order errors, log after commit), committed and pushed. Started T-011.
- 2026-11-14 Usage limit reached again; from now on one agent at a time (owner).
- 2026-11-14 T-011 approved by `code-reviewer` after two rounds (worked examples A-D recomputed; shared StoredPercent #99 and OrderLineMatcher #100), committed and pushed. Core flow order -> send -> complete -> compare is in place. Next: order-flow test-helper chore, then T-012.
- 2026-11-14 Chore: shared order-flow test helpers (OrderFlow, #101) approved by `code-reviewer` after one round, committed and pushed. Started T-012.
- 2026-11-14 T-012 approved by `code-reviewer` after two rounds (one-statement read-all, testable tie-order), committed and pushed. Started T-015.
- 2026-11-19 T-015 approved by `code-reviewer` (all five race orderings forced and exact), committed and pushed. Started T-016.
- 2026-11-19 T-016 approved by `code-reviewer` after two rounds (four forced race orderings; shared UnsentComplementaryOrders), committed and pushed. Started T-017.
- 2026-11-19 T-017 approved by `code-reviewer` after two rounds (state machine and races forced; OrderLineMatcher rules option), committed and pushed. Started T-018.
- 2026-11-20 T-018 approved by `code-reviewer` (ledger complementary-parent check #106e; examples A and B through real endpoints), committed and pushed. Started T-019.
- 2026-11-20 T-019 approved by `code-reviewer` after two rounds (all races forced; shared MaterialInUse code), committed and pushed. All PRD features built. Next: T-020.
- 2026-11-20 T-020 approved by `code-reviewer` after two rounds (examples A-D HTTP-only from empty DB; 48-endpoint role matrix; performance tests isolated), committed and pushed. No product bugs found. Started T-021.
- 2026-11-21 T-021 approved by `code-reviewer` after two rounds (48 endpoints documented, accurate required fields, .http walk-through with $dotenv), committed and pushed. Build phase complete.

## Follow-ups to route

- security-reviewer (phase 6): from T-003, 5 wrong passwords can lock any account (a denial of service inherent in AC-01.4; owner may want to revisit). The timing signal and the lost failure counts were fixed in T-003 (decisions #61, #62); re-verify them.
- qa-engineer (phase 5): displayed percentages follow round(round(x, 6), 2) (decision #58), which can differ from a single rounding in rare cases (e.g. 1.0049996 shows 1.01). Test against the two-step formula. Threshold checks use the unrounded value.
- security-reviewer (phase 6): sign-in has no rate limit; each request costs about 45 ms of CPU. Belongs with the lock-out denial-of-service note.
- ux-designer (small): design 2.4 says the limits keep every computed amount inside the stored range; soften to match architecture risk 12 (an extreme stock total from about a million maximum-size movements is the one exception).
- tech-lead: decide whether AppDbContext.SaveChanges should refuse any Added Movement not written by StockLedger (full 'only writer' guarantee, decision #86). Test helpers that insert movements directly (ItemsTests in-use theory, StockTests AC-10.3) would then need to go through the ledger.
- qa-engineer (phase 5): a location with movements but no change shows 0.0 in stock, while an item with no movements shows 0; both valid per design 2.4 (T-008 review).
- tech-lead (next doc update): add decision #87 (ledger link rules) and #86 (stray-movement refusal) to architecture 3.2/5.1 'contract as built'. (Return order link decided: #107.)
- tech-lead (cosmetic): non-numeric number query values (?itemId=abc, ?page=abc) get MVC's message naming the C# property ("...not valid for ItemId."), while the errors key is camelCase. A shared fix would touch PageQuery and every filter (T-013 report).
- ux-designer (next design update): design 7.2's generic '(any quantity)' decimals row should read '{Field} can have at most 3 decimal places.' (decision #95).
- Common/ owner (low priority): a JSON number too large for decimal (e.g. 1e30) is reported by the T-001 framework handler as "{path} has the wrong type.", which is slightly misleading; could say it is too large (T-010 review).
- ux-designer (next design update): design 7.2 "missing line (send or complete)" says "Every order line needs a quantity."; for completion the field is the leftover. Consider "Every order line needs a leftover." for completion (T-011 used the design text as written).
- ux-designer (next design update): design 2.1 says actions such as /read answer 200 with the updated resource, while 2.2, 4 and 7.2 say 204 for notification reads. Built as 204 (T-012); align 2.1.
- tech-lead (next doc update): architecture 3.1 lists the Quantity message helpers; add `Quantity.BelowMinMessage` (T-015, #103).
- Optional comment (next owner of AdjustmentService.cs): explain why AC-12.6's 400 before AC-12.5's 409 never conflicts with design 2.2 (a finished product can never be an order line).
- tech-lead (next doc update): T-016's board wording ('when any of the order's materials has Production storage stock above zero') is broader than AC-21.3 / design 3.8; built per the PRD (only InProduction cancels notify, decision #104a). Tighten the board text.
- ux-designer (next design update): design 7.2 words "item not on the order" two ways (sends: "Item {itemId} is not on order #N."; complementary requests: "{item} is not on order #N."). Built as written (#105a); consider one wording. Also design 5.8 gives no row shape for GET /complementary-orders; built as the full 5.8 shape per row (#105f); confirm.
- ux-designer (next design update): design 7.2 'Missing line (send or complete)' text ('Every order line needs a quantity.') is reused for complementary sends, where 'order line' means the complementary order's lines; confirm the wording (T-018). (T-018 ran examples A and B through the real endpoints; T-020's own AC, A and B from an empty database via HTTP only, still stands.)
- tech-lead (next doc update): architecture 5.1 'contract as built' for StockLedger: add the database-backed complementary-parent check (#106e) alongside #86/#87.
- tech-lead (next doc update): architecture 4.2 says the stock leftover fields are "added in T-019", but T-008 shipped them; reword.
- T-021 (optional): non-numeric id filters (?itemId=abc on returns, movements, complementary orders) get the framework's "The value 'abc' is not valid."; design-style wording everywhere would need a shared fix (see the earlier PascalCase binding note).
- product-manager (wording only): AC-22.6 says "403 for all return actions" while PRD 4.2 lets every role view returns; built per 4.2 and design 4 (list/view open to all, instruct/receive/cancel restricted). Clarify AC-22.6.
- Optional test chore (later): the suite takes ~1.5-2 min on this machine; ~175 per-test factories each migrate a fresh database (~450 ms). Starting from a pre-migrated database copy in TestApiFactory (~230 ms) would cut that, with an opt-out for start-up/migration tests (T-020 measurement).
- qa-engineer (phase 5): without a token, any unknown path answers 401 rather than 404 (the fallback auth policy runs first); consistent with design 2.2's check order (T-021 note).
- devops-engineer / technical-writer (phase 7): .env.example's header says to copy it to .env only as a private note or for Docker; add that the sample requests (InventoryTracker.Api.http) read src/InventoryTracker.Api/.env via $dotenv, not a root .env (T-021 review).
