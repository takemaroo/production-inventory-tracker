# Project status: Production & Inventory Tracker API

Kept current by the delivery manager. Last updated: 2026-11-21

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
| 5 | QA | done | `docs/05-test-plan.md` | Verdict PASS. |
| 6 | Security | done | `docs/06-security-review.md` | Clear to release, with 9 conditions for phase 7 (report 7.2). |
| 7 | Release | done | `docs/07-release.md`, `README.md` | Approved by owner 2026-11-22 and pushed. |

Status values: `not started`, `in progress`, `waiting on owner`, `done`.

## Version 1.1: sales orders

| # | Phase | Status | Output |
|---|-------|--------|--------|
| 1 | Discovery | done | `docs/01-prd.md` (Version 1.1 part) |
| 2 | Design | done | `docs/02-design.md` |
| 3 | Architecture | done | `docs/03-architecture.md`, `docs/04-tasks.md` |
| 4 | Build | done | code (tasks done: 12 of 12, T-022 to T-033, plus the T-031 fix and the BUG-002 fix) |
| 5 | QA | done | `docs/05-test-plan.md` (Part 2: verdict PASS; BUG-002 fixed and closed) |
| 6 | Security | done | `docs/06-security-review.md` (Part 2: clear to release localhost-only, 5 phase 7 conditions; SEC-24/25/26/29 settled, #175-#178; SEC-26 log line done in T-033) |
| 7 | Release | done, waiting on owner approval to push | `docs/09-release-notes.md`, README |

## Waiting on the owner

- Approval to push the v1.1 release commits (phase 7) to `main`.

## Next step

Owner approves (or not) pushing the v1.1 release commits. After that: the SEC-25 name-rules task (#176), then the going-public preparation and the web front end.

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
- 2026-11-21 Docs cleaned up before QA (architecture, design, PRD wording; decisions #112-#115).
- 2026-11-21 Phase 5 QA (focused, #111): 130 ACs traced, 33 acceptance cases added, BUG-001 (minor, emoji length counting) fixed (#116), password length fixed (#118), re-tested and closed. Verdict PASS. Started phase 6.
- 2026-11-21 `security-reviewer`: go with conditions (SEC-01..SEC-17). Owner settled Q47-Q49 (#119-#121: localhost-only v1, no rate limit, deactivate-to-remove runbook).
- 2026-11-21 SEC-01 and SEC-02 fixed (1 MB body cap, 500-line and 10-role caps before the write lock, 100-message cap, unreadable bodies 413/408/400 at Information; #122-#126). Approved by `code-reviewer` after two rounds; 1,721 tests pass. Committed and pushed.
- 2026-11-21 Design, architecture and PRD updated for the request limits (#127); comment tidy approved by `code-reviewer`, committed and pushed. Next: security re-check of SEC-01/SEC-02.
- 2026-11-21 `security-reviewer` re-check: SEC-01 and SEC-02 fixed and verified live; verdict clear to release with 9 phase 7 conditions; SEC-18 (info) added to Later. Phase 6 done. Started phase 7 with `devops-engineer`.
- 2026-11-22 `devops-engineer` finished scripts, CI, `docs/07-release.md` and `docs/07a-cloud-guide.md` (Azure; #128-#133). Approved by `code-reviewer` after two rounds (backup/restore refuse while the API runs; relative publish folder). Committed locally; phase 7 is not pushed until the owner approves the release.
- 2026-11-22 `tech-lead` updated architecture for phase 7 (SEC-12 placeholder, Production configuration #128, hosting). Committed locally. Next: `technical-writer`.
- 2026-11-22 `technical-writer`: README rewritten, user guide (08) and release notes (09) (#134); .http sample passwords moved to .env (#135). Final security check: release approved for localhost-only v1, all nine conditions met; SEC-19 and SEC-20 fixed and re-checked. Committed locally; waiting on owner approval to push.
- 2026-11-22 Owner approved the release; design-taste-frontend skill untracked (#136). Pushed to main.
- 2026-11-22 First CI run on GitHub passed (about 7 min). Owner merged Dependabot PRs #1 (checkout 7.0.1) and #2 (setup-dotnet 6.1.0); both passed CI. v1 complete.
- 2026-11-22 Owner's first real start done (first password changed, first backup made). Owner installed SDK 10.0.404 with runtime 10.0.15: SEC-06 met on this machine. CD added to the PRD Later list (#137).
- 2026-11-22 Small fixes from the first start: run.ps1 explains a saved-but-not-loaded key; new-signing-key.ps1 -SaveForUser replaces the unsafe capture form (it could save PowerShell's banner as the key); keys with spaces refused; sign-in and change-password snippets show the API's message. Approved by `code-reviewer` after two rounds; security re-check OK (SEC-22 Later, SEC-23 done). Committed and pushed.
- 2026-11-23 Owner walked through the production cycle (user guide sections 3-10). Owner chose the Later-list order: sales orders (v1.1), then a web front end, then the cloud path. Started v1.1 phase 1 with `product-manager`.
- 2026-11-23 Owner answered Q50-Q57 (#138-#145) and settled the rest in a grilling session (Q58-Q81, #146-#151). Owner approved the v1.1 PRD. Started v1.1 phase 2 with `ux-designer`.
- 2026-11-23 `ux-designer` finished the v1.1 design (62 endpoints, role-based reads, flows, messages; #152-#153). Started v1.1 phase 3 with `tech-lead`.
- 2026-11-24 `tech-lead` finished v1.1 architecture and tasks T-022 to T-032 (#154-#164), checked against a prototype (migration on a 100,000-movement v1 database in 180 ms, one transaction; reports 1-90 ms at PRD volume). Existing suite 1,721/1,721. Waiting on owner approval.
- 2026-11-24 Owner approved v1.1 phase 3. Started T-022.
- 2026-11-24 T-022 approved by `code-reviewer` (hand-edited one-transaction migration verified; comment fixes), committed and pushed. Architecture 3.1 corrected (rolled-back inserts reuse a number on SQLite). Started T-023.
- 2026-11-24 T-023 approved by `code-reviewer` (Seller role, role-based reads on nine v1 endpoints, six-caller role matrix; review tidy-ups), committed and pushed. Started T-024.
- 2026-11-24 T-024 approved by `code-reviewer` (customers; race test verified by mutation; review tidy-ups), committed and pushed. Design 7.2 note corrected for #152. Started T-025.
- 2026-11-24 T-025 approved by `code-reviewer` (create, list and view sales orders; customer race forced both ways; comment fixes), committed and pushed. Started T-026.
- 2026-11-24 T-026 approved by `code-reviewer` (ShippedToCustomer ledger rules, salesOrderNumber on movements and filter, DayRange), committed and pushed. Started T-027.
- 2026-11-24 T-027 approved by `code-reviewer` (ship: all-lines stock check, Warehouse only, both races forced, one clock read), committed and pushed. Started T-028.
- 2026-11-24 T-028 approved by `code-reviewer` after two rounds (cancel; ship-vs-cancel race forced both ways; shared sales test helpers #166; #165), committed and pushed. Started T-029.
- 2026-11-24 T-029 approved by `code-reviewer` (open demand in the stock snapshot; mutation-checked), committed and pushed. Started T-030.
- 2026-11-24 T-030 approved by `code-reviewer` after two rounds (shipped-totals reports; expression-tree helper reverted to plain copies, #167), committed and pushed. Started T-031.
- 2026-11-24 T-031 approved by `code-reviewer` (HTTP-only sales chain, Seller's day, 7 races, performance at v1.1 volume: slowest p95 ~0.1 s; #168), committed and pushed. Review confirmed a performance issue: the sales order detail and ship loads scan every sales order line (fix before release, AsSplitQuery). Started that fix.
- 2026-11-24 Fix approved by `code-reviewer`: sales order detail and ship loads use AsSplitQuery and the index; GET in a read transaction (#169); query-plan regression tests. p95 detail 37→2 ms, ship 92→23 ms. Committed and pushed. Started T-032.
- 2026-11-25 T-032 approved by `code-reviewer` (OpenAPI 62 operations pinned; v1.1 .http walk-through replayed, 83 requests as expected), committed and pushed. v1.1 build phase complete. Disk was full: cleared the git-ignored TestResults build output (7.3 GB). Started v1.1 QA.
- 2026-11-25 Phase 5 QA (v1.1): verdict PASS; 2 acceptance files added (item race, open-demand zero form). BUG-002 (minor, look-alike Unicode names) settled with the owner in a grilling session (Q82-Q86, #170-#172): NFC names, invisible-only names refused, no data migration, fix before release. Fixed for customers, units and items in a shared NameRules helper (#173-#174, U+FFFE refused with 400), approved by `code-reviewer` after two rounds; design and architecture updated; QA re-tested (287 live checks) and closed it. 2,503 tests pass. Next: phase 6.
- 2026-11-25 `security-reviewer` (v1.1): clear to release localhost-only; no critical/high/medium findings; SEC-24..SEC-31 (low/info); 5 phase 7 conditions (report 13.2). Owner questions put in a grilling session.
- 2026-11-25 Owner settled the security questions in a grilling session (Q87-Q90, #175-#178): SEC-24/25 and the stricter SEC-26/29 rules to the Later list; customer change log lines now. T-033 (log who changed a customer, #179) approved by `code-reviewer`, committed and pushed. Phase 6 done. Next: phase 7.
- 2026-11-25 `devops-engineer` (v1.1 release): upgrade and way-back steps in 07-release.md section 6, rehearsed end to end on scratch copies (#180); v1 starts silently on a v1.1 database, so a refuse-unknown-migrations check went on the Later list. Approved by `code-reviewer` after two rounds (the way back swaps the publish folders so the daily start runs v1). Committed locally.
- 2026-11-25 `technical-writer` (v1.1): README, user guide (sales chain sections 15-21, Seller column) and release notes v1.1; every guide PowerShell block run against a throwaway instance. Final security check: release approved for localhost-only v1.1, all five conditions met (F1-F4 wording fixed). Committed locally; waiting on the owner to approve the push.

## Follow-ups to route

- security-reviewer (phase 6): from T-003, 5 wrong passwords can lock any account (a denial of service inherent in AC-01.4; owner may want to revisit). The timing signal and the lost failure counts were fixed in T-003 (decisions #61, #62); re-verify them.
- security-reviewer (phase 6): sign-in has no rate limit; each request costs about 45 ms of CPU. Belongs with the lock-out denial-of-service note.
- Common/ owner (low priority): a JSON number too large for decimal (e.g. 1e30) is reported by the T-001 framework handler as "{path} has the wrong type.", which is slightly misleading; could say it is too large (T-010 review).
- Optional comment (next owner of AdjustmentService.cs): explain why AC-12.6's 400 before AC-12.5's 409 never conflicts with design 2.2 (a finished product can never be an order line).
- T-021 (optional): non-numeric id filters (?itemId=abc on returns, movements, complementary orders) get the framework's "The value 'abc' is not valid."; design-style wording everywhere would need a shared fix (see the earlier PascalCase binding note).
- Optional test chore (later): the suite takes ~1.5-2 min on this machine; ~175 per-test factories each migrate a fresh database (~450 ms). Starting from a pre-migrated database copy in TestApiFactory (~230 ms) would cut that, with an opt-out for start-up/migration tests (T-020 measurement).
- backend-developer (later tidy, not v1): UserService and PasswordService log inside the write transaction just before commit (other services log after commit); a failed commit could leave a log line for an unsaved change. Recorded in architecture 6 (tech-lead clean-up).
- security-reviewer (phase 6): from QA, item names accept a tab and a NUL character; HTML-like text is returned unescaped in JSON (application/json). PRD forbids neither; judge the risk. Also: writes under many parallel clients at full volume were not load-tested (SQLite serialises writers, architecture risk 3).
- Cosmetic (later): GET /production-orders/99999999999 (beyond int range) returns 404 "No endpoint matches this path." instead of "Order #... not found." (QA note).
- Optional comment nits (later, from the comment-tidy review): CheckLineCount remarks also cite #123; RequestTimeoutMessage and UnreadableBodyMessage cite #124; RequestBodyLimitTests comment says TestOutputHelper (not TestContext.Current) is null outside a test; TooManyRolesMessage points to design 7.2.
- Optional script nits (later): Test-FileInUse description says the lock check fails "exactly when the API is running" (not true when idle); 07-release section 10 row could mention a running build of the API project matching the process check.
- backend-developer (Later, SEC-22): move the signing-key rule into the API's start-up checks (StartupChecks.CheckSettings): characters A-Z a-z 0-9 + / = - _, at least 43 characters, at least 10 different; keep run.ps1's check in step; tests for the banner, a repeated letter and a generated key.
- technical-writer / T-032 (v1.1): docs/08-user-guide.md:158 and InventoryTracker.Api.http:154 still use the v1 item-in-use wording; the message now includes "sales order lines".
- After v1.1 (owner, 2026-11-24): prepare the repo for going public: full-history secret scan (security-reviewer), LICENSE (owner to choose; suggested "all rights reserved", view for evaluation only), README "About this project" (AI-team approach, stated honestly), and GitHub's private-email setting. The owner changes visibility.
- Later tidy (T-027 review nit): the stem "Not enough stock: {item} in {location} has {n} {unit}" exists in both StockLedger.InsufficientStockMessage and ShipSalesOrderService; a shared stem helper in StockLedger would give one source.
- Test chore (later): the performance timing loop exists in four test files (MovementsTests, StockTests, Acceptance/PerformanceAcceptanceTests, SalesPerformanceTests); one shared Infrastructure helper (#101).
- Later sweep: the v1 write-path loads with Include chains (ComplementaryOrderService, ComplementarySendService, RequestComplementaryOrderService, CancelOrderService, CompleteOrderService, SendOrderService) have the same single-query shape as the sales order detail; add AsSplitQuery (lower impact: small line tables).
- technical-writer (v1.1 release): README's .env section lists five names; add SAMPLE_SELLER_PASSWORD and the Seller walk-through (sections 12-17).
- Test tidy (later, not v1): third copy of the 100,000-movement volume seed in Acceptance/PerformanceAcceptanceTests; private AdjustAsync in StockZeroForms/NotificationEvents acceptance tests duplicates ScenarioApi.AdjustAsync; fold into OrderFlow/Seed (BUG-001 review).
- technical-writer (v1.1 release, #172): release notes must say names stored before v1.1 are not rewritten; a name stored in decomposed form can gain one look-alike twin (QA, BUG-002 re-test).
- Optional comment nit (later, BUG-002 review): NameNormalizer.Normalize remarks could say it throws ArgumentException on U+FFFE (#174), so request input must go through NameRules first.
- security-reviewer (v1.1 phase 6): names made only of U+3164 (Hangul filler), U+2800, U+FFFF or U+FDD0 are accepted and can look blank (#173/#174 allow them); judge the risk.
- Optional comment nit (later, T-033 review): CustomerService DeactivateAsync, ReactivateAsync and DeleteAsync could document userId with <param> tags, as UpdateAsync does.
- security-reviewer (next review): SEC-26 in docs/06-security-review.md is still marked open; its log-line part was done in T-033 (#177, #179), the "last changed by" columns are on the Later list.
- Practice note for every agent doing scratch runs: redirecting LOCALAPPDATA does not isolate ASP.NET Core DataProtection; the API still reads the owner's %LOCALAPPDATA%\ASP.NET\DataProtection-Keys (read only so far, no product use of the keys). Until the key-folder setting on the Later list exists, accept it; never write or delete there.
- tech-lead (when the web front end or cloud path is planned): add explicit DataProtection configuration to the requirements (final v1.1 security check; PRD Later list).
