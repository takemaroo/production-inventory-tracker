# Task board: Production & Inventory Tracker API

Based on `docs/01-prd.md`, `docs/02-design.md` and `docs/03-architecture.md`. Status values: `todo`, `in progress`, `in review`, `done`, `blocked`. The delivery manager updates the Status column; the tech lead owns everything else.

There is no front end in v1, so every task is owned by `backend-developer` and reviewed by `code-reviewer`.

(v1.1) Tasks T-022 to T-032 build version 1.1 (sales orders): `docs/01-prd.md` sections 7 to 13, `docs/02-design.md` v1.1, and `docs/03-architecture.md` as updated on 2026-11-24 (every "(v1.1)" part, and decisions #154 to #164 in its section 10). There is still no front end, so every v1.1 task is owned by `backend-developer` too.

| ID | Task | Owner | Depends on | Status |
|----|------|-------|------------|--------|
| T-001 | Common foundations: errors, paging, quantities, validation, clock | backend-developer | none | done |
| T-002 | Data model, domain migration, write transaction | backend-developer | none | done |
| T-003 | Authentication, roles, first Manager, start-up checks | backend-developer | T-001, T-002 | done |
| T-004 | Production calculations (pure functions D-3 to D-9) | backend-developer | T-001 | done |
| T-005 | Notification messages and data (pure) | backend-developer | T-001, T-002 | done |
| T-006 | Units and items | backend-developer | T-003 | done |
| T-007 | Recipes | backend-developer | T-006 | done |
| T-008 | Stock ledger, receipts, current stock | backend-developer | T-004, T-006 | done |
| T-008a | Input quantity maximums: shared check, retrofit recipes and receipts | backend-developer | T-007, T-008 | done |
| T-009 | Create and view production orders | backend-developer | T-007, T-008, T-008a | done |
| T-010 | Send materials for an order | backend-developer | T-009 | done |
| T-011 | Complete an order: output, variances, notifications | backend-developer | T-005, T-010 | done |
| T-012 | Notifications API | backend-developer | T-011 | done |
| T-013 | Movement history | backend-developer | T-008 | done |
| T-014 | User management and own password | backend-developer | T-003 | done |
| T-015 | Adjustments and write-offs | backend-developer | T-010 | done |
| T-016 | Cancel an order | backend-developer | T-011 | done |
| T-017 | Complementary orders: request, approve, reject, view | backend-developer | T-005, T-010 | done |
| T-018 | Complementary order send | backend-developer | T-017 | done |
| T-019 | Leftover returns | backend-developer | T-010 | done |
| T-020 | End-to-end scenarios, role matrix, concurrency | backend-developer | T-012 to T-019 | done |
| T-021 | OpenAPI completeness and sample requests | backend-developer | T-012 to T-019 | done |
| T-022 | (v1.1) Data model, `SalesOrders` migration, sales order lines count as use | backend-developer | none | done |
| T-023 | (v1.1) Seller role and role-based reads | backend-developer | T-022 | done |
| T-024 | (v1.1) Customers | backend-developer | T-023 | done |
| T-025 | (v1.1) Create and view sales orders | backend-developer | T-024 | done |
| T-026 | (v1.1) Shipment movements: ledger rule, movement link and filter | backend-developer | T-025 | done |
| T-027 | (v1.1) Ship a sales order | backend-developer | T-026 | done |
| T-028 | (v1.1) Cancel a sales order | backend-developer | T-027 | done |
| T-029 | (v1.1) Open demand on the stock view | backend-developer | T-028 | done |
| T-030 | (v1.1) Shipped-totals reports | backend-developer | T-028 | done |
| T-031 | (v1.1) End-to-end scenarios, cross-endpoint races, performance | backend-developer | T-029, T-030 | done |
| T-032 | (v1.1) OpenAPI and sample requests | backend-developer | T-029, T-030 | done |
| T-033 | (v1.1) Log who changed a customer (SEC-26, decision #177) | backend-developer | T-024 | done |
| T-034 | Tighter name rules (SEC-25, decision #176) | backend-developer | BUG-002 fix | done |
| T-035 | (v1.2) Batch a shipment's movements (SEC-24, US-55, A-4) | backend-developer | T-027 | done |
| T-036 | (v1.2) Serve the front end: finish A-1 | backend-developer | none (skeleton) | done |
| T-037 | (v1.2) Security headers, CSP, no CORS (A-2, A-3) | backend-developer | T-036 | done |
| T-038 | (v1.2) Escape HTML characters in JSON output (A-5) | backend-developer | T-037 | done |
| T-039 | (v1.2) API client: requests, failures, paging, stock lookup | frontend-developer | none (skeleton) | done |
| T-040 | (v1.2) Formatting and safe text | frontend-developer | none (skeleton) | done |
| T-041 | (v1.2) Session: sign in, sign out, end of a session | frontend-developer | T-039 | in progress |
| T-042 | (v1.2) Design system, page frame, menu, route table | frontend-developer | T-040, T-041 | todo |
| T-043 | (v1.2) List toolkit and the Stock screen (thin slice complete) | frontend-developer | T-042 | todo |
| T-044 | (v1.2) End-to-end and accessibility harness | frontend-developer | T-036, T-037, T-043 | todo |
| T-045 | (v1.2) Form toolkit and Change password | frontend-developer | T-042 | todo |
| T-046 | (v1.2) Dialogs, idle warning and idle sign-out | frontend-developer | T-045 | todo |
| T-047 | (v1.2) Picker and Record a receipt | frontend-developer | T-045 | todo |
| T-048 | (v1.2) Units | frontend-developer | T-043, T-046, T-047 | todo |
| T-049 | (v1.2) Items | frontend-developer | T-043, T-046, T-047 | todo |
| T-050 | (v1.2) Recipes | frontend-developer | T-049 | todo |
| T-051 | (v1.2) Production orders: list, detail, cancel | frontend-developer | T-043, T-046 | todo |
| T-052 | (v1.2) New production order | frontend-developer | T-047, T-051 | todo |
| T-053 | (v1.2) Send materials | frontend-developer | T-051 | todo |
| T-054 | (v1.2) Complete an order | frontend-developer | T-051 | todo |
| T-055 | (v1.2) Request extra material | frontend-developer | T-051 | todo |
| T-056 | (v1.2) Complementary orders: list, detail, approve, reject | frontend-developer | T-043, T-046 | todo |
| T-057 | (v1.2) Send a complementary order | frontend-developer | T-056 | todo |
| T-058 | (v1.2) Leftover returns | frontend-developer | T-043, T-046, T-047 | todo |
| T-059 | (v1.2) Notifications and the unread count | frontend-developer | T-043, T-045 | todo |
| T-060 | (v1.2) Customers | frontend-developer | T-043, T-046, T-047 | todo |
| T-061 | (v1.2) Sales orders: list, detail, cancel | frontend-developer | T-043, T-046, T-047 | todo |
| T-062 | (v1.2) New sales order | frontend-developer | T-061 | todo |
| T-063 | (v1.2) Ship a sales order | frontend-developer | T-061 | todo |
| T-064 | (v1.2) Users: list and add | frontend-developer | T-043, T-045 | todo |
| T-065 | (v1.2) User detail: roles, access, password | frontend-developer | T-046, T-064 | todo |
| T-066 | (v1.2) Close-out: safe text, roles, storage, performance across every screen | frontend-developer | T-048 to T-065 | todo |

**Build phase complete (2026-11-21):** 22 of 22 tasks done, each reviewed by `code-reviewer` and committed as `T-0NN: ...` (last: `d09c6db T-021`). No task is open. Nothing was added after the decision on a full "only writer" guard for movements (decision #112: not adopted, so no post-QA task).

**Thin slice first.** T-001 to T-012 give a working path through every layer: sign in, set up master data, receive stock, create an order, send, complete, read the notifications. T-013 to T-019 add breadth. T-020 and T-021 close out. T-008a was added on 2026-11-13 for decision #80 (owner): it puts the input maximums in place before the first task that multiplies quantities (T-009).

**Running tasks at the same time.** Tasks can run in parallel when their dependencies are done and their "Touches" lists do not overlap. Safe pairs once their dependencies are met include: T-001 with T-002; T-004 with T-002 or T-003; T-005 with T-003 or T-004; T-007 with T-008; T-008a with T-013 (T-008a must not run alongside any change to `Features/Recipes/` or `Features/Stock/`); T-013 and T-014 with almost anything; T-017 with T-011; T-019 with T-011, T-015 or T-017. T-010, T-011 and T-016 share `ProductionOrdersController.cs` and are chained on purpose. T-011 adds a migration (decision #63), so it must not run alongside any other task that touches `Data/`. T-017 may run alongside T-011 (it does not touch `Data/`; SQLite does not enforce the old limit), but it must not add its own length check or migration for notification messages.

**(v1.1) Thin slice first.** T-022 to T-027 give one path through every layer: schema and migration, the Seller role and read rules, a customer, a sales order, a shipment that writes movements, and the stock and movement history that show it. T-028 to T-030 add breadth (cancel, open demand, reports). T-031 and T-032 close out, as T-020 and T-021 did.

**(v1.1) Running tasks at the same time.** T-022 adds the one v1.1 migration, so it runs alone. Every endpoint task (T-024, T-025, T-027, T-028, T-030) edits the same three test-and-sample files: `RoleMatrixTests.cs`, `OpenApiTests.cs` and `InventoryTracker.Api.http`. The suite compares each of them with the endpoints the app serves (architecture section 2, decision #164), so those tasks run one after another. Safe pairs once their dependencies are met: T-029 with T-030 (stock files against report files; T-029 touches none of the three shared files), and T-031 with T-032 (scenario tests against OpenAPI, the sample file and `.env.example`). The delivery manager's standing note to run one agent at a time still applies; these pairs are only where parallel work would be safe.

**(v1.2) The web front end.** Tasks T-036 to T-066 build it from `docs/01-prd.md` sections 14 to 20, `docs/02-design.md` sections 11 to 22 with `design/mockups/`, and `docs/03-architecture.md` part "Version 1.2" (sections 11 to 23, decisions #225 to #245). T-035 (A-4) is already done. The skeleton (phase 3) already gives: the Vite and React project in `src/InventoryTracker.Web`, the build into the API's `wwwroot`, the minimal A-1 serving, exact decimal JSON (`src/api/json.ts`), the source guard test, and the front end in `build.ps1`, `test.ps1`, `publish.ps1` and CI.

**(v1.2) Thin slice first.** T-036 to T-044 give one path through every layer: the API serves the built front end with its headers and escaping (T-036 to T-038); the client, formatting and session (T-039 to T-041); the frame and route table (T-042); then sign-in to Stock, the one screen every role has (T-043), proved in Edge through the API by the end-to-end harness (T-044). T-045 to T-047 add the shared form, dialog and picker toolkit, each with a first real screen (Change password, the idle warning, Record a receipt). T-048 to T-065 add the screens area by area. T-066 closes out, as T-020 and T-031 did.

**(v1.2) Running tasks at the same time.** T-036, T-037 and T-038 all edit `Program.cs` and run one after another; they share no file with the front-end tasks, so one backend and one front-end task can run side by side. T-039 and T-040 touch different folders and can run together. Shared front-end code (`src/components/`, `src/api/http.ts`, `src/api/paging.ts`, `src/format/`, `src/session/`, `src/app/`, `src/styles/`) is owned by T-039 to T-047; a screen task must not change it (architecture 12). From T-048 on, each screen task touches only `src/screens/<area>/`, `src/api/<area>.ts` and `e2e/<area>.spec.ts`, so screen tasks of different areas are safe together once their dependencies are done. Same-area chains are on purpose: T-049 then T-050 (`items`); T-051 then T-052 to T-055 (`production-orders`, which may then run together only if each keeps to its own screen file, see each task); T-056 then T-057; T-061 then T-062 and T-063; T-064 then T-065. Only T-044 changes `package.json`. The standing note to run one agent at a time still applies; these are only where parallel work would be safe.

## Done criteria for every task

Each task is done only when all of these hold, as well as its own criteria below:

- `dotnet build` from the repository root gives **0 errors and 0 warnings**, and `dotnet test` passes in full. The developer's report includes both commands and their results.
- **Code is commented to the owner's comment standard (CLAUDE.md, "Code comments for learning")**: a `///` summary on every class and public method or endpoint, the *why* and the PRD id beside each business rule, a short explanation the first time a framework concept appears, and a comment on each test naming the acceptance criterion it proves. `code-reviewer` checks this.
- Status codes, `code` values and messages match `docs/02-design.md` section 7 exactly where the PRD gives the wording, and keep the same facts elsewhere. Checks run in the design's order (design 2.2, architecture 5.1).
- Every request that changes data runs inside `InWriteTransactionAsync`, and every movement is written through `StockLedger` (architecture 5.1, 5.2). `StockLedger` and `StockQueries` are taken through the constructor, never created with `new` (decision #77).
- From T-008a on, every quantity the client sends is checked against the input maximums with the shared helpers in `Common/Quantities/Quantity.cs` (architecture 3.1, decision #80), never a re-typed number. The task's own criteria name its fields.
- Every acceptance criterion listed under "Serves" has at least one test, and every new endpoint has a `401` test (no token) and a `403` test (a role without access).
- No secrets in tracked files. No changes to `docs/01-prd.md`, `docs/02-design.md` or `docs/03-architecture.md`. If the task does not fit the architecture, stop and report it so the tech lead can change the document first.
- The delivery manager commits each task as `T-0NN: short description` after review.
- (v1.1) **A task that adds an endpoint** adds, in the same commit: its row or rows in `RoleMatrixTests.Endpoints` (with the `Prd42` row they perform), its OpenAPI expectations in `OpenApiTests` (for example `ConflictEndpoints` for an endpoint that can answer a business `409`, and a request-schema case for a new body), one sample request in `src/InventoryTracker.Api/InventoryTracker.Api.http`, and the raised `RoleMatrixTests.DesignEndpointCount` (architecture 7). Without them the existing tests fail.
- (v1.1) Role strings come from the `Roles` constants of architecture 4.1 item 13, one `[Authorize(Roles = ...)]` per action, never typed out and never on a controller class whose actions differ.
- (v1.1) PRD sentences are checked against `ExpectedMessages`, where they are typed out in full, never built from the code under test (decision #88).
- (v1.1) Tests use their own temporary databases. Nothing in a task opens, copies or migrates the owner's database at `%LOCALAPPDATA%\InventoryTracker\inventory.db`, or reads or changes user-secrets.
- (v1.2) **Front-end tasks** are done only when, from `src/InventoryTracker.Web`, `npm run typecheck` gives 0 errors, `npm test` passes in full (the source guard included), `npm run build` succeeds, and, from T-044 on, `npm run e2e` passes. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1` passes too, which also runs the .NET suite. The report includes the commands and their results.
- (v1.2) **The owner's comment standard applies to TypeScript too**: a `/** ... */` comment on every exported function, component and type saying what it does and why, the PRD or design id beside each rule (`// F-7: ...`, `// AC-42.1: ...`), a short explanation the first time a React, router or browser concept appears (component, hook, effect, context, `<dialog>`, ARIA role), and a comment on each test naming the criterion it proves. `code-reviewer` checks it.
- (v1.2) **Front-end rules** (architecture 15 to 18): every API string goes through `SafeText` or a form field's value, never HTML; API messages are shown word for word and never rewritten; the token stays in the session module; quantities are sent as `ExactDecimal` and shown through `src/format/`, with no arithmetic; requests go through `src/api/http.ts` with relative paths; every text the screen owns is the design's (design 18) exactly. No inline style, no new package (see "Running tasks at the same time"); any package that is added follows the 14-day rule (decision #245).
- (v1.2) **Each screen task** adds Vitest component tests for its criteria (the API answered by a stub of `src/api/http.ts`, never a real network), and one `e2e/<area>.spec.ts` that opens each of its screens through the API in Edge, checks the main path, and runs the axe check of architecture 18 at the four window sizes with no critical or serious finding.

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

### T-008a: Input quantity maximums: shared check, retrofit recipes and receipts

- **Owner:** backend-developer
- **Serves:** decision #80 (owner, Q45); design 2.4 "Maximums on input" and 7.2 (Recipes, Receipts and adjustments); architecture 3.1 "Input maximums"; AC-08.2 and US-09 (one more `400` case each)
- **Depends on:** T-007, T-008 (both committed first). Must be done before T-009.
- **Touches:** `src/InventoryTracker.Api/Common/Quantities/Quantity.cs`, `src/InventoryTracker.Api/Features/Recipes/RecipeService.cs`, `src/InventoryTracker.Api/Features/Stock/ReceiptService.cs`, `src/InventoryTracker.Api/Features/Stock/StockLedger.cs` (comment on `MaxQuantity` only), `tests/InventoryTracker.Api.Tests/Unit/Common/QuantityTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/RecipesTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/ReceiptsTests.cs`, `tests/InventoryTracker.Api.Tests/Infrastructure/ExpectedMessages.cs` (if the messages are kept there)
- **What to build:**
  1. In `Quantity.cs`, the one shared check of architecture 3.1: `MaxQuantity` = 999,999,999.999m, `MaxAmountPerUnit` = 999,999.999999m, `IsWithinMax(decimal)` (`Math.Abs(value) <= MaxQuantity`), `IsWithinMaxAmountPerUnit(decimal)`, `AboveMaxMessage(string fieldLabel)` giving "{fieldLabel} must be 999,999,999.999 or less.", and `AmountPerUnitAboveMaxMessage` = "Amount per unit must be 999,999.999999 or less." (design 7.2). Write both limits into the messages as literal text: `MessageFormat.Number` keeps 3 places and would print the amount limit as "1,000,000". The `///` comments explain the overflow reasoning of architecture 3.1 in plain words.
  2. Recipes (`PUT /items/{id}/recipe`): an amount per unit above `MaxAmountPerUnit` gets `400` `validation` with `AmountPerUnitAboveMaxMessage` under `lines[i].amountPerUnit`, as the last rule in that field's chain (required, greater than 0, 6 decimal places, maximum; one message per field, decision #75).
  3. Receipts (`POST /receipts`): replace the provisional cap of decision #79. The quantity check uses `Quantity.IsWithinMax` and `AboveMaxMessage("Quantity")` ("Quantity must be 999,999,999.999 or less."), still as the last rule for `quantity`. No service refers to `StockLedger.MaxQuantity` any more.
  4. `StockLedger.MaxQuantity` stays at the `decimal(18,3)` capacity (999,999,999,999,999.999) as the ledger's programming-error guard, because computed movements (`ConsumedInProduction`, D-9) can be larger than any single input (decision #81). Change only its comment: it is not the client limit, which is `Quantity.MaxQuantity`.
- **Acceptance criteria:**
  - Unit (`QuantityTests.cs`): `IsWithinMax(999_999_999.999m)` is true and `IsWithinMax(1_000_000_000m)` is false; `IsWithinMax(-999_999_999.999m)` is true and `IsWithinMax(-1_000_000_000m)` is false; the same pair for `IsWithinMaxAmountPerUnit` at 999,999.999999 and 1,000,000. Both messages equal the design 7.2 text exactly.
  - Unit, **max × max**: `Quantity.MaxAmountPerUnit * Quantity.MaxQuantity` does not throw `OverflowException`, and `Quantity.Round3` of it is 999,999,999,998,000.000, which is less than 999,999,999,999,999.999 (the `decimal(18,3)` maximum) and has at most 3 decimal places. The test comment names decision #80 and says this proves every planned amount (AC-13.1) and required amount (D-9) fits its column.
  - Integration, recipes: amount 999,999.999999 is saved and read back unchanged; 1,000,000 gives `400` `validation` with the message under `lines[0].amountPerUnit`; on a two-line recipe with one bad amount, only that line's path has the error.
  - Integration, receipts: quantity 999,999,999.999 is accepted (`201`); 1,000,000,000 gives `400` `validation` "Quantity must be 999,999,999.999 or less." under `quantity`, and no movement is created. The #79 message no longer appears anywhere in code or tests.
  - `dotnet build` 0 warnings, `dotnet test` all pass.

### T-009: Create and view production orders

- **Owner:** backend-developer
- **Serves:** US-13 (AC-13.1 to AC-13.7), US-14 (AC-14.1 to AC-14.3), AC-08.4, AC-15.5 (estimate bullet)
- **Depends on:** T-007, T-008, T-008a
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/` (ProductionOrdersController, ProductionOrderService, ProductionOrderContracts, mapping), `tests/InventoryTracker.Api.Tests/Integration/ProductionOrdersTests.cs`
- **What to build:** `POST /production-orders` (recipe snapshot, planned amounts, no stock moved), `GET /production-orders` (filter `status`, summary shape), `GET /production-orders/{number}` (full detail of design 5.7). The detail includes `estimate` per line while Created (D-5 and D-7, via T-004 and `StockQueries`), `history` built from the timestamp pairs, and `complementaryOrders` read from their table.
- **Acceptance criteria:**
  - Every criterion of US-13 and US-14. Every field in design 5.7 is present, with `null` for unknown values.
  - AC-08.4: change the recipe after creating an order; the order keeps its amounts, and a new order uses the new recipe.
  - AC-15.5 estimates: with 50 kg of free leftover (set up through a receipt and a ledger movement in the test) and orders #7, #8, #9, the three estimates match the PRD.
  - AC-13.4: creating an order creates no movement.
  - Decision #80: `orderedQuantity` above 999,999,999.999 gives `400` `validation` "Ordered quantity must be 999,999,999.999 or less." under `orderedQuantity`, through `Quantity.IsWithinMax` / `AboveMaxMessage` (T-008a). Test: an order of exactly 999,999,999.999 for a recipe line of 999,999.999999 is created, and its planned amount is 999,999,999,998,000 (no overflow, fits `decimal(18,3)`).
- **Notes:**
  - **Decision #58, percentages.** The detail and summary mappings that T-009 writes are reused for Completed orders, so every percentage they return (`outputVariancePercent`, each line's `usageVariancePercent`, `toleranceAtCompletionPercent` where shown) is round(stored 6-place value, 2), half away from zero, never a fresh calculation from the quantities. A stored `null` (decision #49) is returned as `null`. Call the shared helper `Quantity.DisplayPercent` in `Common/Quantities` (decisions #93, #94: round to 6 places, then to 2, both half away from zero) from the mapping code, so T-011's responses reuse it; never re-implement the rule. Test it with a stored value such as 2.004999 → 2.00 and 2.005 → 2.01 (on a Completed order inserted through the DbContext).

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
  - Decision #80: a line's `quantity` above 999,999,999.999 gives `400` `validation` "Quantity must be 999,999,999.999 or less." under `lines[i].quantity` (shared check, T-008a), as a field error before the range check.

### T-011: Complete an order: output, variances, notifications

- **Owner:** backend-developer
- **Serves:** US-19 (AC-19.1 to AC-19.8), US-20 (AC-20.1 to AC-20.10), AC-23.2 (first three types created)
- **Depends on:** T-005, T-010
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/CompleteOrderService.cs`, `ProductionOrdersController.cs` (one action), `ProductionOrderContracts.cs`, `src/InventoryTracker.Api/Data/Configurations/NotificationConfiguration.cs`, `src/InventoryTracker.Api/Data/Configurations/ColumnRules.cs`, `src/InventoryTracker.Api/Data/Configurations/ProductionOrderConfiguration.cs`, `src/InventoryTracker.Api/Data/Configurations/ProductionOrderLineConfiguration.cs`, `src/InventoryTracker.Api/Data/Migrations/` (new migration and the model snapshot), `tests/InventoryTracker.Api.Tests/Integration/CompleteOrderTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/DataModelTests.cs`. Because it touches `Data/`, T-011 must not run at the same time as any other task that touches `Data/`.
- **Step 1, schema change (decision #63, architecture 3.2 Notification):** remove the 1,000-character limit on `Notification.Message`. Delete `HasMaxLength(ColumnRules.MessageLength)` in `NotificationConfiguration` and the `ColumnRules.MessageLength` constant, then generate the migration named **`NotificationMessageUnbounded`** with `dotnet tool run dotnet-ef migrations add NotificationMessageUnbounded --project src/InventoryTracker.Api --output-dir Data/Migrations`. Do not edit the committed `DomainModel` migration. No code anywhere truncates or length-checks a notification message. Do this before saving any notification; T-016 and T-017 also save notifications and rely on it.
- **Step 1b, percentage precision (decision #82, architecture 3.1):** add `ColumnRules.IsPercentage()` with `HasPrecision(28, 6)` and use it for `ProductionOrder.OutputVariancePercent`, `ProductionOrder.ToleranceAtCompletionPercent` and `ProductionOrderLine.UsageVariancePercent` (they use `IsFineDecimal()` today, which stays for recipe and order-line `AmountPerUnit`). Then generate a second migration named **`PercentagePrecision`** the same way. On SQLite every decimal is `TEXT`, so its `Up`/`Down` may contain no column change; keep it anyway, because the model snapshot is what records the precision for a later PostgreSQL port. With capped inputs a variance percentage can reach about 1e14, which `(18, 6)` cannot hold (architecture 3.1).
- **What to build:** `POST /production-orders/{number}/complete` in one write transaction. It creates the ProducedOutput movement and ConsumedInProduction movements (none for zero), fills every completion field on the order and its lines through T-004, stores the tolerance in force, and creates the OutputVariance, UsageVariance and LeftoverAtCompletion notifications through T-005. It also cancels the order's PendingApproval and Approved complementary orders with "Parent order completed" (AC-19.6).
- **Acceptance criteria:**
  - Every criterion of US-19 and US-20, with the exact PRD messages for AC-19.4.
  - Worked examples C (AC-20.7) and D (AC-20.8) through HTTP: every line figure, the movements, Production storage stock afterwards, and the notifications created. Examples A and B need complementary orders and are covered in T-020.
  - AC-19.6: complementary orders inserted through the DbContext in PendingApproval and Approved become Cancelled with the note; Rejected and Sent ones are unchanged.
  - AC-20.10: changing the tolerance setting after completion does not change the stored values.
  - Concurrency: two simultaneous completions give exactly one `200`.
  - Decision #80: `producedQuantity` above 999,999,999.999 gives `400` "Produced quantity must be 999,999,999.999 or less." under `producedQuantity`, and a line's `leftover` above it gives "Leftover must be 999,999,999.999 or less." under `lines[i].leftover` (shared check, T-008a).
  - Decision #82: an order of 0.001 completed with a produced quantity of 999,999,999.999 stores and returns its output variance percentage without error, and the model snapshot shows `(28, 6)` for the three percentage columns.
  - Decision #63: the app starts on a database created before this task (with `DomainModel` only) and migrates it; a notification whose message is longer than 1,000 characters (for example an order with long item names, units and many lines) is saved and read back unchanged, character for character. Test in `DataModelTests.cs` or `CompleteOrderTests.cs`.
- **Notes:**
  - **Decision #58, percentages.** `OutputVariancePercent` and `UsageVariancePercent` are stored as the unrounded value rounded to 6 places, half away from zero. Round in code before saving: SQLite does not apply `HasPrecision` on write. Every percentage in the completion response is round(stored 6-place value, 2), half away from zero, which is the same value the notifications show. A test compares a response percentage with the matching notification's `data`.
  - **Decision #49, required amount rounds to 0.** When a line's `RequiredForActualOutput` rounds to 0 (3 places), the completion still succeeds: store `UsageVariancePercent` as `null` (never divide by zero), set `UsageVarianceAboveTolerance` to true when `ActualUsage` is not 0 (false when it is 0), and create the UsageVariance notification in the "percentage not applicable" form of decision #57, with `data.percent` null. The response shows `usageVariancePercent: null` for that line. Test both cases (ActualUsage 0 and not 0).
  - Decision #48: a negative `LeftoverRemaining` is stored as calculated, not clamped; include one HTTP test of it.

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
  - **UserName must stay equal to Email** (architecture 3.2 AppUser, decision #64). `POST /users` trims the email, refuses one that still contains a space with `400` `validation` "Email is not a valid email address." (AC-03.3), and sets `UserName` to exactly the same trimmed value as `Email`. No code path changes one without the other. Identity's email index is not unique; this rule is what makes the unique `UserNameIndex` guarantee AC-03.2. Tests: creating "Ana@Example.com" and then " ana@example.com " gives `409` `email-taken`; "ana smith@example.com" gives the `400`; a created user's `UserName` equals its `Email` (checked through the DbContext).

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
  - Decision #80: the limit applies to the size of the signed `quantity`, through `Quantity.IsWithinMax` (T-008a). 999,999,999.999 and -999,999,999.999 pass the input check; 1,000,000,000 gives `400` "Quantity must be 999,999,999.999 or less." and -1,000,000,000 gives `400` "Quantity must be between -999,999,999.999 and 999,999,999.999." (design 7.2), both under `quantity`.

### T-016: Cancel an order

- **Owner:** backend-developer
- **Serves:** US-21 (AC-21.1 to AC-21.6), AC-15.5 (cancel bullet), AC-23.2 (OrderCancelledWithMaterial)
- **Depends on:** T-011 (shares the controller)
- **Touches:** `src/InventoryTracker.Api/Features/ProductionOrders/CancelOrderService.cs`, `ProductionOrdersController.cs` (one action), `ProductionOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/CancelOrderTests.cs`
- **What to build:** `POST /production-orders/{number}/cancel` in one write transaction. No stock moves. It cancels open complementary orders with "Parent order cancelled" (AC-21.4), and creates the OrderCancelledWithMaterial notification through T-005 when an InProduction order is cancelled with production-storage stock above zero (AC-21.3, design 3.8): at least one of the order's materials has stock above zero in Production storage, and each such material is listed with its whole production-storage quantity. Cancelling a Created order raises no notification (decision #104a, b).
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
  - Decision #80: a line's `requestedAmount` above 999,999,999.999 gives `400` `validation` "Requested amount must be 999,999,999.999 or less." under `lines[i].requestedAmount` (shared check, T-008a).

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
  - Decision #80: a line's `quantity` above 999,999,999.999 gives `400` `validation` "Quantity must be 999,999,999.999 or less." under `lines[i].quantity` (shared check, T-008a), as a field error before the range check.

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
  - Decision #80: `quantity` above 999,999,999.999 on `POST /returns` gives `400` `validation` "Quantity must be 999,999,999.999 or less." under `quantity` (shared check, T-008a).

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
- **As built (decision #109):** besides `Integration/Scenarios/`, T-020 moved the two 100,000-movement performance tests into nested `Performance` classes in `StockTests.cs` and `MovementsTests.cs` (collection `PerformanceCollection`, new in `tests/.../Infrastructure`), extended `Infrastructure/OrderFlow.cs` (`LedgerAsync` with several movements, `InsertReturnAsync`), and made small tidy-ups in `ForcedOrder.cs`, `AdjustmentsTests.cs` and `ItemsTests.cs`. Tests only; no product code changed, no product bugs found. The role matrix also covers Warehouse plus Production (AC-05.2) and compares with `EndpointDataSource`; a complementary send against a negative adjustment was added alongside the order send.

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
- **As built (decision #110, accepted by the delivery manager outside the Touches list):** every controller action gained `[ProducesResponseType]` attributes; request records whose optional fields lacked a default got `= null` (`ItemRequest`'s `Description` moved last for this); `PageQuery.Skip` became `[BindNever]`; `Program.cs` changed in a few lines (the OpenAPI set-up and the minimal-API JSON options), not one; `.env.example` is unchanged, and the `.http` file reads `src/InventoryTracker.Api/.env` with `$dotenv`.

### T-022: (v1.1) Data model, `SalesOrders` migration, sales order lines count as use

- **Owner:** backend-developer
- **Serves:** D-10 (own number sequence), AC-24.1 and AC-26.1 (stored fields), AC-28.1 (movement link column), AC-28.5 (sales order lines count as use), AC-29.5 (ids never reused, `Restrict` backstop), AC-26.10 and AC-29.8 (database backstops), AC-30.3 ("no other data changes" on upgrade), PRD section 5 Data; architecture 3.1, 3.2, 3.4 (v1.1); decisions #154, #157, #162
- **Depends on:** none. **Runs alone**: no other task may run while T-022 changes `Data/`.
- **Touches:** `src/InventoryTracker.Api/Domain/Customer.cs`, `SalesOrder.cs`, `SalesOrderLine.cs`, `SalesOrderStatus.cs` (new), `src/InventoryTracker.Api/Domain/Movement.cs` (`SalesOrderNumber` and its navigation), `src/InventoryTracker.Api/Data/AppDbContext.cs` (three `DbSet`s), `src/InventoryTracker.Api/Data/Configurations/CustomerConfiguration.cs`, `SalesOrderConfiguration.cs`, `SalesOrderLineConfiguration.cs` (new), `MovementConfiguration.cs`, `ColumnRules.cs` (`CustomerNameLength`, `CustomerReferenceLength`), `src/InventoryTracker.Api/Data/Migrations/` (the new migration, its Designer file and the model snapshot), `src/InventoryTracker.Api/Features/Items/ItemService.cs` (in-use check and delete message), `tests/InventoryTracker.Api.Tests/Integration/DataModelTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/ItemsTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/UpgradeTests.cs` (new)
- **What to build:** The three new entities, `SalesOrderStatus`, and `Movement.SalesOrderNumber`, exactly as architecture 3.2 (v1.1) lists them: fields, lengths, `Restrict` foreign keys, the indexes, and `IX_Movements_OneShipmentPerLine` (unique, filter `"SalesOrderNumber" IS NOT NULL`). One configuration class per entity. Then the migration `SalesOrders`, following architecture 3.4 steps 1 to 3 to the letter, including the hand edit of `Up` and its comment. `MovementType` does **not** change here (T-026 adds `ShippedToCustomer`). Last, `ItemService.IsInUseAsync` gains `SalesOrderLines.AnyAsync(line => line.ItemId == itemId)`, and the delete message becomes "{item} has movements, recipe lines, order lines or sales order lines and cannot be deleted." (AC-28.5, design 7.2). An existing test reads every foreign key to `Items` from the model, so this cannot wait for T-025.
- **Acceptance criteria:**
  - The report includes the output of `dotnet tool run dotnet-ef migrations script PercentagePrecision --project src/InventoryTracker.Api`: one `BEGIN TRANSACTION` and one `COMMIT`, the line `ALTER TABLE "Movements" ADD "SalesOrderNumber" INTEGER NULL CONSTRAINT "FK_Movements_SalesOrders_SalesOrderNumber" REFERENCES "SalesOrders" ("Number") ON DELETE RESTRICT;`, the history insert inside the transaction, and no `ef_temp_Movements` and no `PRAGMA`.
  - `DataModelTests`: five migrations applied, the last `_SalesOrders`, none pending; `Customers`, `SalesOrders` and `SalesOrderLines` exist; 15 domain entities, each with its own configuration class; 7 enum properties stored as names (30 characters); `SalesOrderStatus` names are exactly Created, Shipped, Cancelled (design 2.3); `db.Database.HasPendingModelChanges()` is false.
  - `DataModelTests`, database rules (rows inserted through the DbContext, each expecting `DbUpdateException` where refused):
    - two customers whose names differ only in case cannot both be saved;
    - a second line for the same item on one sales order is refused;
    - deleting a customer that a sales order names is refused (`Restrict`);
    - a movement naming a sales order number that does not exist is refused (foreign key);
    - two movements with the same `SalesOrderNumber` and `ItemId` are refused, while two with a null `SalesOrderNumber` are fine (filtered index);
    - the first sales order is number 1 even when production orders exist (own sequence, D-10);
    - after a customer is deleted, the next customer does not get its id (AUTOINCREMENT, AC-29.5).
  - `UpgradeTests` (architecture 7, v1.1): builds a v1 database on a temporary file with `IMigrator.MigrateAsync("20261114053033_PercentagePrecision")` and inserts v1 rows with SQL (the three roles, users with roles, a unit, items, a few hundred movements, a Completed production order with a line, a Pending return, a notification). It then starts `TestApiFactory` with `DatabasePath` set to that file, and asserts:
    - `SalesOrders` is applied and nothing is pending;
    - every v1 table's row count, and sampled values (one movement's every column, a user's roles), are unchanged;
    - `sqlite_sequence` for `Movements` is unchanged;
    - `PRAGMA foreign_key_check` returns nothing, and `PRAGMA foreign_key_list(Movements)` includes `SalesOrders`;
    - all eight `Movements` indexes exist;
    - the SQL that `IMigrator.GenerateScript("20261114053033_PercentagePrecision")` produces contains no `ef_temp_` (architecture 9, risk 15);
    - the app still answers `GET /api/v1/stock` with the v1 figures.
  - AC-28.5: the item in-use theory in `ItemsTests` gains a sales order line (inserted through the DbContext, in a Cancelled sales order, to show "whatever the status"). Changing that item's type or unit gives `409` `item-in-use` "Type and unit cannot change once the item is in use.", and deleting it gives `409` `item-in-use` with the new message. `Item_in_use_check_covers_every_foreign_key_to_items` passes. Existing tests that pin the old delete message are updated to the new one.
  - Every new class and public member has its `///` comment. The migration's hand edit carries the comment of architecture 3.4, and the entity comments explain the "why" (own sequence, never reused, insert-only links).
  - `dotnet build` 0 warnings, `dotnet test` all pass.

### T-023: (v1.1) Seller role and role-based reads

- **Owner:** backend-developer
- **Serves:** US-30 (AC-30.1, AC-30.2, AC-30.3, AC-30.5; AC-30.4, AC-30.6, AC-30.7 and AC-30.8 for the 48 v1 endpoints), AC-28.7 (a Seller gets `403` on the movement list), AC-05.1 and AC-05.2 (v1.1 table), decision #147; architecture 4.1 items 13 and 14, decisions #155, #156, #164
- **Depends on:** T-022 (its `UpgradeTests` is extended here)
- **Touches:** `src/InventoryTracker.Api/Auth/Roles.cs`, `src/InventoryTracker.Api/Auth/StartupChecks.cs` (comments that say "three roles" only), `src/InventoryTracker.Api/Features/Users/UserService.cs` (unknown-role message), one attribute per read action in `src/InventoryTracker.Api/Features/Recipes/RecipesController.cs`, `Features/Movements/MovementsController.cs`, `Features/ProductionOrders/ProductionOrdersController.cs`, `Features/ComplementaryOrders/ComplementaryOrdersController.cs`, `Features/Returns/ReturnsController.cs` (and the class comments there that say "any signed-in user"), `tests/InventoryTracker.Api.Tests/Integration/Scenarios/RoleMatrixTests.cs`, `tests/InventoryTracker.Api.Tests/Integration/OpenApiTests.cs` (the count constant only), `tests/InventoryTracker.Api.Tests/Infrastructure/ExpectedMessages.cs`, `tests/InventoryTracker.Api.Tests/Integration/UsersTests.cs`, `RequestRolesLimitTests.cs`, `AuthTests.cs`, `UpgradeTests.cs`, `tests/InventoryTracker.Api.Tests/Unit/Auth/ForbiddenResponseMessageTests.cs` (new)
- **What to build:** `Roles.Seller` and `Roles.All` = Warehouse, Production, Manager, Seller. The three new constants of architecture 4.1 item 13, with the exact values given there. `[Authorize(Roles = Roles.WarehouseProductionOrManager)]` on `GET /items/{id}/recipe`, `GET /movements`, `GET /movements/{id}`, `GET /production-orders`, `GET /production-orders/{number}`, `GET /complementary-orders`, `GET /complementary-orders/{number}`, `GET /returns` and `GET /returns/{number}`, each on its action. The unknown-role message lists four roles. In the role matrix, `Prd42` becomes the v1.1 PRD 4.2 table and the callers become the six of architecture 7 (v1.1). The literal 48 becomes `RoleMatrixTests.DesignEndpointCount` (48 here).
- **Acceptance criteria:**
  - AC-30.1:
    - `POST /users` with roles `["seller"]` gives `201` with `"roles": ["Seller"]`, and with `["Seller", "Warehouse"]` gives `["Warehouse", "Seller"]`;
    - `PUT /users/{id}/roles` to `["SELLER"]` works;
    - an unknown role gives `400` "Unknown role 'Driver'. Roles are Warehouse, Production, Manager and Seller." under `roles[i]` (`UsersTests` and `RequestRolesLimitTests` updated);
    - a Seller-only user gets `403` "Your roles do not allow this action. It needs the Manager role." on every `/users` endpoint (matrix).
  - AC-30.2: signing in as a user with all four roles returns `"roles": ["Warehouse", "Production", "Manager", "Seller"]`, in that order.
  - AC-30.3 (`UpgradeTests`): after the first start on the v1 database, the role `Seller` exists, every user has exactly the roles they had, and no user holds Seller. A test also restarts on the same file and proves that seeding a second time changes nothing. An empty database still gives one Manager-only user (the existing `StartupTests`).
  - AC-30.5: the last active Manager, who also holds Seller, cannot have Manager removed (`409` `last-manager`). A Seller-only user is not counted as a Manager.
  - Role matrix (48 endpoints × 6 callers, plus the 48 no-token cases):
    - every allowed caller gets the design 4 success status, and every refused caller gets the exact 7.1 message, including "Your roles do not allow this action. It needs the Warehouse, Production or Manager role.";
    - a Seller gets `200` on units, items, stock (list and one) and `204` on change-password (AC-30.7);
    - a Seller gets `403` on the recipe read, movements, production orders, complementary orders, returns, users and notifications (AC-30.6);
    - Production plus Seller gets `200` on movements and production orders (AC-30.8, the v1 half);
    - the table test confirms each endpoint's declared roles match the v1.1 PRD 4.2 row.
  - AC-30.7, committed order: with an InProduction order, a Seller's `GET /stock/{itemId}` shows `committedOrderNumber`, and the Seller's `GET /production-orders/{number}` for it is `403`.
  - Design 2.2: a Seller-only user's `GET /production-orders/99999` (unknown) is `403`, not `404`. A Seller-only user's `GET /movements?type=Nope` is `403`, not `400`.
  - Unit: `ForbiddenResponseHandler.Message(["Warehouse", "Production", "Manager"])` and `(["Warehouse", "Manager", "Seller"])` and `(["Manager", "Seller"])` give the three design 7.1 sentences, typed out.
  - No read that a v1 role could do before is refused now: Warehouse, Production and Manager rows are unchanged in the matrix.

### T-024: (v1.1) Customers

- **Owner:** backend-developer
- **Serves:** US-29 (AC-29.1 to AC-29.7; the sales order parts of AC-29.3 and AC-29.4 are proved in T-025 and T-027, and AC-29.8 in T-025), AC-05.3 (created by whom and when); design 3.12, 5.11, 7.2 Customers; architecture 3.2 (Customer), 4.1 items 15 and 19, 5.1 (check order), decision #161
- **Depends on:** T-023
- **Touches:** `src/InventoryTracker.Api/Features/Customers/` (new: `CustomersController.cs`, `CustomerService.cs`, `CustomerContracts.cs`), `src/InventoryTracker.Api/Common/Validation/BoolParser.cs` (new), `src/InventoryTracker.Api/Features/Notifications/NotificationService.cs` (uses `BoolParser`; same behaviour and message), `tests/InventoryTracker.Api.Tests/Integration/CustomersTests.cs` (new), `tests/InventoryTracker.Api.Tests/Infrastructure/SalesFlow.cs` (new), `Infrastructure/ExpectedMessages.cs`, `Integration/Scenarios/RoleMatrixTests.cs` (seven rows; `DesignEndpointCount` 55), `Integration/OpenApiTests.cs`, `tests/InventoryTracker.Api.Tests/Unit/Common/BoolParserTests.cs` (new), `src/InventoryTracker.Api/InventoryTracker.Api.http` (seven samples in a new "v1.1" section)
- **What to build:** The seven `/customers` endpoints of design 4 and 5.11. Reads use `Roles.WarehouseManagerOrSeller`; create, edit, deactivate, reactivate and delete use `Roles.ManagerOrSeller`. Follow the check order of architecture 5.1 (v1.1). Names follow the unit and item pattern (`NameNormalizer`, the duplicate check inside the write transaction, the unique index as backstop). Move the `unreadOnly` parsing into `BoolParser`, and use it for `isActive`. `SalesFlow` starts with `CreateCustomerAsync` and `InsertSalesOrderAsync`: the latter inserts a sales order with one line, in a given status, through the DbContext inside a write transaction, for the delete test.
- **Acceptance criteria:**
  - AC-29.1: `201` with `Location: /api/v1/customers/{id}` and the design 5.11 body, active, with `createdAt` and `createdBy`. `notes` is optional, trimmed, and blank becomes `null`.
  - AC-29.2:
    - a name another customer has, ignoring case, gives `409` `customer-name-taken` "A customer named 'Deli Norte' already exists." (the name as sent, trimmed);
    - a missing or blank name gives "Name is required.";
    - a name of 101 characters gives "Name must be 100 characters or fewer.", and notes of 501 give "Notes must be 500 characters or fewer.", each under its field;
    - lengths count code points (an emoji counts as one, decision #116).
  - AC-29.3: `PUT` changes name and notes and returns `200`. Its own name in another letter case is allowed. Leaving `notes` out clears them. An unknown id gives `404` "Customer {id} not found.", and the `404` comes before the body checks.
  - AC-29.4: deactivate gives `isActive: false`, and a second deactivate gives `200` with nothing changed. Reactivate works the same way. A deactivated customer is still listed and readable.
  - AC-29.5:
    - deleting an unused customer gives `204`; afterwards `GET` is `404`, it is not in the list, and its name can be used for a new customer, which gets a new id;
    - a customer with one Cancelled sales order (`SalesFlow.InsertSalesOrderAsync`) gives `409` `customer-has-sales-orders` "Deli Norte has sales orders and cannot be deleted. Deactivate it instead.";
    - an unknown id gives `404`.
  - AC-29.6: the list is newest first (`FakeTime`) and paged. `isActive=true` and `false` work in any letter case. `isActive=yes` gives `400` "isActive must be true or false." under `isActive`, together with any paging errors in one response.
  - AC-29.7:
    - a Production-only user gets `403` "Your roles do not allow this action. It needs the Warehouse, Manager or Seller role." on `GET /customers`;
    - a Warehouse-only user gets "... It needs the Manager or Seller role." on `POST /customers`;
    - every endpoint has a `401` test, and the matrix covers every caller.
  - Concurrency: two simultaneous `POST /customers` with "Deli Norte" and "DELI NORTE" give exactly one `201` and one `409` `customer-name-taken`.
  - `BoolParser` unit tests (true, FALSE, empty, spaces, " true", "1"), and every existing `NotificationsTests` case passes unchanged.
  - No log line contains customer notes (decision #163).

### T-025: (v1.1) Create and view sales orders

- **Owner:** backend-developer
- **Serves:** US-24 (AC-24.1 to AC-24.9), US-25 (AC-25.1 to AC-25.4), AC-29.3 (current name on sales orders), AC-29.4 (a deactivated customer: `409` on create, shown inactive), AC-29.8 (delete racing a new sales order), AC-05.3; design 3.13, 5.12, 7.2 Sales orders; architecture 3.5 (list, detail), 4.1 items 12, 15 to 18, 5.1 (check order), 5.2 (v1.1 rows)
- **Depends on:** T-024
- **Touches:** `src/InventoryTracker.Api/Features/SalesOrders/` (new: `SalesOrdersController.cs`, `SalesOrderService.cs`, `SalesOrderContracts.cs`, `SalesOrderMapping.cs`), `src/InventoryTracker.Api/Common/Contracts/CustomerSummary.cs` (new), `tests/InventoryTracker.Api.Tests/Integration/SalesOrdersTests.cs` (new), `Integration/RequestLinesLimitTests.cs` (one more endpoint), `Infrastructure/SalesFlow.cs`, `Infrastructure/ExpectedMessages.cs`, `Integration/Scenarios/RoleMatrixTests.cs` (three rows; `DesignEndpointCount` 58), `Integration/OpenApiTests.cs`, `src/InventoryTracker.Api/InventoryTracker.Api.http`
- **What to build:** `POST /sales-orders` (`Roles.ManagerOrSeller`), `GET /sales-orders` and `GET /sales-orders/{number}` (`Roles.WarehouseManagerOrSeller`), in the shapes of design 5.12. Creation follows the check order of architecture 5.1 (v1.1): the line cap first, then the line checks of 4.1 item 16, all in one write transaction. `SalesOrderService.ReadDetailAsync` is the one builder of the detail (architecture 3.5). Log "Sales order {number} created by user {userId}." after the commit. `SalesFlow` gains `CreateSalesOrderAsync` and `ReadSalesOrderAsync`.
- **Acceptance criteria:**
  - AC-24.1:
    - `201` with `Location: /api/v1/sales-orders/1` and the design 5.12 detail;
    - the number is 1 even after production orders were created (D-10), and the status is Created;
    - `createdAt` and `createdBy` are set, the lines keep the entered order with `itemType` and `unit`, and `history` has one Created entry;
    - `customerReference` is trimmed, and blank becomes `null`.
  - AC-24.2: an item with 0 Warehouse stock and a sales order for 600 Bag gives `201`. No movement is created, and `GET /stock/{itemId}` still shows 0.
  - AC-24.3, each case with its design 7.2 message and JSON path, and all problems in one response:
    - customer missing, or an unknown id;
    - a reference of 101 characters;
    - `lines` missing, null or empty;
    - a line sent as `null` gets both "Item is required." and "Quantity is required.";
    - an unknown item;
    - the same item twice, flagged on every later line;
    - quantity missing, 0, negative, 4 decimal places, and 1,000,000,000 (through `Quantity.IsWithinMax`).
  - AC-24.4: a deactivated customer gives `409` `customer-inactive` "Customer Deli Norte is deactivated. Reactivate the customer or choose another one.". A body with a bad line **and** a deactivated customer gives the `400` only (field checks first).
  - AC-24.5:
    - 500 lines are accepted;
    - 501 lines give `400` `too-many-lines` "At most 500 lines are allowed." on its own, even with the customer missing;
    - in `RequestLinesLimitTests`, the 501-line request is answered while `HeldWriteLock` holds the write lock, and a 500-line request waits for it.
  - AC-24.6: several Created sales orders for the same customer and items, and one created while a production order is InProduction, are all `201`.
  - AC-24.7: `PUT` and `DELETE /sales-orders/1` give `405` `method-not-allowed`.
  - AC-24.8: Pizzeria Lago with 120 kg of Cheese (a raw material) gives `201`, and so does a sales order with Cheese and a finished product together.
  - AC-24.9, AC-25.4, design 2.2:
    - Warehouse plus Production gets `403` "... It needs the Manager or Seller role." on create;
    - Production-only gets "... It needs the Warehouse, Manager or Seller role." on the list and the detail;
    - Production-only asking for a number that does not exist gets `403`, not `404`;
    - each endpoint has a `401` test, and the matrix covers every caller.
  - AC-25.1:
    - the list is newest first by `createdAt`, then number;
    - each row has the summary shape (`lineCount`, `statusChangedAt`, `customer`);
    - the `status`, `itemId` and `customerId` filters combine, and no match gives an empty `200`;
    - `status=Done` gives `400` "status must be Created, Shipped or Cancelled.";
    - paging works.
  - AC-25.2, AC-25.3: the detail has every field, with `null` where not yet known. An unknown number gives `404` "Sales order #{number} not found.".
  - AC-29.3: after a customer is renamed, an older sales order's detail and list row show the new name.
  - AC-29.4: after a deactivation, the sales order shows `customer.isActive: false`.
  - AC-29.8: a customer delete and a sales order create for that customer at the same time, with both orderings forced (`ForcedOrder` on `Customers`, and on `SalesOrders`). Exactly one succeeds: either the delete (`204`) and the create's `400` "Customer {id} does not exist.", or the create (`201`) and the delete's `409` `customer-has-sales-orders`. No sales order names a missing customer.

### T-026: (v1.1) Shipment movements: ledger rule, movement link and filter

- **Owner:** backend-developer
- **Serves:** D-2 (the ShippedToCustomer row), AC-28.1, AC-28.2, AC-28.3, AC-28.6, AC-28.7 (Warehouse, Production and Manager see and filter shipments); design 2.3, 5.6, 7.2 Lists; architecture 3.2 (Movement), 4.1 item 20, 5.1 (v1.1 contract), decisions #157, #161
- **Depends on:** T-025 (tests link movements to real sales orders made through the API)
- **Touches:** `src/InventoryTracker.Api/Domain/MovementType.cs`, `src/InventoryTracker.Api/Features/Stock/StockLedger.cs`, `src/InventoryTracker.Api/Features/Stock/MovementContracts.cs` (`MovementResponse`, `MovementMapping`), `src/InventoryTracker.Api/Features/Movements/MovementFilter.cs`, `MovementService.cs`, `src/InventoryTracker.Api/Common/Validation/DayRange.cs` (new), `tests/InventoryTracker.Api.Tests/Integration/StockTests.cs` (ledger rules), `Integration/MovementsTests.cs`, `Integration/DataModelTests.cs` (the `MovementType` names), `tests/InventoryTracker.Api.Tests/Unit/Common/DayRangeTests.cs` (new)
- **What to build:** `MovementType.ShippedToCustomer`, after `Adjustment`. The ledger's shape and link rules for it (architecture 5.1, v1.1). `salesOrderNumber` on every movement response, placed between `returnNumber` and `receiptDate`. The `salesOrderNumber` filter on `GET /movements`. `MovementService`'s private start-of-day code moves into `DayRange`, with no change in behaviour. No endpoint writes a shipment yet; tests add shipment movements with `OrderFlow.LedgerAsync`.
- **Acceptance criteria:**
  - The ledger accepts a `ShippedToCustomer` movement with a negative `WarehouseChange`, a null `ProductionStorageChange` and the number of an existing sales order, and Warehouse stock goes down by that amount.
  - The ledger refuses with `ArgumentException` (and saves nothing) a `ShippedToCustomer` movement:
    - with a production-storage change, a positive or null Warehouse change, or no `SalesOrderNumber`;
    - with a `ProductionOrderNumber`, `ComplementaryOrderNumber`, `ReturnNumber`, `ReceiptDate`, `Note` or `Reason`.
  - The ledger also refuses a `SalesOrderNumber` on Received, Adjustment and SentToProduction.
  - A shipment that would take the Warehouse below zero gives `409` `insufficient-stock` (the ledger's message). One naming an unknown sales order, or repeating a (sales order, item) pair, is refused by the database (`DbUpdateException`), and nothing is saved.
  - Design 5.6: every movement in every response has `salesOrderNumber` (`null` except on shipments). The design's ShippedToCustomer example round-trips field for field.
  - AC-28.2:
    - `?salesOrderNumber=` works alone and combined with `itemId`, `type=shippedtocustomer` (any case), `from` and `to` (the AC's December example);
    - `?orderNumber=` never returns a shipment;
    - an unknown sales order number gives an empty `200`, and `?salesOrderNumber=abc` gives `400` with the framework wording (decision #113).
  - `?type=Nope` gives "type must be Received, SentToProduction, ConsumedInProduction, ProducedOutput, ReturnedToWarehouse, Adjustment or ShippedToCustomer." (`MovementsTests` updated), and `DataModelTests` lists seven `MovementType` names in that order.
  - AC-28.3: for an item with receipts, an adjustment and two shipments, the listed changes per location add up to `GET /stock/{itemId}`.
  - AC-28.6: `PUT` and `DELETE /movements/{id}` on a shipment movement give `405`.
  - AC-28.7: Warehouse-only and Production-only users list shipment movements with their `salesOrderNumber` and can filter by it. The Seller-only `403` stays (matrix).
  - `DayRange` unit tests, including 9999-12-31 (no upper bound). Every existing movement date-filter test passes unchanged.

### T-027: (v1.1) Ship a sales order

- **Owner:** backend-developer
- **Serves:** US-26 (AC-26.1 to AC-26.9, AC-26.10 first and second bullets, AC-26.11, AC-26.12, AC-26.13), AC-28.1 (the movements a ship writes), AC-27.3 (a Cancelled sales order never ships), AC-29.4 (a deactivated customer's sales order still ships), AC-05.3; design 3.13, 3.14, 5.12, 7.2 Sales orders; architecture 5.1 (check order), 5.2 (v1.1 rows), 5.3, decision #158
- **Depends on:** T-026
- **Touches:** `src/InventoryTracker.Api/Features/SalesOrders/ShipSalesOrderService.cs` (new), `SalesOrdersController.cs` (one action), `SalesOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/ShipSalesOrderTests.cs` (new), `Infrastructure/SalesFlow.cs` (`ShipAsync`), `Infrastructure/ExpectedMessages.cs`, `Integration/Scenarios/RoleMatrixTests.cs` (one row; `DesignEndpointCount` 59), `Integration/OpenApiTests.cs`, `src/InventoryTracker.Api/InventoryTracker.Api.http`
- **What to build:** `POST /sales-orders/{number}/ship` (`Roles.WarehouseOrManager`), exactly as architecture 5.3: one write transaction; `404`, then the note, then the status, then every line checked against Warehouse stock before anything is written; one ledger movement per line, all with the one shipment time; then status Shipped. Log "Sales order {number} shipped by user {userId}." after the commit.
- **Acceptance criteria:**
  - AC-26.1:
    - `200` with the design 5.12 Shipped detail: `shipmentNote`, and a Shipped `history` entry with the user and time;
    - one `ShippedToCustomer` movement per line, with the line's quantity as a negative Warehouse change and the sales order number;
    - every movement's `occurredAt` equals the Shipped entry's `at` (design 9, v1.1 default 14).
  - AC-26.2: with 1,001 Bag and 80 Bag in the Warehouse, shipping #1 (600 Bag) leaves 401 Bag and one −600 movement, and production storage is unchanged.
  - AC-26.3:
    - #2 (500 Bag plus 50 Bag of Grated cheese) is refused with `409` `insufficient-stock`, with exactly "Not enough stock: Shredded cheese 1 kg bag in Warehouse has 401 Bag; sales order #2 needs 500 Bag." and no `errors` field;
    - Grated cheese is not named, no movement is created, the status stays Created, and the stock stays 401 and 80;
    - with two short lines, both sentences appear in line order.
  - AC-26.4: after a production order adds 200 Bag (completed through `OrderFlow`), #2 ships, leaving 101 and 30. #3 for exactly 101 Bag ships, leaving 0.
  - AC-26.5: a newer sales order ships before an older Created one for the same item.
  - AC-26.6: shipping a raw material of the InProduction order succeeds. `GET /stock/{itemId}` shows only `warehouse` changed (production storage, committed, pending returns and free leftover are unchanged). A Created production order's estimate (`GET /production-orders/{n}`) is unchanged.
  - AC-26.7, AC-27.3: shipping a Shipped sales order gives `409` `already-shipped` "This sales order has already been shipped.". A Cancelled one (`SalesFlow.InsertSalesOrderAsync`) gives `409` `sales-order-cancelled` "This sales order is cancelled.", even when its stock would be short.
  - AC-26.8: a note of 501 characters gives `400` "Note must be 500 characters or fewer." under `note`, and that `400` comes before the status `409`. No body, `{}`, `null` and an all-spaces note all ship with `shipmentNote: null`.
  - AC-26.9: Production-only, Seller-only and Production plus Seller users get `403` "... It needs the Warehouse or Manager role.". The endpoint has a `401` test, and the matrix covers every caller.
  - AC-26.10, first bullet: two simultaneous ships of one sales order, with both orderings forced (`ForcedOrder("Movements")`), give exactly one `200` and one `409` `already-shipped`, and exactly one movement per line.
  - AC-26.10, second bullet: with 1,000 Bag in the Warehouse, #5 and #6 for 600 Bag each give one `200` and one `409` `insufficient-stock` naming #5 or #6, and the Warehouse holds 400.
  - AC-26.11:
    - Cheese with 100 kg in the Warehouse and 50 kg of free leftover: a ship of 120 kg is refused, naming 120 kg needed and 100 kg in the Warehouse;
    - it is still refused while a 20 kg return is Pending;
    - after the return is received it succeeds, leaving Warehouse 0, production storage 30 and free leftover 30.
  - AC-26.12: with 1,000 kg of Cheese and a Created order planning 1,000 kg, a 100 kg ship succeeds and leaves 900 kg. The order's estimate is unchanged, and its 1,000 kg send then gets `409` `insufficient-stock`.
  - AC-26.13: a sales order whose customer was deactivated after creation ships with `200`.
  - Code review searches confirm the ship adds movements only through `StockLedger.AddAsync` (decision #112).

### T-028: (v1.1) Cancel a sales order

- **Owner:** backend-developer
- **Serves:** US-27 (AC-27.1 to AC-27.4), AC-26.10 third bullet (a ship racing a cancel), AC-29.4 (a deactivated customer's sales order can be cancelled), AC-05.3; design 3.15, 5.12, 7.2 Sales orders; architecture 5.1 (check order), 5.2 (v1.1 rows)
- **Depends on:** T-027
- **Touches:** `src/InventoryTracker.Api/Features/SalesOrders/CancelSalesOrderService.cs` (new), `SalesOrdersController.cs` (one action), `SalesOrderContracts.cs`, `tests/InventoryTracker.Api.Tests/Integration/CancelSalesOrderTests.cs` (new), `Infrastructure/SalesFlow.cs` (`CancelSalesOrderAsync`), `Infrastructure/ExpectedMessages.cs`, `Integration/Scenarios/RoleMatrixTests.cs` (one row; `DesignEndpointCount` 60), `Integration/OpenApiTests.cs`, `src/InventoryTracker.Api/InventoryTracker.Api.http`
- **What to build:** `POST /sales-orders/{number}/cancel` (`Roles.ManagerOrSeller`). In one write transaction: `404`, then the reason (`Text.Clean`, required, at most 500), then the status, then Cancelled with `CancelledAt`, `CancelledById`, `CancellationReason` and `StatusChangedAt`. No movement. Log after the commit.
- **Acceptance criteria:**
  - AC-27.1:
    - `200` with the Cancelled detail, `cancellationReason`, and a Cancelled `history` entry whose `note` is the reason;
    - a sales order a Manager created is cancelled by a Seller-only user;
    - no movement is created and stock is unchanged;
    - it also works when the customer is deactivated.
  - AC-27.2:
    - cancelling a Shipped sales order gives `409` `already-shipped` "This sales order has been shipped and cannot be cancelled.";
    - cancelling a Cancelled one gives `409` `sales-order-cancelled` "This sales order is already cancelled.";
    - a missing, blank or 501-character reason gives `400` "Reason is required." / "Reason must be 500 characters or fewer." under `reason`;
    - a blank reason on a Shipped sales order is `400`, not `409` (reason before status).
  - AC-27.3: after a cancel, the ship gives `409` "This sales order is cancelled." and creates no movement.
  - AC-27.4: a Warehouse-only user gets `403` "... It needs the Manager or Seller role.". There is a `401` test, and the matrix covers every caller.
  - AC-26.10, third bullet: a ship and a cancel of the same sales order at the same time, with both orderings forced (`ForcedOrder("SalesOrders")`). Exactly one succeeds. Either the order ends Shipped with one movement per line and the cancel's `409` "This sales order has been shipped and cannot be cancelled.", or it ends Cancelled with no movement and the ship's `409` "This sales order is cancelled.".
  - Two simultaneous cancels give one `200` and one `409` "This sales order is already cancelled.".

### T-029: (v1.1) Open demand on the stock view

- **Owner:** backend-developer
- **Serves:** US-31 (AC-31.1 to AC-31.4), AC-10.5, AC-28.4, D-12; design 3.16, 5.5; architecture 3.3, 3.5, decision #159
- **Depends on:** T-028 (AC-31.2 ships one sales order and cancels another)
- **Touches:** `src/InventoryTracker.Api/Features/Stock/StockQueries.cs` (a fourth query in `ReadFiguresAsync`; `StockFigures.OpenDemand`), `StockContracts.cs` (`StockResponse.OpenDemand`, after `ProductionStorage`), `StockService.cs`, `tests/InventoryTracker.Api.Tests/Integration/StockTests.cs`
- **What to build:** Open demand as architecture 3.5 describes: summed in SQL for the requested item ids only, inside the same snapshot as the other stock figures, and shown for every item type as a number, never `null`.
- **Acceptance criteria:**
  - AC-31.1, design 5.5: every row of `GET /stock` and `GET /stock/{itemId}` has `openDemand` right after `productionStorage`. It is `0` (not `null`) with no Created sales order, for raw materials and finished products alike.
  - AC-31.2, step by step: with 1,001 Bag in the Warehouse and Created #1 (600) and #4 (500), open demand is 1,100 and the Warehouse 1,001. After #1 ships: 500 and 401. After #4 is cancelled: 0.
  - AC-28.4: two Created sales orders of 600 Bag each give `warehouse` 1,001 and `openDemand` 1,200.
  - Only Created sales orders count: lines of Shipped and Cancelled ones do not.
  - AC-31.3:
    - creating a sales order larger than the stock changes no field of the row except `openDemand` (rows compared before and after, for a raw material with committed material, a Pending return and free leftover);
    - a send and an adjustment of that item still succeed as before.
  - AC-31.4: Production-only and Seller-only users see `openDemand` (`200`).
  - The `itemType` filter still works, and the existing stock performance tests still pass.

### T-030: (v1.1) Shipped-totals reports

- **Owner:** backend-developer
- **Serves:** US-32 (AC-32.1 to AC-32.6), US-33 (AC-33.1 to AC-33.4), D-13, AC-29.3 (reports show the current name); design 2.5, 3.16, 5.13, 7.2 Reports; architecture 3.1 (name order), 3.5, 4.1 item 19, decisions #160, #161
- **Depends on:** T-028
- **Touches:** `src/InventoryTracker.Api/Features/Reports/` (new: `ReportsController.cs`, `ShippedTotalsService.cs`, `ReportContracts.cs`), `tests/InventoryTracker.Api.Tests/Integration/ReportsTests.cs` (new), `Infrastructure/SalesFlow.cs`, `Integration/Scenarios/RoleMatrixTests.cs` (two rows; `DesignEndpointCount` 62), `Integration/OpenApiTests.cs`, `src/InventoryTracker.Api/InventoryTracker.Api.http`
- **What to build:** `GET /reports/shipped-by-item` (filters `from`, `to`, `itemType`, `customerId`) and `GET /reports/shipped-by-customer` (filters `from`, `to`, `customerId`, `itemId`), both `Roles.WarehouseManagerOrSeller`, with the query design of architecture 3.5. Use one shared "shipped lines" query, group it, join after, order by `NameNormalized` then id, and page. Day ranges come from `DayRange`.
- **Acceptance criteria:**
  - AC-32.3, with shipments made through the ship endpoint at `FakeTime` 2026-12-02 and 2026-12-05, and #7 Cancelled:
    - December gives Grated cheese 50 Bag, 1 sales order, then Shredded cheese 1,201 Bag, 3 sales orders, in that order;
    - with Deli Norte, only Shredded cheese 701 Bag, 2 sales orders;
    - from 2026-12-03, Grated cheese 50 Bag, 1, then Shredded cheese 601 Bag, 2.
  - AC-33.3: December gives the three rows in the AC's order with its figures. With `itemId` of Grated cheese, only Cafe Sur.
  - AC-32.1, AC-33.1: the row shapes of design 5.13. Paging works (`pageSize=1&page=2` is the second row; `totalCount` is the number of rows).
  - Name order is ordinal (architecture 3.1, risk 16). Items or customers named "apple", "Banana", "Zeta" and "Émile" come out as apple, Banana, Zeta, Émile, and letter case never changes the order.
  - AC-32.2: Created and Cancelled sales orders do not count, and neither does an adjustment, including a positive Warehouse adjustment that corrects a shipment (AC-28.6).
  - AC-32.4: for each item in the example and each of the AC's periods, `shippedQuantity` equals minus the sum of `warehouseChange` over `GET /movements?type=ShippedToCustomer&itemId=...&from=...&to=...`.
  - AC-32.5:
    - no shipment in the period gives `200` with an empty list;
    - `from` after `to`, a bad date and `itemType=Box` each give `400` with the design 7.2 list messages;
    - an unknown `customerId` or `itemId` gives an empty `200`, and `customerId=abc` gives `400` with the framework wording.
  - Day boundaries: `from` alone, `to` alone and neither (all time) all work. A shipment at 23:59:59 on 2026-12-31 counts for `to=2026-12-31`, and one at 00:00:00 on 2027-01-01 does not.
  - AC-33.4: a deactivated customer's rows still appear, with `isActive: false`.
  - AC-29.3: after a rename, the report shows the new name and sorts by it.
  - AC-32.6, AC-33.2: a Production-only user gets `403` "... It needs the Warehouse, Manager or Seller role.". Warehouse, Manager and Seller get `200`. There is a `401` test, and the matrix covers every caller.

### T-031: (v1.1) End-to-end scenarios, cross-endpoint races, performance

- **Owner:** backend-developer
- **Serves:** AC-26.2 to AC-26.4 (as one chain), AC-26.10 fourth and fifth bullets, AC-28.3, AC-28.4, AC-29.5 (its example), AC-30.4, AC-30.6 to AC-30.8 (end to end), AC-31.2, AC-32.3, AC-33.3, AC-05.3, AC-11.4, PRD sections 5 and 11 (performance with the v1.1 data set; consistency); architecture 7 (v1.1)
- **Depends on:** T-029, T-030
- **Touches:** `tests/InventoryTracker.Api.Tests/Integration/Scenarios/` only (new `SalesScenarioTests.cs` and `SalesPerformanceTests.cs`; `CrossEndpointRaceTests.cs`; `RoleMatrixTests.cs` only if a gap is found), plus `tests/InventoryTracker.Api.Tests/Infrastructure/SalesFlow.cs` if a helper is missing. Product bugs found are reported in the task report and fixed in the owning feature's files under review, as in T-020.
- **What to build:** The PRD's v1.1 worked examples as a client sees them: from an empty database, written only through the API, with the clock set by `FakeTimeProvider`. Add the remaining AC-26.10 races and the performance test of architecture 7 (v1.1). Where the scenario's numbers differ from the PRD's because of the order things are created in (for example sales order #4), the test comment says so, as decision #109c did.
- **Acceptance criteria:**
  - One chain from an empty database:
    - units, items and a recipe;
    - production orders completed so the Warehouse holds 1,001 Bag;
    - customers Deli Norte, Cafe Sur and Pizzeria Lago;
    - AC-26.2, AC-26.3 (exact message) and AC-26.4, through to 0 Bag;
    - AC-31.2's open demand at each step, and AC-28.4.
  - Then the reports for December match AC-32.3 and AC-33.3 exactly, with #7 Cancelled.
  - AC-29.5's example: Pizzeria Lago, never used, is deleted (`204`), and Deli Norte, with a Cancelled sales order, gives `409` with the PRD sentence.
  - AC-26.10, fourth and fifth bullets, each with both orderings forced: a ship against a negative Warehouse adjustment of the same item, a ship of a raw material against a production send, and against a complementary send of it. Each pair exceeds the stock together; at most one succeeds and the Warehouse never goes below zero.
  - A Seller's day (AC-30.4, AC-30.6 to AC-30.8):
    - a Seller-only user creates a customer, creates and cancels a sales order, reads stock, units, items and both reports, and gets `403` on ship, receipts, adjustments, movements and production orders;
    - a Production plus Seller user reads movements, production orders, customers, sales orders and both reports.
  - AC-05.3: every sales order status change in the scenarios has a user and a UTC time in its `history`.
  - AC-11.4, AC-28.3: after the scenarios, the movements of every item and location add up to the stock (`ScenarioApi.AssertMovementsAddUpToStockAsync`).
  - Performance (`SalesPerformanceTests`, in `PerformanceCollection`), at the PRD section 5 v1.1 volume:
    - the volume is 100,000 movements, 1,000 orders, 500 items, 500 customers, and 5,000 sales orders of 1 to 10 lines in mixed statuses over a year;
    - 20 timed calls (after one warm-up) of each request in architecture 7 (v1.1) have a 95th percentile under 1 second;
    - the timings are written to the test output, and the report quotes them.
  - `RoleMatrixTests` has 62 rows, `DesignEndpointCount` is 62, and the full matrix passes for all six callers.

### T-032: (v1.1) OpenAPI and sample requests

- **Owner:** backend-developer
- **Serves:** PRD section 5 (the OpenAPI description lists every endpoint), US-24 to US-33 as documented for clients; design 4, 5.11 to 5.13; architecture 6 (OpenAPI document)
- **Depends on:** T-029, T-030
- **Touches:** `src/InventoryTracker.Api/Common/OpenApi/` (only if a rule is missing), `src/InventoryTracker.Api/InventoryTracker.Api.http`, `.env.example` (one name), `tests/InventoryTracker.Api.Tests/Integration/OpenApiTests.cs`
- **What to build:** Make sure the OpenAPI document describes the 14 new endpoints as precisely as the 48 old ones. Turn the samples the earlier tasks added into one working v1.1 walk-through, appended to the existing one in the `.http` file. It runs after the v1 sections and reuses their product, which a completed order has put in the Warehouse. Its steps:
  - a Manager creates a Seller user, whose password comes from `{{$dotenv SAMPLE_SELLER_PASSWORD}}` (decision #135; the name goes into `.env.example` with an empty value);
  - the Seller signs in, creates, edits, deactivates and reactivates customers, and creates sales orders;
  - `GET /stock` shows `openDemand`;
  - a ship that is too large is refused (`409`), and a ship that fits succeeds;
  - `GET /movements?salesOrderNumber=` lists the shipment;
  - both reports run;
  - a sales order is cancelled;
  - an unused customer is deleted, and deleting a used one gives `409`.
- **Acceptance criteria:**
  - In Development, `/openapi/v1.json` lists exactly the 62 operations. Each new one has its `///` summary and exactly its design 4 success status.
  - The new endpoints with a business `409` document it: `POST` and `PUT /customers`, `DELETE /customers/{id}`, `POST /sales-orders`, ship and cancel. Deactivate, reactivate, the reads and the reports do not.
  - Request schemas require exactly the needed fields:
    - `CustomerRequest`: `name` (not `notes`);
    - `CreateSalesOrderRequest`: `customerId` and `lines` (not `customerReference`);
    - `SalesOrderLineRequest`: `itemId` and `quantity`;
    - `ShipSalesOrderRequest`: none;
    - `CancelSalesOrderRequest`: `reason`.
  - Enums show their names (`SalesOrderStatus`, and the seven `MovementType` values). Response schemas require every field, including `openDemand`, `salesOrderNumber`, `customer` and `history`.
  - `Http_file_has_a_sample_request_for_every_endpoint` passes. The whole `.http` file runs top to bottom against a fresh Development database (manual check, described in the report). No password or other secret is typed into a tracked file.

## Coverage

Every one of the 23 user stories in the PRD is served by at least one task, and every task is done (2026-11-21). The tasks in brackets add a specific criterion or an end-to-end proof.

| User story | Tasks | All done |
|------------|-------|----------|
| US-01 Sign in | T-003, T-014 (AC-01.6; change-password failures count towards the lock-out, #71), T-021 (sign-in documented), T-020 (401 on every endpoint in the role matrix) | yes |
| US-02 First Manager | T-003 | yes |
| US-03 Manage users | T-014 (T-003 provides the token check behind AC-03.6) | yes |
| US-04 Change own password | T-014 | yes |
| US-05 Role checks and audit | T-003 (mechanism), every endpoint task (401 and 403 tests), T-002 (audit columns), T-020 (full role matrix, AC-05.3 audit) | yes |
| US-06 Units | T-006 | yes |
| US-07 Items | T-006 (in-use checks over every reference in architecture 3.2) | yes |
| US-08 Recipes | T-007, T-008a (amount maximum), T-009 (AC-08.4) | yes |
| US-09 Stock receipt | T-008, T-008a (quantity maximum) | yes |
| US-10 Current stock | T-008 (all fields, including committed, pending returns and free leftover), T-018 (committed after a complementary send), T-019 (pending returns end to end) | yes |
| US-11 Movement history | T-013, T-019 (return movements linked to their order, #107), T-020 (AC-11.4, performance) | yes |
| US-12 Adjustments | T-015, T-020 (send and complementary send against a negative adjustment) | yes |
| US-13 Create order | T-009, T-004 (calculations), T-008a (ordered-quantity maximum) | yes |
| US-14 View orders | T-009, T-017 (complementary orders in the detail) | yes |
| US-15 Send materials | T-010, T-004, T-019 (AC-15.11 end to end), T-016 (AC-15.5 cancel bullet), T-020 (AC-15.5 whole scenario) | yes |
| US-16 Request complementary order | T-017, T-005 (notification text) | yes |
| US-17 Approve or reject | T-017 | yes |
| US-18 Complementary send | T-018, T-004, T-020 (examples A and B) | yes |
| US-19 Complete order | T-011, T-004, T-005 (notification text), T-016 (shared cancellation of unsent complementary orders, #104d) | yes |
| US-20 Variances | T-004, T-011 (examples C and D), T-020 (examples A and B) | yes |
| US-21 Cancel order | T-016, T-005 (notification text) | yes |
| US-22 Returns | T-019 | yes |
| US-23 Notifications | T-005 (texts), T-011, T-016, T-017 (creation), T-012 (API) | yes |

**(v1.1) Coverage of version 1.1.** Every one of the 10 new user stories (US-24 to US-33) is served by at least one task, and so is every version 1 story that version 1.1 changes (PRD section 12).

| User story | Tasks | Acceptance criteria and where |
|------------|-------|-------------------------------|
| US-24 Create a sales order | T-025, T-022 (table), T-031 (worked chain) | AC-24.1 to AC-24.9 in T-025 |
| US-25 View sales orders | T-025, T-023 and T-031 (role matrix) | AC-25.1 to AC-25.4 in T-025 |
| US-26 Ship a sales order | T-027, T-026 (ledger rule), T-028 (ship against cancel), T-031 (races with adjustments and sends, worked chain) | AC-26.1 to AC-26.9, AC-26.11 to AC-26.13 in T-027; AC-26.10 bullets 1 and 2 in T-027, bullet 3 in T-028, bullets 4 and 5 in T-031 |
| US-27 Cancel a sales order | T-028 | AC-27.1 to AC-27.4 in T-028; AC-27.3 also in T-027 |
| US-28 Shipments in stock and history | T-026, T-022 (AC-28.5), T-027 (movements from the ship), T-029 (AC-28.4), T-031 (AC-28.3 after the scenarios) | AC-28.1 to AC-28.3, AC-28.6, AC-28.7 in T-026; AC-28.5 in T-022 |
| US-29 Customers | T-024, T-025 (AC-29.3 and AC-29.4 on sales orders, AC-29.8 race), T-027 and T-028 (AC-29.4 ship and cancel), T-030 (AC-29.3 in reports), T-031 (AC-29.5 example) | AC-29.1 to AC-29.7 in T-024 |
| US-30 Seller role | T-023, every endpoint task (its matrix rows), T-031 (a Seller's day) | AC-30.1 to AC-30.8 in T-023 for the v1 endpoints; the new endpoints' rows in T-024 to T-030 |
| US-31 Open demand | T-029, T-031 (worked chain) | AC-31.1 to AC-31.4 in T-029 |
| US-32 Shipped totals per item | T-030, T-031 (example and performance) | AC-32.1 to AC-32.6 in T-030 |
| US-33 Shipped totals per customer | T-030, T-031 (example and performance) | AC-33.1 to AC-33.4 in T-030 |
| (v1.1 changes) US-03 Manage users | T-023 (Seller is given like any role; four-role message) | AC-03.1, AC-03.3 |
| (v1.1 changes) US-05 Role checks and audit | T-023 (v1.1 matrix), T-024 to T-030 (rows), T-031 (AC-05.3 on sales orders) | AC-05.1 to AC-05.3 |
| (v1.1 changes) US-07 Items | T-022 (sales order lines count as use) | AC-07.3, AC-07.4 |
| (v1.1 changes) US-08, US-11, US-14, US-16, US-22 reads | T-023 (W, P, M only; a Seller gets `403`) | AC-11.1, AC-14.1, AC-22.6 (v1.1) |
| (v1.1 changes) US-10 Current stock | T-029 (`openDemand`, AC-10.5) | AC-10.5 |
| (v1.1 changes) US-11 Movement history | T-026 (sales order link and filter) | AC-11.1, AC-11.2 (v1.1) |
| PRD sections 5 and 11 (performance, consistency, data) | T-022 (backstops, upgrade), T-027, T-028, T-025 (races), T-031 (performance and the remaining races), T-032 (OpenAPI lists every endpoint) | |

### T-033: (v1.1) Log who changed a customer

From the v1.1 security review (SEC-26) and owner decision #177. Every customer change (rename, notes edit, deactivate, reactivate, delete) writes one `Information` log line after the commit, naming the action, the customer id and the acting user id only: no customer name, no notes. Follow the existing after-commit logging pattern of the other services. No schema change, no change to any response. Done when a test per action shows the line is written with the ids and without the name or notes, a refused change (400/403/404/409) writes no line, and the full suite passes.

### T-034: Tighter name rules

From the v1.1 security review (SEC-25, its "Fix" list) and owner decision #176, building on the BUG-002 rules (#170 to #174) in `Common/Validation/NameRules.cs`. For customer, unit and item names, on create and rename: refuse a name that starts or ends with a Default_Ignorable_Code_Point or a Hangul filler (ZWJ and ZWNJ stay allowed between letters); count the Hangul fillers and U+2800 as invisible in the "Name is required." check; refuse noncharacters and control (Cc) characters anywhere, including NUL and tab, with "Name contains a character that is not allowed.". Stored names are not rewritten (#172). Done when unit and integration tests prove each rule for all three lists, the BUG-002 tests still pass, and the full suite passes.

### T-035: (v1.2) Batch a shipment's movements

From SEC-24 (docs/06-security-review.md Part 2), owner decision #175 and PRD v1.2 US-55 / A-4 (scheduled before the front-end build, #192). Shipping a sales order with many lines must not hold the write lock for seconds: today 500 lines take 4-8 s, growing faster than the line count. Write the shipment's movements and the stock checks in a batched way inside the same single write transaction, keeping every ShippedToCustomer rule (#157, the one-shipment-per-line index, the all-lines stock check, the races already forced in tests) and every response and message unchanged. Done when the PRD's AC-55.x target is met and measured (row 82: a 500-line shipment answers within 1 second at the section 5 data volume, or a measured alternative the tech lead accepts), the existing ship tests and races pass unchanged, and the full suite passes.

**Tech-lead ruling, 2026-11-27 (decision #223).** The code of #222 stays as built. T-035 is not done until these are added (architecture 3.2, 3.4, 5.1, 7):

- `Data/Configurations/MovementConfiguration.cs`: the covering index `IX_Movements_StockSums` on (`ItemId`, `WarehouseChange`, `ProductionStorageChange`), name in the constant `StockSumsIndexName`. Keep `IX_Movements_ItemId`.
- `Data/Migrations/`: the generated migration `StockSumsIndex`, not hand-edited: one `CreateIndex` in `Up`, one `DropIndex` in `Down`. Its script from `SalesOrders` shows only that `CREATE INDEX` and the history insert. While this is open, no other task may touch `Data/`.
- `tests/.../Integration/UpgradeTests.cs`: nine `Movements` indexes, including `IX_Movements_StockSums` and its `CREATE INDEX` text.
- A query-plan test (`Infrastructure/QueryPlanRecorder`), in `ShipSalesOrderTests` or `StockTests`: the ship's two stock sums and `GET /stock/{itemId}`'s stock read use `USING COVERING INDEX IX_Movements_StockSums`.
- `tests/.../Integration/Scenarios/SalesPerformanceTests.cs`, the AC-55 test: 5 uncounted warm-up rounds (`const int WarmUpRounds = 5`, with the reason in a comment), then the same 20 timed rounds and `p95 < 1000` for both requests. `WithoutWarmUp` takes the warm-up count. Every round, warm-ups included, goes to the test output.
- Comments that quote the old figures or explanation: `Features/Stock/StockQueries.cs` (class remarks, `GetStockAsync(itemIds, ...)`), `StockLedger.AddRangeAsync` where it repeats them, and the AC-55 test's remarks.

Done when the AC-55 test passes 3 times out of 3 on a quiet machine (report the median and p95 of both requests, and the warm-up rounds), the migration script is as above, the query-plan test passes, and the full suite passes.

## (v1.2) Task details: the web front end

Every task below also meets the "Done criteria for every task" above, the (v1.2) items included. "Architecture" means `docs/03-architecture.md`, part "Version 1.2"; "design" means `docs/02-design.md`.

### T-036: (v1.2) Serve the front end: finish A-1

- **Owner:** backend-developer
- **Serves:** US-34 (AC-34.1), US-36 (AC-36.3); PRD A-1, row 88
- **Depends on:** none (the skeleton's `Common/FrontEnd/FrontEndSetup.cs`)
- **Touches:** `src/InventoryTracker.Api/Common/FrontEnd/`, `Program.cs` (start-up warning only), `scripts/run.ps1`; `tests/.../Integration/FrontEndSkeletonTests.cs` (renamed `FrontEndTests.cs`), `tests/.../Acceptance/UnknownPathAcceptanceTests.cs`
- **What to build:** complete the request rules and cache headers of architecture 13. A GET or HEAD outside `/api`, `/health` and `/openapi` whose last segment has a dot and no file answers `404` ProblemDetails `not-found` without a token. `Cache-Control` is `no-cache` on the page and on other files, and `public, max-age=31536000, immutable` under `/assets/`. One Warning at start-up when `wwwroot/index.html` is missing, with the architecture's wording. `run.ps1` prints `Front end:   http://localhost:<port>/ (open it in Edge or Chrome)`, and with `-Published` warns when the folder has no `wwwroot\index.html`. Optionally switch off the SDK's publish-time `.br`/`.gz` copies if one MSBuild property does it; record either way.
- **Acceptance criteria:**
  - Tests (with `WriteFrontEndFile`) for every row of the architecture 13 table, with and without a token: `/`, a deep screen address with a query string, `/sign-in`, `/index.html`, an asset, a missing asset, `/favicon.ico`, HEAD of the page, `POST /stock`, `/api`, `/api/v1/does-not-exist`, `/health`, `/health/details`, `/openapi/v1.json`, plus each cache header.
  - The default `TestApiFactory` (no front end) still gives every answer exactly as before v1.2. The start-up warning is proved through `factory.Logs`.
  - `UnknownPathAcceptanceTests`: the `GET /` and `GET /nope` rows move to a test showing that with a front end they answer the page. The "with a token" `/nope` row becomes an unknown `/api/` path. The rest is unchanged. QA is named in the report (architecture risk 23.5).
- **Done when:** the full suite passes, and a published run (`publish.ps1 -Output` a scratch folder, then `run.ps1 -Published -PublishFolder` it, on a scratch database with throwaway secrets) shows the printed address, the page, a `404` for `/assets/missing.js`, and the cache headers. The run must not touch the owner's database or the repository's `publish\` folder.

### T-037: (v1.2) Security headers, CSP, no CORS (A-2, A-3)

- **Owner:** backend-developer
- **Serves:** US-37 (AC-37.5, AC-37.6, AC-37.7); SEC-10, SEC-11, SEC-27
- **Depends on:** T-036
- **Touches:** `src/InventoryTracker.Api/Common/Security/SecurityHeaders.cs` (new), `Program.cs` (first pipeline step, `AddServerHeader = false` in the existing `ConfigureKestrel`), `tests/.../Integration/SecurityHeadersTests.cs` (new)
- **What to build:** `app.UseSecurityHeaders()` as the first pipeline step, setting through `Response.OnStarting` exactly the headers and the CSP value of architecture 14 on every response; no `Server` header. No CORS code at all.
- **Acceptance criteria:**
  - Tests on the page, an asset, an API `200`, a `401`, a `404` under `/api/`, a `400` with ProblemDetails, a `500` from the existing error probe, and `/health`: CSP exactly the architecture's string, `nosniff`, `no-referrer`, no `Server` header (on real Kestrel too, in the way `RequestBodyLimitTests` does), and `Cache-Control: no-store` under `/api/` only.
  - A test that parses the CSP and checks each AC-37.5 rule: the required directives are present; no `*`, `http:`, `https:` or `data:` in `script-src`; no `'unsafe-inline'` or `'unsafe-eval'` anywhere.
  - AC-37.7: `GET /api/v1/units` with `Origin: http://localhost:5999` (with a token) and a preflight `OPTIONS /api/v1/units` with `Origin` and `Access-Control-Request-Method: POST` get no `Access-Control-*` header.
  - No CORS policy (architecture 14, decisions #253, #256). `ICorsService` stays registered, because `AddControllers()` adds it itself. `No_CORS_policy_is_configured`: no `IConfigureOptions<CorsOptions>` or `IPostConfigureOptions<CorsOptions>`, no default policy in `CorsOptions` or from `ICorsPolicyProvider`, and no endpoint with `ICorsMetadata`. `Only_AddControllers_registers_the_empty_CORS_service`: a bare `ServiceCollection` with `AddControllers()` holds `ICorsService` and no CORS options callback.
  - Source guard `Api_source_has_no_CORS_code` (decision #256), in `SecurityHeadersTests.cs`:
    - It reads every `*.cs` file under `src/InventoryTracker.Api`, recursively, skipping any `bin` or `obj` folder. The repository root is found as in `CalculationsPurityTests`, by walking up to `InventoryTracker.slnx`.
    - It fails if the scan misses `Program.cs` or `Common/Security/SecurityHeaders.cs`, so a wrong path cannot pass with nothing scanned.
    - It skips lines whose trimmed text starts with `//` (comments and `///` doc comments), so explanations such as the one in `SecurityHeaders.cs` may name the header. Every other line is checked whole, including any comment after code on it.
    - It fails on any of these texts, ordinal and case-sensitive: `AddCors`, `UseCors`, `RequireCors`, `EnableCors`, `DisableCors`, `CorsPolicy`, `CorsOptions`, `CorsService`, `Microsoft.AspNetCore.Cors`, `AccessControl` (the `HeaderNames` and `IHeaderDictionary` names). It also fails on `Access-Control-` in any letter case.
    - The failure message lists each file, line number and text found, and says that A-3 forbids CORS, so allowing an origin is a PRD change.
    - The report shows the guard failing once on a temporary `app.UseCors(policy => policy.WithOrigins("http://example.test"));` line in `Program.cs`, removed afterwards, and the full suite passing without it.
- **Done when:** the full suite passes and the published run of T-036 shows the headers (curl output in the report).

### T-038: (v1.2) Escape HTML characters in JSON output (A-5)

- **Owner:** backend-developer
- **Serves:** US-37 (AC-37.9); SEC-27 server hardening (Q108)
- **Depends on:** T-037
- **Touches:** `Program.cs` (controllers' JSON options), `src/InventoryTracker.Api/Common/Errors/ErrorHandlingSetup.cs` or a new `Common/Json/` file for the minimal-API `JsonOptions`, `tests/.../Integration/JsonEscapingTests.cs` (new); any test the change turns red, with the reason in the report
- **What to build:** `JavaScriptEncoder.Create(UnicodeRanges.All)` on both JSON settings, as architecture 14 says, with a comment explaining why and what changes in the raw bytes.
- **Acceptance criteria:**
  - Raw-byte tests: `GET /customers/{id}` for the AC-37.1 customer name `</script><script>alert(1)</script>` holds no `<` and holds `<`, and the parsed name is unchanged. Notes `<b>bold</b> & 'quote'` hold no `<`, `>`, `&` or `'`. A ProblemDetails `detail` with a quote ("A unit named 'kg' already exists.") holds `'` and parses back exactly. A notification's `message` and `data` hold no `<`. An accented name ("Crème") stays readable in the raw bytes.
  - The whole existing suite passes, with any changed test explained. The report states what the OpenAPI document (Development) does.
- **Done when:** the full suite passes and the AC-37.9 example is shown with curl on the published run.

### T-039: (v1.2) API client: requests, failures, paging, stock lookup

- **Owner:** frontend-developer
- **Serves:** F-1, F-2, F-3 (request side), F-6 (paging), F-13 (reading all pages); US-42, US-43, US-47, US-53 (live stock); US-34 (AC-34.6 detection)
- **Depends on:** none (the skeleton's `src/api/json.ts`)
- **Touches:** `src/InventoryTracker.Web/src/api/http.ts`, `paging.ts`, `stockLookup.ts`, `stock.ts` (the stock types and reads), their tests
- **What to build:** architecture 16, all of it. `apiRequest` with relative `/api/v1` paths, the headers, the 30-second timeout, no retries, `parseApiJson`, and the four failure kinds with their exact texts. A small hook that `session/` registers to be told of `401` `not-authenticated` (T-041 connects it). `fieldErrors` with the position map. `readPage`, `readAllPages` (pageSize 100). `readStockFor` with its two strategies and the threshold constant.
- **Acceptance criteria:**
  - Unit tests with a stubbed `fetch`: each failure kind and its exact text; `detail` and `errors` passed through unchanged; the token header only when a session exists; no second request after a timeout or failure; the timeout at 30 s with fake timers; `401` `not-authenticated` triggers the hook while `invalid-credentials` does not; quantities in a body are written from `ExactDecimal`.
  - `readAllPages` stops at `totalCount`. `readStockFor`: 3 items give 3 item reads, at most 4 at a time; 500 items over 500 stock records give at most 5 page reads, stopping when every item is found; a failed read leaves that item missing.
- **Done when:** the front-end done criteria hold.

### T-040: (v1.2) Formatting and safe text

- **Owner:** frontend-developer
- **Serves:** F-7, F-8, F-9, F-10; US-37 (AC-37.1, AC-37.3, AC-37.4)
- **Depends on:** none
- **Touches:** `src/InventoryTracker.Web/src/format/`, `src/components/SafeText.tsx`, their tests
- **What to build:** formatting from `ApiNumber` text (design 12.7, architecture 16): quantities with unit, amounts per unit (up to 6 decimals), signed differences, percentages (signed, two decimals; the tolerance unsigned), "-" with the hidden "none", ranges "975 to 1,015 kg", local times "2026-11-26 08:05", the footer offset "UTC-06:00" ("UTC+00:00"), today's local date for the receipt field, customers and items with ids, " (inactive)". Plus `SafeText` (`<bdi>`, direction markers, line breaks kept where asked) and `hiddenDirectionCharacters(text)` for the edit-field line.
- **Acceptance criteria:**
  - Unit tests with the design's examples: "1,015 kg", "0.6 kg", "0.000125 kg", "+15 kg", "-25 kg", "+1.50%", "0.00%", "2.00%", "-", "Deli Norte (#4)", "Cafe Sur (#7) (inactive)"; large values beyond 15 digits are printed exactly; extra decimals are rounded half away from zero on the digits; times in two time zones (set `TZ` for the test run, or inject the zone).
  - Component tests: the AC-37.1 strings render as text (no element, no link, nothing bold); "Milk" + U+202E + "gnp.exe" shows "Milk[U+202E]gnp.exe" with the marker element, for each of the 13 code points; the line "This text contains hidden direction characters: U+202E." lists each code point once, in order.
- **Done when:** the front-end done criteria hold.

### T-041: (v1.2) Session: sign in, sign out, end of a session

- **Owner:** frontend-developer
- **Serves:** US-34 (AC-34.1 to AC-34.6, AC-34.8)
- **Depends on:** T-039
- **Touches:** `src/InventoryTracker.Web/src/session/` (not `idle.ts`), `src/screens/auth/SignInScreen.tsx`, `src/app/App.tsx` (a minimal router with `/sign-in`, `RequireSession` and a temporary signed-in placeholder; T-042 replaces the placeholder), tests
- **What to build:** architecture 15 except the idle timer: the session module and context, the sign-in screen of design 16.1 (fields, autocomplete, focus, arrival messages from `ended`, the refusal texts, the F-2 text, the busy button), the safe return path, sign-out and every session end as a full page load with blanking first, the `pageshow` guard, and the `401` hook from T-039.
- **Acceptance criteria:**
  - Component and unit tests: AC-34.1 (any address without a session goes to `/sign-in?return=...`, and after signing in that address opens); AC-34.2 (opens `/stock`); AC-34.3 (each message, email kept, password emptied, blank fields checked without a request); AC-34.4 (sign-out replaces the history entry); AC-34.6 (a `401` from any request ends at `/sign-in?ended=expired&return=...`); AC-34.8 (F-2 text); the return-path rule (`//evil.example`, `/sign-in`, `https://...` all give `/stock`); a restored page (`pageshow` persisted) is replaced.
  - The source guard still passes, so the token is in no storage and not in the console.
- **Done when:** the front-end done criteria hold. The AC-34.5 browser check is T-044's.

### T-042: (v1.2) Design system, page frame, menu, route table

- **Owner:** frontend-developer
- **Serves:** US-36 (AC-36.1 to AC-36.5), US-34 (AC-34.2 header), F-8 (footer), F-11 (mechanism), F-15 (frame), PRD section 18 (titles, `lang`, landmarks)
- **Depends on:** T-040, T-041
- **Touches:** `src/InventoryTracker.Web/src/styles/app.css`, `src/app/` (routes, frame, guards, Not allowed, Not found, `RecordLink`, notification-count slot), one placeholder component per screen in every `src/screens/<area>/` folder, `src/main.tsx`, tests
- **What to build:** port `design/mockups/app.css` (no inline styles; fix nothing the design did not ask for). Write the **whole route table** of design 12.1 (architecture 17), with roles, menu entries, titles and parameter checks, and each screen as a placeholder in its own file. Build the frame of design 12.2 and 13: skip link, header at both widths, menu column and panel (focus, Escape, `aria-expanded`), current entry, footer with the offset, document titles. Add 16.33 and 16.34. The Manager's "Notifications (n)" slot shows "Notifications" until T-059.
- **Acceptance criteria:**
  - Component tests: the five menus of AC-36.1 exactly, in order, with groups hidden when empty; AC-36.2 for a sample of addresses per role, and no data request made; AC-36.3 (a direct address opens, with the query string kept); the header identity "maria.lopez@example.com (Manager)" with roles in the API's order; `RecordLink` as text for a role that cannot open the record (AC-36.5); parameter checks (`/production-orders/abc`, `/production-orders/0`, `/production-orders/2147483648`) show 16.34; each document title follows design 12.2.
  - Below 1024 px, the identity, Change password and Sign out are in the menu panel (#196).
- **Done when:** the front-end done criteria hold.

### T-043: (v1.2) List toolkit and the Stock screen (thin slice complete)

- **Owner:** frontend-developer
- **Serves:** US-38 (AC-38.1 to AC-38.5), F-6, F-14, design 12.6 and 12.11; the phone-table follow-up
- **Depends on:** T-042
- **Touches:** `src/InventoryTracker.Web/src/components/` (DataTable, FilterBar, CountLine, Pager, LoadState, `useApiRead`), `src/screens/stock/`, tests
- **What to build:** the list pattern of design 12.6 and 12.11 as shared pieces: `DataTable` with the explicit ARIA roles, `data-label` and the scrolling region (architecture 18); filters applied with the button into the query string; count line with singular and plural; pager; empty sentences; past-the-end; loading after 300 ms; load failure with Try again; Refresh keeping rows. Then the Stock screen of design 16.3.
- **Acceptance criteria:**
  - Component tests: AC-38.1 columns and "-" for finished products; AC-38.2 type filter and paging (F-6: 50 rows, total count, the pager hidden as specified, filter change shows page 1); AC-38.3 the worked example's figures exactly; AC-38.4 the committed order as a link for W, P and M and as text for S (AC-36.5); AC-38.5; refresh only on Refresh (F-14).
  - The table's roles are present on every element, and the phone label is spoken once (the CSS rule of architecture 18).
- **Done when:** the front-end done criteria hold. Sign-in then Stock works with `npm run dev` against the API.

### T-044: (v1.2) End-to-end and accessibility harness

- **Owner:** frontend-developer
- **Serves:** PRD section 18 (accessibility checker, browsers, sizes), US-34 (AC-34.4, AC-34.5), US-37 (AC-37.5 no violations, AC-37.8), F-15
- **Depends on:** T-036, T-037, T-043
- **Touches:** `src/InventoryTracker.Web/package.json` and `package-lock.json` (add `@playwright/test` and `@axe-core/playwright` at the newest releases at least 14 days old (decision #245), exact versions, each release date and `npm audit` reported), `playwright.config.ts`, `e2e/`, `scripts/test.ps1` (end-to-end step and `-SkipEndToEnd`), `.github/workflows/ci.yml` (end-to-end step), `src/InventoryTracker.Web/.gitignore` entries if needed
- **What to build:** Playwright with `channel: 'msedge'` (and a `chrome` project), no browser download. `e2e/support/` starts the API from the Release build through Playwright's `webServer`, on a free local port and a temporary database, with throwaway `JWT_SIGNING_KEY`, `ADMIN_EMAIL` and `ADMIN_PASSWORD` set only for that process. It never uses the owner's database, user-secrets or `.env`. It serves the front end built into `wwwroot`. Shared helpers: sign in as a role (users created through the API as the first Manager), the axe check of architecture 18 at the four sizes, a listener that fails a test on any CSP violation or console error, and a network listener that fails on any request to another origin. Add `npm run e2e`.
- **Acceptance criteria:**
  - `e2e/thin-slice.spec.ts`: sign in, Stock shows; axe clean at every size; AC-34.4 (Back after sign-out shows the sign-in screen, also with the back-forward cache); AC-34.5 (after sign-in, local and session storage, IndexedDB, cookies and cache storage are empty; reload and a second tab show sign-in); AC-37.5 (the CSP header present, no violation); AC-37.8 (same-origin requests only).
  - `scripts\test.ps1` runs it after the Vitest step; CI runs it; the measured CI time is reported.
- **Done when:** the front-end done criteria hold, and `scripts\test.ps1` passes with the end-to-end step.

### T-045: (v1.2) Form toolkit and Change password

- **Owner:** frontend-developer
- **Serves:** US-35 (AC-35.1 to AC-35.3); F-1, F-3, F-4, F-7 (on screen), design 12.4 and 12.5 (banner)
- **Depends on:** T-042
- **Touches:** `src/InventoryTracker.Web/src/components/` (Field, MessageArea, Banner, BusyButton, `useSubmit`, check helpers, flash banner), `src/screens/auth/ChangePasswordScreen.tsx`, tests
- **What to build:** the form pattern. Labelled fields with hints and errors (`aria-invalid`, `aria-describedby`, the "Error:" prefix). The message area: refusal with "Nothing was changed.", focus moved to it, front-end checks without that line. `useSubmit`: one request per click or Enter, `aria-disabled` and the spinner. Password fields emptied after any refusal. The banner (`role="status"`, close, flash through navigation). Then Change password (design 16.2), ending with `ended=password-changed`.
- **Acceptance criteria:**
  - Component tests: AC-35.1 to AC-35.3 with every message; F-3 (double click and double Enter send one request); F-1 (input kept, field messages placed, focus on the message area); F-7 text for "1,5", "1 000" and "-5" with nothing sent; the banner flash shows once.
- **Done when:** the front-end done criteria hold.

### T-046: (v1.2) Dialogs, idle warning and idle sign-out

- **Owner:** frontend-developer
- **Serves:** F-5, design 12.5; US-34 (AC-34.7, AC-34.7a)
- **Depends on:** T-045
- **Touches:** `src/InventoryTracker.Web/src/components/Dialog.tsx` (with the compact variant), `src/session/idle.ts`, the frame's mount point for the warning in `src/app/`, tests
- **What to build:** the Dialog of design 12.5 on `<dialog>` with `showModal()`: title, body, "Go back" and the confirm button (red for destructive actions). Initial focus on the first field or "Go back". Focus returns to the opener. Escape and Go back are held while busy. A refusal stays inside the dialog and is copied to the screen when it closes. Then the idle timer and warning of architecture 15 and design 12.3.
- **Acceptance criteria:**
  - Component tests: F-5 (only confirm sends; Go back, Escape and closing send nothing); the busy hold; focus rules.
  - Idle tests with fake timers: the warning at 14 minutes, text and role; each activity type closes it and restarts the time; the notification refresh is not activity; sign-out at 15 minutes to `ended=idle` and Stock after signing in again (#200); a gap of 14.5 minutes on `visibilitychange` shows the warning, and 15 minutes or more signs out with no warning (#216); the warning sits above an open dialog, and the dialog keeps its input (#217); nothing runs on the sign-in screen.
- **Done when:** the front-end done criteria hold.

### T-047: (v1.2) Picker and Record a receipt

- **Owner:** frontend-developer
- **Serves:** F-13; US-39 (AC-39.1 to AC-39.4); design 12.9
- **Depends on:** T-045
- **Touches:** `src/InventoryTracker.Web/src/components/Picker.tsx`, `src/api/items.ts` (reads only: list and detail, for pickers), `src/api/customers.ts` and `src/api/units.ts` (list reads only), `src/screens/receipts/`, tests
- **What to build:** the combobox picker of design 12.9: all pages at pageSize 100, options with ids and units, narrowing by name or id ("#" ignored), match count, keyboard, the fixed id suffix (#220), only a chosen record sent, the "Choose ... from the list." texts, the loading, failure and empty states. Then Record a receipt (design 16.4). The later area tasks add their write functions to these API files in new functions only, never changing these reads.
- **Acceptance criteria:**
  - Component tests: F-13 narrowing, case-insensitive, and by id; 500 records narrowed within 1 second (measured in the test, the time printed in the output); typed but unchosen text refused; AC-39.1 to AC-39.4 including "Received 1,000 kg of Cheese.", fields emptied but the date kept, focus back on the picker, and the empty-source sentence with no button.
  - `e2e/receipts.spec.ts` with the axe check.
- **Done when:** the front-end done criteria hold.

### T-048: (v1.2) Units

- **Owner:** frontend-developer
- **Serves:** US-56 (AC-56.1 to AC-56.5)
- **Depends on:** T-043, T-046, T-047 (it creates `src/api/units.ts`)
- **Touches:** `src/InventoryTracker.Web/src/screens/units/`, `src/api/units.ts` (add the writes), `e2e/units.spec.ts`
- **What to build:** design 16.24 and 16.25: the list with Add unit for Managers, the detail with rename and delete.
- **Acceptance criteria:** component tests for each criterion, with the API's messages shown word for word and the hidden-direction line on the name field; `e2e/units.spec.ts` with the axe check.
- **Done when:** the front-end done criteria hold.

### T-049: (v1.2) Items

- **Owner:** frontend-developer
- **Serves:** US-57 (AC-57.1 to AC-57.6), US-37 (AC-37.3 edit fields)
- **Depends on:** T-043, T-046, T-047
- **Touches:** `src/InventoryTracker.Web/src/screens/items/` (not the recipe files), `src/api/items.ts` (add the writes), `e2e/items.spec.ts`
- **What to build:** design 16.26 to 16.28 without the recipe panel's content (T-050 fills it): list with type filter, add and edit (full replacement, the unit picker), detail, delete.
- **Acceptance criteria:** component tests for each criterion, including AC-57.3's refusal with the input kept and AC-57.2's empty unit picker; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-050: (v1.2) Recipes

- **Owner:** frontend-developer
- **Serves:** US-58 (AC-58.1 to AC-58.6)
- **Depends on:** T-049
- **Touches:** `src/InventoryTracker.Web/src/screens/items/` (recipe panel and Edit recipe files), `src/api/recipes.ts`, `e2e/recipes.spec.ts`
- **What to build:** the recipe panel on the item detail (design 16.28; not requested for Seller-only users), and Edit recipe (design 16.29) as a line form with Add line and Remove.
- **Acceptance criteria:** component tests for each criterion. AC-58.2: no recipe request for a Seller-only user. AC-58.5: field messages under the right line. AC-58.6 end to end in `e2e/recipes.spec.ts` (two orders show 1,000 kg and 1,010 kg), with the axe check.
- **Done when:** the front-end done criteria hold.

### T-051: (v1.2) Production orders: list, detail, cancel

- **Owner:** frontend-developer
- **Serves:** US-40 (AC-40.1 to AC-40.7), US-44 (AC-44.1 to AC-44.3)
- **Depends on:** T-043, T-046
- **Touches:** `src/InventoryTracker.Web/src/screens/production-orders/` (list and detail files), `src/api/productionOrders.ts` (**every** production-order call, including create, send, complete, cancel and request extra material, so T-052 to T-055 do not edit it), `e2e/production-orders.spec.ts`
- **What to build:** design 16.5 and 16.7: the list with status filter; the detail with facts, line blocks (Plan, Estimate while Created, Sent, Result), "Above tolerance" and "n/a", complementary orders and history; actions per status and role; the cancel dialog.
- **Acceptance criteria:** component tests for each criterion, with AC-40.5 examples A and B exact; F-11 actions per status and role; AC-44.2's extra sentence; AC-40.7 not found. e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-052: (v1.2) New production order

- **Owner:** frontend-developer
- **Serves:** US-41 (AC-41.1 to AC-41.3)
- **Depends on:** T-047, T-051
- **Touches:** `src/InventoryTracker.Web/src/screens/production-orders/NewProductionOrderScreen.tsx` (and its test), `e2e/new-production-order.spec.ts`
- **What to build:** design 16.6.
- **Acceptance criteria:** component tests for each criterion; on success the detail opens with the form's history entry replaced and the banner shown; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-053: (v1.2) Send materials

- **Owner:** frontend-developer
- **Serves:** US-42 (AC-42.1 to AC-42.5)
- **Depends on:** T-051
- **Touches:** `src/InventoryTracker.Web/src/screens/production-orders/SendMaterialsScreen.tsx` (and its test), `e2e/send-materials.spec.ts`
- **What to build:** design 16.8 as a line form: Warehouse stock through `readStockFor` (raw materials); every row required; the dialog listing quantities; refusals under rows, with the "See pending returns" link for `pending-return`; the wrong-status message of #214.
- **Acceptance criteria:** component tests for each criterion, AC-42.3's range message under the Cheese row included. A 500-line order renders and sends one request whose body carries the typed digits. The stock lookup makes at most 5 requests (measured, reported). e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-054: (v1.2) Complete an order

- **Owner:** frontend-developer
- **Serves:** US-43 (AC-43.1 to AC-43.4)
- **Depends on:** T-051
- **Touches:** `src/InventoryTracker.Web/src/screens/production-orders/CompleteOrderScreen.tsx` (and its test), `e2e/complete-order.spec.ts`
- **What to build:** design 16.9, with the production storage figures through `readStockFor`.
- **Acceptance criteria:** component tests for each criterion; the dialog repeats the produced quantity; e2e with the axe check, ending on example A's variances.
- **Done when:** the front-end done criteria hold.

### T-055: (v1.2) Request extra material

- **Owner:** frontend-developer
- **Serves:** US-45 (AC-45.1 to AC-45.3)
- **Depends on:** T-051
- **Touches:** `src/InventoryTracker.Web/src/screens/production-orders/RequestMaterialScreen.tsx` (and its test), `e2e/request-material.spec.ts`
- **What to build:** design 16.10: empty rows not sent, with the body-position-to-row map for field messages; no dialog.
- **Acceptance criteria:** component tests for each criterion, including a field message for body line 0 placed under the second screen row when the first is empty; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-056: (v1.2) Complementary orders: list, detail, approve, reject

- **Owner:** frontend-developer
- **Serves:** US-46 (AC-46.1 to AC-46.3)
- **Depends on:** T-043, T-046
- **Touches:** `src/InventoryTracker.Web/src/screens/complementary-orders/` (list and detail files), `src/api/complementaryOrders.ts` (every call, send included), `e2e/complementary-orders.spec.ts`
- **What to build:** design 16.11 and 16.12: the list with status and order-number filters (#207); the detail with the parent product sub-line (#214 fallback); the approve and reject dialogs (#198).
- **Acceptance criteria:** component tests for each criterion and F-11 per role; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-057: (v1.2) Send a complementary order

- **Owner:** frontend-developer
- **Serves:** US-47 (AC-47.1 to AC-47.3)
- **Depends on:** T-056
- **Touches:** `src/InventoryTracker.Web/src/screens/complementary-orders/SendComplementaryScreen.tsx` (and its test), `e2e/send-complementary.spec.ts`
- **What to build:** design 16.13, with Warehouse stock through `readStockFor`.
- **Acceptance criteria:** component tests for each criterion, AC-47.2's 20.4 accepted and 20.401 refused under the row; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-058: (v1.2) Leftover returns

- **Owner:** frontend-developer
- **Serves:** US-48 (AC-48.1 to AC-48.4)
- **Depends on:** T-043, T-046, T-047
- **Touches:** `src/InventoryTracker.Web/src/screens/returns/`, `src/api/returns.ts`, `e2e/returns.spec.ts`
- **What to build:** design 16.14 to 16.16: the list with status and material filters, New return with "Free leftover now", the detail with Receive and Cancel return dialogs.
- **Acceptance criteria:** component tests for each criterion, including `/returns?status=Pending` from AC-42.4's link; e2e with the axe check, AC-48.3's stock example included.
- **Done when:** the front-end done criteria hold.

### T-059: (v1.2) Notifications and the unread count

- **Owner:** frontend-developer
- **Serves:** US-49 (AC-49.1 to AC-49.5)
- **Depends on:** T-043, T-045
- **Touches:** `src/InventoryTracker.Web/src/screens/notifications/`, `src/api/notifications.ts`, the notification-count provider file that T-042 left as a slot (the only shared file this task may change; it runs alone while it does), `e2e/notifications.spec.ts`
- **What to build:** design 16.17 and 13: the list with the unread filter, Mark read, Mark all read, rows staying in place; the Manager's count, read on every screen opened, every 60 seconds and after marking, never counted as activity, a `401` ending the session.
- **Acceptance criteria:** component tests for each criterion, with fake timers for the 60 seconds; no count control and no count request for other roles; e2e with the axe check, AC-49.4's example included.
- **Done when:** the front-end done criteria hold.

### T-060: (v1.2) Customers

- **Owner:** frontend-developer
- **Serves:** US-50 (AC-50.1 to AC-50.6), US-37 (AC-37.3, AC-37.4)
- **Depends on:** T-043, T-046, T-047 (it creates `src/api/customers.ts`)
- **Touches:** `src/InventoryTracker.Web/src/screens/customers/`, `src/api/customers.ts` (add the writes), `e2e/customers.spec.ts`
- **What to build:** design 16.18 to 16.20: the list with status filter; the detail with notes and line breaks kept; add and edit with the SEC-28 line; deactivate, reactivate and delete dialogs; delete success to the list.
- **Acceptance criteria:** component tests for each criterion and F-11 for Warehouse; AC-37.4's two look-alike customers each shown with its id; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-061: (v1.2) Sales orders: list, detail, cancel

- **Owner:** frontend-developer
- **Serves:** US-51 (AC-51.1 to AC-51.3), US-54 (AC-54.1, AC-54.2)
- **Depends on:** T-043, T-046, T-047
- **Touches:** `src/InventoryTracker.Web/src/screens/sales-orders/` (list and detail files), `src/api/salesOrders.ts` (every call, create and ship included), `e2e/sales-orders.spec.ts`
- **What to build:** design 16.21 and 16.23 without the ship dialog: combined filters with customer and item pickers; the detail facts, lines and history; the cancel dialog.
- **Acceptance criteria:** component tests for each criterion, with F-11 per status and role; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-062: (v1.2) New sales order

- **Owner:** frontend-developer
- **Serves:** US-52 (AC-52.1 to AC-52.4)
- **Depends on:** T-061
- **Touches:** `src/InventoryTracker.Web/src/screens/sales-orders/NewSalesOrderScreen.tsx` (and its test), `e2e/new-sales-order.spec.ts`
- **What to build:** design 16.22 as a line form, with active customers only, Add line and Remove with the focus rules, and no stock check.
- **Acceptance criteria:** component tests for each criterion, AC-52.3's messages under the second line and under the customer; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-063: (v1.2) Ship a sales order

- **Owner:** frontend-developer
- **Serves:** US-53 (AC-53.1 to AC-53.3), US-55 (the busy state on screen)
- **Depends on:** T-061
- **Touches:** `src/InventoryTracker.Web/src/screens/sales-orders/ShipDialog.tsx` (and its test), the one line in the detail file that opens it, `e2e/ship-sales-order.spec.ts`
- **What to build:** the ship dialog of design 16.23: lines with quantity and Warehouse stock through `readStockFor` ("Loading…", "-" and the line on failure), the optional note, the busy state until the answer.
- **Acceptance criteria:** component tests for each criterion, AC-53.2's message in the dialog; a 500-line order's dialog opens with at most 5 stock requests and scrolls with the buttons visible. In `e2e/ship-sales-order.spec.ts`, the axe check, and a 500-line shipment answered and shown as Shipped (the time reported).
- **Done when:** the front-end done criteria hold.

### T-064: (v1.2) Users: list and add

- **Owner:** frontend-developer
- **Serves:** US-59 (AC-59.1 to AC-59.3, AC-59.8)
- **Depends on:** T-043, T-045
- **Touches:** `src/InventoryTracker.Web/src/screens/users/` (list and add files), `src/api/users.ts` (every call), `e2e/users.spec.ts`
- **What to build:** design 16.30 and 16.31: four role checkboxes, the password entered twice.
- **Acceptance criteria:** component tests for each criterion; no password or hash anywhere on screen; e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-065: (v1.2) User detail: roles, access, password

- **Owner:** frontend-developer
- **Serves:** US-59 (AC-59.4 to AC-59.7)
- **Depends on:** T-046, T-064
- **Touches:** `src/InventoryTracker.Web/src/screens/users/UserScreen.tsx` (and its test), `e2e/user-detail.spec.ts`
- **What to build:** design 16.32: the roles, password and access panels, with their dialogs. "Own account" is decided by email (#205): no Set password, and the self-deactivate warning.
- **Acceptance criteria:** component tests for each criterion. AC-59.5's self-deactivation ends at the sign-in screen through the `401`. AC-59.6's message appears in the dialog. AC-59.7's own-detail line replaces the panel. e2e with the axe check.
- **Done when:** the front-end done criteria hold.

### T-066: (v1.2) Close-out: safe text, roles, storage, performance across every screen

- **Owner:** frontend-developer
- **Serves:** US-36 (AC-36.1 whole), US-37 (AC-37.1, AC-37.8 across screens), US-34 (AC-34.5 across a session), F-15, PRD section 18 (performance, 200% zoom, both browsers)
- **Depends on:** T-048 to T-065
- **Touches:** `src/InventoryTracker.Web/e2e/close-out/` (new files only), `e2e/support/` (a seed helper for the section 5 volume, if needed)
- **What to build:** the cross-screen checks, as T-020 and T-031 did for the API. Run the AC-37.1 test data (item, customer, notes, completion reason) through every screen where it appears: text only, no dialog, no request to another address. Sign in with each role combination of AC-36.1 and check its menu and refused addresses. Check storage after a working session. Measure performance at the section 5 volume (sign-in within 3 s; 95% of screens within 2 s; pickers within 1 s). Run every screen at the four sizes in Edge and Chrome with axe, and check every screen for CSP violations.
- **Acceptance criteria:** each check above is a test that passes. The performance figures are in the report. Any failure that belongs to an earlier task is reported, not patched here.
- **Done when:** `scripts\test.ps1` passes in full, end-to-end included.

## (v1.2) Coverage

Every one of the 26 version 1.2 user stories (US-34 to US-59) is served by at least one task. So are the conventions F-1 to F-15, PRD section 18, and the API changes A-1 to A-5.

| User story | Tasks | Acceptance criteria and where |
|------------|-------|-------------------------------|
| US-34 Sign in, sign out, end of a session | T-041, T-046, T-039, T-044, T-066 | AC-34.1 to 34.4, 34.6, 34.8 in T-041; 34.7 and 34.7a in T-046; 34.5 in T-041 (guard) and T-044 (browser); T-066 across a session |
| US-35 Change own password | T-045 | AC-35.1 to 35.3 |
| US-36 Screens by role | T-042, T-043 (AC-36.5 on Stock), every screen task (F-11), T-066 | AC-36.1 to 36.5 in T-042; AC-36.1 whole in T-066 |
| US-37 Safe display of stored text | T-040, skeleton guard test, T-037, T-038, T-044, T-060, T-066 | AC-37.1 and 37.3 in T-040, across screens in T-066; 37.2 by the guard (skeleton) and Trusted Types (T-037); 37.4 in T-040 and T-060; 37.5 to 37.7 in T-037; 37.8 in T-044 and T-066; 37.9 in T-038 |
| US-38 Stock | T-043 | AC-38.1 to 38.5 |
| US-39 Record a receipt | T-047 | AC-39.1 to 39.4 |
| US-40 Production orders, list and detail | T-051 | AC-40.1 to 40.7 |
| US-41 Create a production order | T-052 | AC-41.1 to 41.3 |
| US-42 Send materials | T-053, T-039 (stock lookup) | AC-42.1 to 42.5 |
| US-43 Complete an order | T-054 | AC-43.1 to 43.4 |
| US-44 Cancel a production order | T-051 | AC-44.1 to 44.3 |
| US-45 Request a complementary order | T-055 | AC-45.1 to 45.3 |
| US-46 Complementary orders, approve and reject | T-056 | AC-46.1 to 46.3 |
| US-47 Send a complementary order | T-057 | AC-47.1 to 47.3 |
| US-48 Leftover returns | T-058 | AC-48.1 to 48.4 |
| US-49 Notifications | T-059, T-042 (count slot) | AC-49.1 to 49.5 |
| US-50 Customers | T-060 | AC-50.1 to 50.6 |
| US-51 Sales orders, list and detail | T-061 | AC-51.1 to 51.3 |
| US-52 Create a sales order | T-062 | AC-52.1 to 52.4 |
| US-53 Ship a sales order | T-063, T-039 (stock lookup) | AC-53.1 to 53.3 |
| US-54 Cancel a sales order | T-061 | AC-54.1, 54.2 |
| US-55 Shipment in one batch | T-035 (done), T-063 (busy state, 500-line run) | AC-55.1 to 55.3 in T-035 |
| US-56 Units | T-048 | AC-56.1 to 56.5 |
| US-57 Items | T-049 | AC-57.1 to 57.6 |
| US-58 Recipes | T-050 | AC-58.1 to 58.6 |
| US-59 User management | T-064, T-065 | AC-59.1 to 59.3 and 59.8 in T-064; 59.4 to 59.7 in T-065 |
| F-1 to F-3 | T-039, T-045, T-046 | |
| F-4, F-5 | T-045 (banner), T-046 (dialog) | |
| F-6, F-14 | T-043, T-039 (paging) | |
| F-7, F-8 | skeleton (`json.ts`), T-040, T-045 (on screen) | |
| F-9, F-10 | T-040 | |
| F-11 | T-042 (mechanism), every screen task | |
| F-12 | T-042 (16.34), every detail screen | |
| F-13 | T-047, T-039 (all pages) | |
| F-15 | T-042, T-043, T-044, every screen's e2e, T-066 | |
| PRD section 18 (accessibility, browsers, performance, data in the browser) | T-044, every screen's e2e, T-066 | |
| A-1 to A-5 | T-036 (A-1), T-037 (A-2, A-3), T-035 (A-4, done), T-038 (A-5) | |
