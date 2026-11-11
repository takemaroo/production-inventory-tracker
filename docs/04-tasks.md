# Task board: Production & Inventory Tracker API

Based on `docs/01-prd.md`, `docs/02-design.md` and `docs/03-architecture.md`. Status values: `todo`, `in progress`, `in review`, `done`, `blocked`. The delivery manager updates the Status column; the tech lead owns everything else.

There is no front end in v1, so every task is owned by `backend-developer` and reviewed by `code-reviewer`.

| ID | Task | Owner | Depends on | Status |
|----|------|-------|------------|--------|
| T-001 | Common foundations: errors, paging, quantities, validation, clock | backend-developer | none | todo |
| T-002 | Data model, domain migration, write transaction | backend-developer | none | todo |
| T-003 | Authentication, roles, first Manager, start-up checks | backend-developer | T-001, T-002 | todo |
| T-004 | Production calculations (pure functions D-3 to D-9) | backend-developer | T-001 | todo |
| T-005 | Notification messages and data (pure) | backend-developer | T-001, T-002 | todo |
| T-006 | Units and items | backend-developer | T-003 | todo |
| T-007 | Recipes | backend-developer | T-006 | todo |
| T-008 | Stock ledger, receipts, current stock | backend-developer | T-004, T-006 | todo |
| T-009 | Create and view production orders | backend-developer | T-007, T-008 | todo |
| T-010 | Send materials for an order | backend-developer | T-009 | todo |
| T-011 | Complete an order: output, variances, notifications | backend-developer | T-005, T-010 | todo |
| T-012 | Notifications API | backend-developer | T-011 | todo |
| T-013 | Movement history | backend-developer | T-008 | todo |
| T-014 | User management and own password | backend-developer | T-003 | todo |
| T-015 | Adjustments and write-offs | backend-developer | T-010 | todo |
| T-016 | Cancel an order | backend-developer | T-011 | todo |
| T-017 | Complementary orders: request, approve, reject, view | backend-developer | T-005, T-010 | todo |
| T-018 | Complementary order send | backend-developer | T-017 | todo |
| T-019 | Leftover returns | backend-developer | T-010 | todo |
| T-020 | End-to-end scenarios, role matrix, concurrency | backend-developer | T-012 to T-019 | todo |
| T-021 | OpenAPI completeness and sample requests | backend-developer | T-012 to T-019 | todo |

**Thin slice first.** T-001 to T-012 give a working path through every layer: sign in, set up master data, receive stock, create an order, send, complete, read the notifications. T-013 to T-019 add breadth. T-020 and T-021 close out.

**Running tasks at the same time.** Tasks can run in parallel when their dependencies are done and their "Touches" lists do not overlap. Safe pairs once their dependencies are met include: T-001 with T-002; T-004 with T-002 or T-003; T-005 with T-003 or T-004; T-007 with T-008; T-013 and T-014 with almost anything; T-017 with T-011; T-019 with T-011, T-015 or T-017. T-010, T-011 and T-016 share `ProductionOrdersController.cs` and are chained on purpose.

## Done criteria for every task

Each task is done only when all of these hold, as well as its own criteria below:

- `dotnet build` from the repository root gives **0 errors and 0 warnings**, and `dotnet test` passes in full. The developer's report includes both commands and their results.
- **Code is commented to the owner's comment standard (CLAUDE.md, "Code comments for learning")**: a `///` summary on every class and public method or endpoint, the *why* and the PRD id beside each business rule, a short explanation the first time a framework concept appears, and a comment on each test naming the acceptance criterion it proves. `code-reviewer` checks this.
- Status codes, `code` values and messages match `docs/02-design.md` section 7 exactly where the PRD gives the wording, and keep the same facts elsewhere. Checks run in the design's order (design 2.2, architecture 5.1).
- Every request that changes data runs inside `InWriteTransactionAsync`, and every movement is written through `StockLedger` (architecture 5.1, 5.2).
- Every acceptance criterion listed under "Serves" has at least one test, and every new endpoint has a `401` test (no token) and a `403` test (a role without access).
- No secrets in tracked files. No changes to `docs/01-prd.md`, `docs/02-design.md` or `docs/03-architecture.md`. If the task does not fit the architecture, stop and report it so the tech lead can change the document first.
- The delivery manager commits each task as `T-0NN: short description` after review.

## Task details

### T-001: Common foundations

- **Owner:** backend-developer
- **Serves:** PRD 4.1 conventions (quantities, rounding, lists, "rejected" format); design 2.4, 2.5, 6, 6.1; architecture 4.1 items 2, 3, 8, 9 and 6 "Error handling", "Validation", "Time"
- **Depends on:** none
- **Touches:** `src/InventoryTracker.Api/Common/` (Errors, Paging, Quantities, Validation, Time), `src/InventoryTracker.Api/Program.cs`, `tests/InventoryTracker.Api.Tests/Unit/Common/`, `tests/InventoryTracker.Api.Tests/Integration/ErrorHandlingTests.cs`, `tests/InventoryTracker.Api.Tests/Infrastructure/` (test-only probe controller)
- **What to build:** The shared building blocks every feature uses. These are the `ApiException` types and one `IExceptionHandler` that writes ProblemDetails with `code`, a fixed `title` per status and `traceId`, plus the `InvalidModelStateResponseFactory` for unreadable bodies and ProblemDetails for bodiless `404`/`405`. Also: `ValidationErrors` (collects messages keyed by JSON path, `detail` = messages joined with a space), enum and date parsers with the design's messages, `PageQuery`/`PagedResult<T>`, `Quantity.Round3`/`HasAtMostDecimals`, `MessageFormat` (design 6.1), and `Clock` over `TimeProvider`, registered in DI.
- **Acceptance criteria:**
  - A `NotFoundException`/`ConflictException`/`ValidationException` thrown in an action produces the design 6 body: correct status, `title`, `code`, `detail`, `traceId`, and `errors` for validation. Content type is `application/problem+json`.
  - An unhandled exception gives `500` `internal-error` "An unexpected error occurred." with no stack trace in the body, and the exception is logged.
  - Unknown route: `404` `not-found`. Wrong method: `405` `method-not-allowed`. Malformed JSON: `400` `validation` "The request body is not valid JSON.". Number sent as a string: `400` `validation` (architecture 4.1 item 2).
  - `HasAtMostDecimals(12.5000m, 3)` is true; `HasAtMostDecimals(0.0001m, 3)` is false. `Round3(1.0005m)` = 1.001 and `Round3(-1.0005m)` = -1.001.
  - `MessageFormat`: 1015 → `1,015`; 974.999 → `974.999`; 0.6 → `0.6`; 0 → `0`; percent 2.9 → `+2.90%`, −2.5 → `-2.50%`; signed 29 → `+29`.
  - Paging: `page` < 1 or `pageSize` outside 1–100 gives `400` with the design 7.2 messages; defaults are 1 and 50; the envelope is `{ items, page, pageSize, totalCount }`.
  - `Clock.UtcNow` is UTC with whole seconds.
  - Tests: unit tests for every helper; integration tests through a test-only probe controller, added to the app as an application part from the test project, for each error shape.

### T-002: Data model, domain migration, write transaction

- **Owner:** backend-developer
- **Serves:** D-2, D-3 (stored fields), AC-05.3 (audit columns), AC-11.3 (no update path), AC-15.10 (unique InProduction index), PRD section 5 consistency
- **Depends on:** none
- **Touches:** `src/InventoryTracker.Api/Domain/` (except `Calculations/`), `src/InventoryTracker.Api/Data/` (AppDbContext, Configurations, Migrations, WriteTransaction.cs), `tests/InventoryTracker.Api.Tests/Integration/DataModelTests.cs`
- **What to build:** Replace the placeholder `Item`, `Movement` and `UnitOfMeasure` classes and add every entity and enum in architecture section 3, with one `IEntityTypeConfiguration` per entity (lengths, precision, indexes, `Restrict` deletes, enums as strings, `NameNormalized` unique indexes, the filtered unique index on InProduction). Add the model-wide UTC `DateTime` converter in `ConfigureConventions`, the `DbSet`s, and one migration named `DomainModel`. Add `InWriteTransactionAsync` (architecture 5.2).
- **Acceptance criteria:**
  - `dotnet tool run dotnet-ef migrations add DomainModel ...` produces one migration; the app starts on an empty database and on a database that already has `InitialIdentity`.
  - Enum names in the tables match design 2.3 exactly.
  - A `DateTime` saved as UTC is read back with `Kind = Utc`.
  - Inserting a second ProductionOrder with status InProduction fails with `DbUpdateException`.
  - Two units whose names differ only in case cannot both be saved (unique `NameNormalized`).
  - `InWriteTransactionAsync`: an exception inside the work saves nothing. While one write transaction is open, a second one on another `DbContext` waits until the first commits, and then sees the committed data (test with two scopes and a `TaskCompletionSource`).
  - The start-up warning "No instantiatable types implementing IEntityTypeConfiguration" is gone.
  - Tests for each of the above in `DataModelTests.cs`.

### T-003: Authentication, roles, first Manager, start-up checks

- **Owner:** backend-developer
- **Serves:** US-01 (AC-01.1 to AC-01.5), US-02 (AC-02.1 to AC-02.5), US-05 (AC-05.1, AC-05.2 mechanism), PRD section 5 (password policy, tolerance range, secrets from environment)
- **Depends on:** T-001, T-002
- **Touches:** `src/InventoryTracker.Api/Auth/`, `src/InventoryTracker.Api/Common/Configuration/JwtOptions.cs`, `src/InventoryTracker.Api/Program.cs`, `tests/InventoryTracker.Api.Tests/InventoryTracker.Api.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing` 10.11.0), `tests/InventoryTracker.Api.Tests/Infrastructure/`, `tests/InventoryTracker.Api.Tests/Integration/AuthTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/StartupTests.cs`
- **What to build:** Identity core with the password and lock-out options, JWT issuing and validation, `POST /api/v1/auth/sign-in`, the per-request user check (active and security stamp), the fallback "signed-in" policy, the `Roles` constants (`Manager`, `WarehouseOrManager`, `ProductionOrManager`), and the `401`/`403` ProblemDetails handlers (architecture 6). Also the start-up sequence in architecture 6 with `StartupException` and exit code 1. Extend `TestApiFactory` with test values for `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD`, a swappable `FakeTimeProvider`, and helpers `CreateUserAsync(email, roles)` and `CreateClientAsAsync(roles)` that later tasks use.
- **Acceptance criteria:**
  - AC-01.1: sign-in returns `200` with `accessToken`, `tokenType` "Bearer", `expiresAt` (now + 8 h, UTC), `email`, `roles`.
  - AC-01.2: a token used after 8 hours (FakeTimeProvider) gets `401` `not-authenticated`.
  - AC-01.3: wrong password, unknown email and inactive user all give `401` "Invalid email or password.".
  - AC-01.4: after 5 failures, the right password gives `401` "Account temporarily locked. Try again later."; after 15 minutes it works again.
  - AC-01.5: a probe endpoint without a token, or with a garbage or wrongly signed token, gives `401` with the ProblemDetails body and `WWW-Authenticate: Bearer`.
  - Deactivating a user directly in the database, or changing their security stamp, makes their existing token get `401` (the mechanism behind AC-03.6).
  - A probe endpoint with `[Authorize(Roles = Roles.WarehouseOrManager)]` gives `403` "Your roles do not allow this action. It needs the Warehouse or Manager role." to a Production-only user, and `200` to a user with Warehouse and Production (AC-05.2).
  - AC-02.1: an empty database plus both variables gives one active user with only the Manager role, who can sign in. AC-02.2: restarting with different values changes nothing.
  - AC-02.3 and AC-02.4: a missing or empty variable, or a weak password, stops start-up with the design 7.4 message (captured from the log), and the database has no users.
  - A missing or short `JWT_SIGNING_KEY`, or a tolerance outside 0–100, stops start-up with the design 7.4 message.
  - AC-02.5: `.env.example` lists `ADMIN_EMAIL`, `ADMIN_PASSWORD`, `JWT_SIGNING_KEY` with empty values (already true; keep it so).
  - Architecture section 8 commands work: user-secrets, then `dotnet run`, then sign in with the admin account (manual check, described in the report).

### T-004: Production calculations (pure functions)

- **Owner:** backend-developer
- **Serves:** D-3 to D-9; AC-08.3, AC-13.1 (planned amount), AC-13.2 (estimates), AC-15.2 to AC-15.5, AC-18.1, AC-18.2, AC-20.3 to AC-20.8
- **Depends on:** T-001
- **Touches:** `src/InventoryTracker.Api/Domain/Calculations/`, `tests/InventoryTracker.Api.Tests/Unit/Calculations/`
- **What to build:** Static, database-free functions: `PlannedAmount`; `SendRange(planned, leftoverApplied, t)` returning min, max and nominal (D-7, rounded limits per architecture 4.1 item 4); `ComplementaryRange(requested, t)`; `IsWithin`; `Committed` (D-3); `FreeLeftover` (D-4); `LeftoverAllocator.Allocate` (D-5: per material, Created orders by CreatedAt then Number, each takes min(planned, remaining)); `CarriedLeftover` (D-8); `CompleteLine` (D-9: required, remaining, actual usage, variance, unrounded percent, above-tolerance flag); `OutputVariance`.
- **Acceptance criteria:**
  - Unit tests reproduce every number in AC-15.2, AC-15.3, AC-15.4, all three bullets of AC-15.5, AC-18.2, worked examples A, B, C and D (AC-20.5 to AC-20.8), and AC-08.3.
  - AC-20.4: a line at exactly 2.00% is not above tolerance; a line at 2.0001% is above (shown as 2.00).
  - Computed quantities round half away from zero to 3 places; tolerance 0 gives a window of exactly the nominal send.
  - No EF Core, HTTP or `DateTime.Now` in `Domain/Calculations`.

### T-005: Notification messages and data (pure)

- **Owner:** backend-developer
- **Serves:** US-23 (AC-23.2 content), AC-19.7, AC-20.2, AC-20.4, AC-16.1, AC-21.3 (content only)
- **Depends on:** T-001, T-002
- **Touches:** `src/InventoryTracker.Api/Features/Notifications/NotificationComposer.cs`, `tests/InventoryTracker.Api.Tests/Unit/NotificationComposerTests.cs`
- **What to build:** A static `NotificationComposer` with one method per notification type. Each returns a `Notification` entity (not saved) with `Type`, order number, complementary order number where relevant, `Message` from the design 7.3 templates, and `DataJson` holding the design 5.10 `data` object (camelCase). Materials are listed in recipe line order.
- **Acceptance criteria:**
  - The five design 7.3 example messages are reproduced exactly from their inputs, for example "Order #12, Cheese: used 1,030 kg, required 1,001 kg for actual output. Difference +29 kg (+2.90%)."
  - `DataJson` for each type deserializes to exactly the field names in design 5.10.
  - Unit tests for all five types.

### T-006: Units and items

- **Owner:** backend-developer
- **Serves:** US-06 (AC-06.1 to AC-06.4), US-07 (AC-07.1 to AC-07.5), AC-05.1 for these endpoints
- **Depends on:** T-003
- **Touches:** `src/InventoryTracker.Api/Features/Units/`, `src/InventoryTracker.Api/Features/Items/`, `tests/InventoryTracker.Api.Tests/Integration/UnitsTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/ItemsTests.cs`
- **What to build:** `GET/POST /units`, `GET/PUT/DELETE /units/{id}`, `GET/POST /items` (filter `type`), `GET/PUT/DELETE /items/{id}`, as in design 5.3 and 5.4. Reads are for any signed-in user; changes are for Manager only.
- **Acceptance criteria:**
  - Every criterion of US-06 and US-07, including names trimmed and compared without case, and `409` "Unit is used by one or more items.".
  - AC-07.3 and AC-07.4: the "in use" checks use every reference listed in architecture 3.2 (Item). Tests insert a movement and an order line directly through the DbContext.
  - Lists are paged and newest first. `201` responses carry `Location`.
  - `401` and `403` tests for each changing endpoint.

### T-007: Recipes

- **Owner:** backend-developer
- **Serves:** US-08 (AC-08.1 to AC-08.3)
- **Depends on:** T-006
- **Touches:** `src/InventoryTracker.Api/Features/Recipes/`, `tests/InventoryTracker.Api.Tests/Integration/RecipesTests.cs`
- **What to build:** `GET /items/{id}/recipe` and `PUT /items/{id}/recipe` (design 5.4): replace the whole line list, store `LineNo` in submitted order, set `UpdatedAt`/`UpdatedById`.
- **Acceptance criteria:**
  - Every `400` case in AC-08.2, each with its design 7.2 message and the line's JSON path in `errors`.
  - `GET` for a finished product with no recipe, or for a raw material: `404` "{item} has no recipe.".
  - Saving again replaces all previous lines (AC-08.1). The AC-08.3 example round-trips with units shown.
  - AC-08.4 (orders keep their snapshot) is proved in T-009.

### T-008: Stock ledger, receipts, current stock

- **Owner:** backend-developer
- **Serves:** US-09 (AC-09.1 to AC-09.4), US-10 (AC-10.1 to AC-10.4), D-2, D-4, AC-11.4 (stock = sum of movements)
- **Depends on:** T-004, T-006
- **Touches:** `src/InventoryTracker.Api/Features/Stock/` (StockLedger, StockQueries, StockController, ReceiptsController, contracts), `tests/InventoryTracker.Api.Tests/Integration/StockTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/ReceiptsTests.cs`
- **What to build:** `StockLedger.AddAsync` (architecture 5.1): the only writer of movements, refusing any movement that takes a location below zero. `StockQueries`: per-item Warehouse and Production storage stock, committed (D-3, from the InProduction order's lines), pending returns, and free leftover (D-4), calculated for just the requested items. `POST /receipts`, `GET /stock` (filter `itemType`, paged), `GET /stock/{itemId}` (design 5.5 and 5.6).
- **Acceptance criteria:**
  - Every criterion of US-09, including "Receipt date cannot be later than today (UTC)." and "Receipts are for raw materials only.".
  - AC-10.1, AC-10.2, AC-10.4. AC-10.3: committed, pending and free fields are correct when an InProduction order and a Pending return are inserted directly through the DbContext. Finished products have `null` in those four fields.
  - The ledger refuses a movement that would take either location below zero, with `409` `insufficient-stock` and the design 7.2 message.
  - Concurrency: 10 parallel receipts all succeed and the stock equals their sum.
  - The receipt response is the movement shape of design 5.6, with `Location: /api/v1/movements/{id}`.

### T-009: Create and view production orders

- **Owner:** backend-developer
- **Serves:** US-13 (AC-13.1 to AC-13.7), US-14 (AC-14.1 to AC-14.3), AC-08.4, AC-15.5 (estimate bullet)
- **Depends on:** T-007, T-008
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/` (ProductionOrdersController, ProductionOrderService, ProductionOrderContracts, mapping), `tests/InventoryTracker.Api.Tests/Integration/ProductionOrdersTests.cs`
- **What to build:** `POST /production-orders` (recipe snapshot, planned amounts, no stock moved), `GET /production-orders` (filter `status`, summary shape), `GET /production-orders/{number}` (full detail of design 5.7). The detail includes `estimate` per line while Created (D-5 and D-7, via T-004 and `StockQueries`), `history` built from the timestamp pairs, and `complementaryOrders` read from their table.
- **Acceptance criteria:**
  - Every criterion of US-13 and US-14. Every field in design 5.7 is present, with `null` for unknown values.
  - AC-08.4: change the recipe after creating an order; the order keeps its amounts, and a new order uses the new recipe.
  - AC-15.5 estimates: with 50 kg of free leftover (set up through a receipt and a ledger movement in the test) and orders #7, #8, #9, the three estimates match the PRD.
  - AC-13.4: creating an order creates no movement.

### T-010: Send materials for an order

- **Owner:** backend-developer
- **Serves:** US-15 (AC-15.1 to AC-15.12), D-6, D-7, D-8, PRD section 5 (concurrent sends)
- **Depends on:** T-009
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/SendOrderService.cs`, `src/InventoryTracker.Api/Features/ProductionOrders/ProductionOrdersController.cs` (one action), `ProductionOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/SendOrderTests.cs`
- **What to build:** `POST /production-orders/{number}/send`, following design 2.2 and architecture 5.2 inside one write transaction. Checks: order exists; lines complete and well-formed; status Created; no other InProduction order; no Pending return for any of its materials; ranges; Warehouse stock. Then fix LeftoverApplied, WarehouseSend, CarriedLeftover and ComplementarySends = 0 per line, add one SentToProduction movement per line above zero, and set InProduction with SentAt/SentById.
- **Acceptance criteria:**
  - Every criterion of US-15 with the exact PRD messages, including AC-15.4 (send 0, no movement, carried leftover 20) and the AC-15.5 "send #8 first" bullet (leftover applied 20, not 50).
  - AC-15.6: all failing lines are named in one `400` `send-out-of-range`, with `errors` per line.
  - AC-15.11: a Pending return inserted through the DbContext blocks the send with the PRD message.
  - Concurrency: two simultaneous sends of the same order give exactly one `200`. Simultaneous sends of two different orders give exactly one `200` and one `409` `another-order-in-production`. Warehouse stock is never negative.

### T-011: Complete an order: output, variances, notifications

- **Owner:** backend-developer
- **Serves:** US-19 (AC-19.1 to AC-19.8), US-20 (AC-20.1 to AC-20.10), AC-23.2 (first three types created)
- **Depends on:** T-005, T-010
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/CompleteOrderService.cs`, `ProductionOrdersController.cs` (one action), `ProductionOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/CompleteOrderTests.cs`
- **What to build:** `POST /production-orders/{number}/complete` in one write transaction. It creates the ProducedOutput movement and ConsumedInProduction movements (none for zero), fills every completion field on the order and its lines through T-004, stores the tolerance in force, and creates the OutputVariance, UsageVariance and LeftoverAtCompletion notifications through T-005. It also cancels the order's PendingApproval and Approved complementary orders with "Parent order completed" (AC-19.6).
- **Acceptance criteria:**
  - Every criterion of US-19 and US-20, with the exact PRD messages for AC-19.4.
  - Worked examples C (AC-20.7) and D (AC-20.8) through HTTP: every line figure, the movements, Production storage stock afterwards, and the notifications created. Examples A and B need complementary orders and are covered in T-020.
  - AC-19.6: complementary orders inserted through the DbContext in PendingApproval and Approved become Cancelled with the note; Rejected and Sent ones are unchanged.
  - AC-20.10: changing the tolerance setting after completion does not change the stored values.
  - Concurrency: two simultaneous completions give exactly one `200`.

### T-012: Notifications API

- **Owner:** backend-developer
- **Serves:** US-23 (AC-23.1, AC-23.3 to AC-23.6)
- **Depends on:** T-011
- **Touches:** `src/InventoryTracker.Api/Features/Notifications/` (except NotificationComposer.cs), `tests/InventoryTracker.Api.Tests/Integration/NotificationsTests.cs`
- **What to build:** `GET /notifications` (filter `unreadOnly`, newest first, paged, `isRead` for the caller), `GET /notifications/{id}`, `POST /notifications/{id}/read` (idempotent, `204`) and `POST /notifications/read-all` (`204`), all Manager-only (design 5.10). `data` is returned as a JSON object, not a string.
- **Acceptance criteria:**
  - AC-23.3: Manager A marks one read; A sees it read, Manager B sees it unread. Read-all affects only the caller.
  - AC-23.4: a Manager created after the notification sees it as unread.
  - AC-23.5: unknown id gives `404` "Notification {id} not found."; there is no delete (`405`).
  - AC-23.6: Warehouse-only and Production-only users get `403` on all four endpoints.
  - The thin slice works end to end in one test: sign in, create unit, items, recipe, receipt, order, send, complete, then the notifications appear.

### T-013: Movement history

- **Owner:** backend-developer
- **Serves:** US-11 (AC-11.1 to AC-11.4)
- **Depends on:** T-008
- **Touches:** `src/InventoryTracker.Api/Features/Movements/`, `tests/InventoryTracker.Api.Tests/Integration/MovementsTests.cs`
- **What to build:** `GET /movements` with the filters of design 5.6 (`itemId`, `location`, `type`, `orderNumber` including the order's complementary orders, `complementaryOrderNumber`, `from`, `to` as whole UTC days on `occurredAt`), and `GET /movements/{id}`. Both are paged and newest first.
- **Acceptance criteria:**
  - Every criterion of US-11. Filters combine with AND; no match gives `200` with an empty list; `from` after `to` gives `400`.
  - AC-11.3: `PUT`/`DELETE` on `/movements/{id}` give `405`.
  - AC-11.4: for an item with mixed movements, the sum of listed changes per location equals `GET /stock/{itemId}`.

### T-014: User management and own password

- **Owner:** backend-developer
- **Serves:** US-03 (AC-03.1 to AC-03.9), US-04 (AC-04.1, AC-04.2), AC-01.6
- **Depends on:** T-003
- **Touches:** `src/InventoryTracker.Api/Features/Users/` (UsersController, AccountController for `/auth/change-password`, UserService, contracts), `tests/InventoryTracker.Api.Tests/Integration/UsersTests.cs`
- **What to build:** All `/users` endpoints and `POST /auth/change-password` (design 5.2), using `UserManager`. Deactivation updates the security stamp. The last-active-Manager rule is checked inside a write transaction.
- **Acceptance criteria:**
  - Every criterion of US-03 and US-04 with the exact PRD messages. No response contains a password or hash (AC-03.4).
  - AC-03.5: after a role change, the old token keeps working with its old roles, and the next sign-in has the new roles. If Identity turns out to change the security stamp on role changes, report it (architecture 9, risk 4).
  - AC-03.6: a deactivated user's existing token gets `401`; after reactivation, they can sign in again.
  - AC-03.8: removing Manager from, or deactivating, the last active Manager gives `409` "At least one active Manager is required.".
  - AC-01.6: `POST /users` without a token gives `401`.

### T-015: Adjustments and write-offs

- **Owner:** backend-developer
- **Serves:** US-12 (AC-12.1 to AC-12.7)
- **Depends on:** T-010
- **Touches:** `src/InventoryTracker.Api/Features/Stock/AdjustmentsController.cs`, `src/InventoryTracker.Api/Features/Stock/AdjustmentService.cs`, contracts, `tests/InventoryTracker.Api.Tests/Integration/AdjustmentsTests.cs`
- **What to build:** `POST /adjustments` (design 5.6): one Adjustment movement through `StockLedger`, with the reason. Refused for a material of the InProduction order in Production storage (AC-12.5), and for a finished product in Production storage (AC-12.6).
- **Acceptance criteria:**
  - Every criterion of US-12 with the exact PRD messages, including the AC-12.3 example (−13 refused, −12 accepted, leaves 0).
  - AC-12.4: after a −20 kg write-off in Production storage, a Created order's Cheese estimate drops by 20 kg.
  - Concurrency (PRD section 5): two simultaneous −8 kg adjustments on 12 kg of stock give one `201` and one `409`; stock ends at 4.

### T-016: Cancel an order

- **Owner:** backend-developer
- **Serves:** US-21 (AC-21.1 to AC-21.6), AC-15.5 (cancel bullet), AC-23.2 (OrderCancelledWithMaterial)
- **Depends on:** T-011 (shares the controller)
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/CancelOrderService.cs`, `ProductionOrdersController.cs` (one action), `ProductionOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/CancelOrderTests.cs`
- **What to build:** `POST /production-orders/{number}/cancel` in one write transaction. No stock moves. It cancels open complementary orders with "Parent order cancelled" (AC-21.4), and creates the OrderCancelledWithMaterial notification through T-005 when any of the order's materials has Production storage stock above zero.
- **Acceptance criteria:**
  - Every criterion of US-21. AC-21.5: Completed gives `409` "Order #{n} is completed and cannot be cancelled."; Cancelled gives "This order is cancelled.".
  - AC-15.5 cancel bullet: cancelling #7 makes #8's estimate leftover 50, nominal 950, range 930 to 970.
  - AC-21.3: after cancelling an InProduction order, another order can be sent, and the cancelled order's material counts as free leftover.

### T-017: Complementary orders: request, approve, reject, view

- **Owner:** backend-developer
- **Serves:** US-16 (AC-16.1 to AC-16.6), US-17 (AC-17.1 to AC-17.4), AC-23.2 (ComplementaryAwaitingApproval)
- **Depends on:** T-005, T-010
- **Touches:** `src/InventoryTracker.Api/Features/ComplementaryOrders/` (controller with the absolute route `/api/v1/production-orders/{number}/complementary-orders` for the create action, service, contracts), `tests/InventoryTracker.Api.Tests/Integration/ComplementaryOrdersTests.cs`
- **What to build:** Create, list (filters `status`, `orderNumber`), view, approve and reject, as in design 5.8. Creating one raises the ComplementaryAwaitingApproval notification. `sendRangeMin`/`sendRangeMax` are shown while PendingApproval or Approved, using the current tolerance.
- **Acceptance criteria:**
  - Every criterion of US-16 and US-17 with the design 7.2 messages.
  - The parent order's detail (T-009) lists the complementary order with its status.
  - A Manager can raise a request and approve it themselves (PRD row 6).

### T-018: Complementary order send

- **Owner:** backend-developer
- **Serves:** US-18 (AC-18.1 to AC-18.5), D-3 (complementary sends)
- **Depends on:** T-017
- **Touches:** `src/InventoryTracker.Api/Features/ComplementaryOrders/ComplementarySendService.cs`, the complementary orders controller (one action), contracts, `tests/InventoryTracker.Api.Tests/Integration/ComplementarySendTests.cs`
- **What to build:** `POST /complementary-orders/{number}/send` in one write transaction. Check every line against its range (AC-18.1), check Warehouse stock, add SentToProduction movements linked to the complementary order and its parent, add the sent quantity to the parent line's ComplementarySends, and set Sent.
- **Acceptance criteria:**
  - Every criterion of US-18, including the AC-18.2 numbers and `409` stating the status for each non-Approved status.
  - After the send, the parent's detail shows the increased `complementarySends`, and `GET /stock` shows the increased committed amount.
  - Concurrency: two simultaneous sends of one complementary order give exactly one `200`.

### T-019: Leftover returns

- **Owner:** backend-developer
- **Serves:** US-22 (AC-22.1 to AC-22.6), AC-15.11 (end to end), AC-10.3 (pending returns), decision #18
- **Depends on:** T-010
- **Touches:** `src/InventoryTracker.Api/Features/Returns/`, `tests/InventoryTracker.Api.Tests/Integration/ReturnsTests.cs`
- **What to build:** `POST /returns`, `GET /returns` (filters `status`, `itemId`), `GET /returns/{number}`, `POST /returns/{number}/receive` (ReturnedToWarehouse movement through `StockLedger`) and `POST /returns/{number}/cancel`, as in design 5.9.
- **Acceptance criteria:**
  - Every criterion of US-22 with the design 7.2 messages, including the AC-22.4 example.
  - Decision #18: receiving a return larger than current Production storage stock gives `409` `insufficient-stock` with the design message.
  - AC-15.11 through HTTP: a Pending return blocks the send of an order that uses that material; after it is received, the send succeeds.
  - `GET /stock/{itemId}` shows the return in `pendingReturns` and reduces `freeLeftover` while it is Pending.

### T-020: End-to-end scenarios, role matrix, concurrency

- **Owner:** backend-developer
- **Serves:** AC-20.5 and AC-20.6 (worked examples A and B), AC-15.5 (whole scenario), AC-05.1, AC-05.2, AC-05.3, AC-11.4, AC-13.6, PRD section 5 consistency
- **Depends on:** T-012, T-013, T-014, T-015, T-016, T-017, T-018, T-019
- **Touches:** `tests/InventoryTracker.Api.Tests/Integration/Scenarios/` only. Product bugs found are reported in the task report and fixed in the owning feature's files under review.
- **What to build:** Scenario tests that run the PRD's worked examples through HTTP from an empty database. They are backed by a table-driven role matrix (every endpoint in design 4 × Warehouse-only, Production-only and Manager-only users), an audit check, and race tests across endpoints.
- **Acceptance criteria:**
  - Examples A and B: every line and order figure, the movements, and the notifications match the PRD (2 notifications in A, 2 in B).
  - AC-15.5 full scenario: estimates, then sending #8 first, completing it (example D), then #7's estimate.
  - AC-05.1 and AC-05.2: every endpoint returns `403` exactly for the roles PRD 4.2 excludes, and never for an allowed role.
  - AC-05.3: every movement, order, complementary order and return status change in the scenarios has a user and a UTC time in its response.
  - AC-11.4: after the scenarios, for every item and location, the sum of movements equals the stock.
  - Concurrency: a send and a negative Warehouse adjustment that together exceed stock give at most one success, and stock is never negative.

### T-021: OpenAPI completeness and sample requests

- **Owner:** backend-developer
- **Serves:** PRD section 5 (OpenAPI lists every endpoint), US-01 (documenting sign-in for clients)
- **Depends on:** T-012, T-013, T-014, T-015, T-016, T-017, T-018, T-019
- **Touches:** `src/InventoryTracker.Api/Common/OpenApi/`, `src/InventoryTracker.Api/Program.cs` (one line), `src/InventoryTracker.Api/InventoryTracker.Api.http`, `tests/InventoryTracker.Api.Tests/Integration/OpenApiTests.cs`
- **What to build:** A document transformer that declares the Bearer security scheme. Complete the `.http` file with a working walk-through of the main flow (sign in, master data, receipt, order, send, complete, notifications), using variables for the token.
- **Acceptance criteria:**
  - In Development, `/openapi/v1.json` lists all 48 method and path pairs of design section 4 and the Bearer scheme. A test compares the list.
  - Each endpoint's description comes from its `///` comment.
  - The `.http` walk-through runs top to bottom against a fresh database (manual check, described in the report).

## Coverage

Every user story appears at least once.

| User story | Tasks |
|------------|-------|
| US-01 Sign in | T-003, T-014 (AC-01.6), T-021 |
| US-02 First Manager | T-003 |
| US-03 Manage users | T-014 (T-003 provides the token check behind AC-03.6) |
| US-04 Change own password | T-014 |
| US-05 Role checks and audit | T-003 (mechanism), every endpoint task (403 tests), T-002 (audit columns), T-020 (full matrix and audit) |
| US-06 Units | T-006 |
| US-07 Items | T-006 |
| US-08 Recipes | T-007, T-009 (AC-08.4) |
| US-09 Stock receipt | T-008 |
| US-10 Current stock | T-008, T-019 (pending returns end to end) |
| US-11 Movement history | T-013, T-020 (AC-11.4) |
| US-12 Adjustments | T-015 |
| US-13 Create order | T-009, T-004 (calculations) |
| US-14 View orders | T-009 |
| US-15 Send materials | T-010, T-004, T-019 (AC-15.11 end to end), T-016 (AC-15.5 cancel bullet), T-020 |
| US-16 Request complementary order | T-017 |
| US-17 Approve or reject | T-017 |
| US-18 Complementary send | T-018, T-004 |
| US-19 Complete order | T-011, T-004 |
| US-20 Variances | T-004, T-011, T-020 (examples A and B) |
| US-21 Cancel order | T-016 |
| US-22 Returns | T-019 |
| US-23 Notifications | T-005, T-011, T-016, T-017 (creation), T-012 (API) |
