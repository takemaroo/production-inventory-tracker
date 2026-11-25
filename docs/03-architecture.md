# Architecture: Production & Inventory Tracker API

Based on `docs/01-prd.md` (approved 2026-11-11) and `docs/02-design.md`. Decisions referenced as "#n" are rows in `docs/decisions.md`. When this document and the code disagree, the code is wrong: change this document first, then the code. Checked against the code as built (T-001 to T-021, HEAD d09c6db) on 2026-11-21, before QA (decision #114), and updated for the security fixes SEC-01 and SEC-02 (commit 0961b35, decisions #122 to #126) and for the release (phase 7, commit 43b3934, decisions #128 to #133) on 2026-11-22.

(v1.1) Updated on 2026-11-24 for version 1.1 (sales orders): PRD sections 7 to 13 (US-24 to US-33), design v1.1 (every "(v1.1)" part and its section 10), decisions #138 to #153, and the tech-lead decisions #154 to #164 (listed in section 10, to be logged in `docs/decisions.md` with those numbers). Every v1.1 addition or change is marked "(v1.1)" in place. The plan was checked against a prototype on 2026-11-24 (section 3.4 and 3.5): the migration on a v1 database with 100,000 movements, and the report and open-demand queries at the PRD section 5 volume. In short, version 1.1 adds:

- three tables (`Customers`, `SalesOrders`, `SalesOrderLines`) and one nullable column on `Movements` (`SalesOrderNumber`), in one migration that alters the owner's database in place (3.2, 3.4);
- the Seller role, created at start-up on new and existing databases (3.4, 6);
- role-based reads: every read endpoint has an exact role list (4.1 item 13);
- three feature folders, `Features/Customers`, `Features/SalesOrders` and `Features/Reports`, and 14 endpoints (62 in total) (2, 4.2);
- the movement type `ShippedToCustomer`, written only by the ship action through `StockLedger` (5.1, 5.3);
- open demand on the stock view and two computed reports, with no stored totals (3.5).

## 1. Stack

Checked on the owner's machine on 2026-11-11: .NET SDK 10.0.401 (runtimes 10.0.12 and 8.0.18), git 2.55, Node 24.21 (not needed in v1). Docker is not installed and not needed (no Dockerfile in v1, decision #132). Package versions are the latest stable 10.0.x releases on nuget.org on that date.

| Layer | Choice | Version | Why |
|-------|--------|---------|-----|
| Runtime | .NET | 10 (LTS, supported to Nov 2028), SDK pinned to 10.0.x by `global.json` | Installed on the owner's machine. .NET 11.0 shipped this month, but it is short-term support and not installed. The release folder from `scripts\publish.ps1` is self-contained for `win-x64` and bundles runtime 10.0.15 (SEC-06, decision #129); development runs and tests use the installed runtime. |
| Back end | ASP.NET Core Web API with controllers (`[ApiController]`) | 10.0 | Owner's preferred stack. Controllers are the most widely documented style, and attribute routing and `[Authorize]` keep each endpoint readable. |
| Data access | Entity Framework Core + `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.15 | Mainstream ORM. Migrations are versioned schema changes. PostgreSQL later means changing the provider, not rewriting queries. |
| Database | SQLite (one file) | bundled with the provider | PRD section 5. Nothing to install. |
| Users and passwords | ASP.NET Core Identity (`Microsoft.AspNetCore.Identity.EntityFrameworkCore`) | 10.0.15 | Decision #8. Salted password hashing, password policy, lock-out and roles are already built and tested. |
| Tokens | JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`) | 10.0.15 | Decision #8. Standard bearer tokens, validated by the framework. |
| API description | `Microsoft.AspNetCore.OpenApi` | 10.0.15 | Built-in OpenAPI generator (PRD section 5). It uses the `///` comments as descriptions. |
| Health check | `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | 10.0.15 | `GET /health` proves the app and its database are reachable. |
| Tests | xUnit v3 on Microsoft.Testing.Platform, `Microsoft.AspNetCore.Mvc.Testing` | xunit.v3 4.0.3, Mvc.Testing 10.0.15 | xUnit is the most common .NET test framework. `WebApplicationFactory` runs the real app in memory. T-003 adds `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 for token-expiry and lock-out tests. |
| Tools | `dotnet-ef` as a local tool (`dotnet-tools.json`) | 10.0.15 | `dotnet tool restore` installs the same version on any machine. |
| Scripts and CI | PowerShell scripts in `scripts/` (Windows PowerShell 5.1 or PowerShell 7, plain ASCII); GitHub Actions on `windows-latest` | | Owner's machine is Windows (CLAUDE.md). The scripts wrap build, test, publish, run, backup and restore (decisions #128, #129, #133); CI runs the same build and tests (decision #131). |
| Front end | none | | No user interface in v1 (PRD section 3). |

(v1.1) Checked again on 2026-11-24: .NET SDK 10.0.404 and 10.0.401, runtimes 10.0.15 and 10.0.12, git 2.55. On nuget.org, 10.0.15 is still the latest 10.0.x of every Microsoft package above, and 4.0.3 the latest xunit.v3. Version 1.1 adds no package and no tool.

Departures from the owner's preferred stack: none. Deliberately not used, to keep the code small and plain: MediatR, AutoMapper, FluentValidation, FluentAssertions (commercial licence since v8), Swashbuckle. Validation and mapping are plain C#.

## 2. Project structure

```
InventoryTracker.slnx            solution file (the .NET 10 default format); open in VS 2022 17.13+, VS 2026, Rider or VS Code
global.json                      pins the SDK to 10.0.x and selects the Microsoft.Testing.Platform test runner
dotnet-tools.json                local tool manifest (dotnet-ef)
.env.example                     names of the secrets, no values (AC-02.5)
.github/workflows/ci.yml         CI: restore, build Release with -warnaserror, full test suite on windows-latest (decision #131)
scripts/                         PowerShell scripts: build, test, publish, run, new-signing-key, backup-database,
                                 restore-database, and common.ps1 (shared helpers, including the database path);
                                 how to use them is in docs/07-release.md (decisions #128, #129, #133)
publish/                         output of scripts\publish.ps1 (git-ignored); never keep the database here
src/InventoryTracker.Api/
  Program.cs                     composition root only: service registrations, middleware order, start-up work
  appsettings.json               non-secret settings: connection string, tolerance, JWT issuer/audience/lifetime
  appsettings.Development.json   Development log levels
  appsettings.Production.json    Production only: ApiExceptionHandler log level Warning (decisions #126, #130)
  Properties/launchSettings.json launch profiles for `dotnet run` (Development, http://localhost:5133); scripts\run.ps1 never uses them
  InventoryTracker.Api.http      sample requests for manual testing
  Auth/                          AppUser, Roles constants, JWT token service, sign-in and its per-account lock, first-Manager seeding,
                                 start-up checks, 401/403 ProblemDetails handlers (T-003); user admin (T-014)
  Common/                        cross-cutting code with no business rules
    Configuration/               options classes (InventoryOptions, JwtOptions)
    Contracts/                   response records shared by several features (PersonResponse: id and email of who did something)
    Errors/                      ApiException types, the exception handler, ProblemDetails set-up (T-001); ErrorCodes.cs, the
                                 `code` values used by more than one feature (`order-cancelled`, `material-in-use`, decisions #100, #108c;
                                 `too-many-lines`, `request-too-large`, `request-timeout`, decisions #123, #124)
    OpenApi/                     OpenAPI document rules: Bearer scheme, shared 400/401/403/404/413 responses, decimals, enums,
                                 required request fields (T-021, decision #110)
    Paging/                      PageQuery, PagedResult<T> (T-001)
    Quantities/                  rounding, decimal-place checks, number formatting for messages (T-001); input maximums (T-008a);
                                 stored and displayed percentages (decisions #93, #99); all in section 3.1
    Validation/                  ValidationErrors collector, enum/date parsing helpers, Text.Clean (T-001); RequestLimits: the
                                 body, line and role caps (SEC-01, section 4.1 item 12)
    Time/                        Clock: UTC now truncated to whole seconds, from TimeProvider (T-001)
  Data/
    AppDbContext.cs              the one EF Core DbContext (Identity tables + domain tables)
    DatabaseSetup.cs             DbContext registration, migrations at start-up, WAL mode
    WriteTransaction.cs          InWriteTransactionAsync: the single way to change data (T-002, section 5.2)
    Configurations/              one IEntityTypeConfiguration<T> per entity: keys, lengths, indexes (T-002)
    Migrations/                  generated by dotnet-ef; never edit a migration once it is committed
  Domain/                        entities and enums (section 3), no EF or HTTP code
    Calculations/                pure functions for D-3 to D-9: ranges, allocation, variances (T-004)
  Features/                      one folder per resource; each holds its controller, service and contracts
    Units/ Items/ Recipes/ Stock/ Movements/ ProductionOrders/ ComplementaryOrders/ Returns/ Notifications/ Users/
    ProductionOrders/ also holds three helpers shared with other order actions, each written once:
                                 OrderLineMatcher (matching body lines to order lines, decisions #100, #105g, #106b),
                                 WaitingOrderQueue (the leftover allocation queue for estimates and sends, decision #97),
                                 UnsentComplementaryOrders (automatic cancellation on complete or cancel, decision #104d)
    (v1.1) Customers/            CustomersController, CustomerService, CustomerContracts (US-29, T-024)
    (v1.1) SalesOrders/          SalesOrdersController; SalesOrderService (create, list, and ReadDetailAsync, the one builder
                                 of the detail); CancelSalesOrderService; ShipSalesOrderService; SalesOrderContracts;
                                 SalesOrderMapping (US-24 to US-28, T-025, T-027, T-028)
    (v1.1) Reports/              ReportsController (route api/v1/reports), ShippedTotalsService (both reports, one shared
                                 "shipped lines" query), ReportContracts (US-32, US-33, T-030)
    FeatureRegistration.cs       registers every Features/**/*Service, *Ledger and *Queries class with DI, scoped, by convention (decision #77)
tests/InventoryTracker.Api.Tests/
  Infrastructure/                TestApiFactory (app + temporary SQLite file), auth and data helpers (Seed, ApiAssert, ApiJson),
                                 ExpectedMessages (literal expected texts, decision #88), OrderFlow (order, complementary and
                                 return set-ups, decision #101), ForcedOrder and the interceptors that force race orderings,
                                 PerformanceCollection (decision #109a), HeldWriteLock (holds SQLite's write lock to prove a
                                 request is refused before its write transaction, SEC-01), test-only probe controllers
  Unit/                          tests of pure code: Domain/Calculations, Common/Quantities, notification messages
  Integration/                   HTTP tests through TestApiFactory, one file per feature (<Feature>Tests.cs)
    Scenarios/                   worked examples from an empty database, the role matrix, cross-endpoint races (T-020)
```

(v1.1) Additions outside the feature folders, each in one place (decision #161):

```
src/InventoryTracker.Api/
  Auth/Roles.cs                  (v1.1) Seller, and the role sets WarehouseProductionOrManager, WarehouseManagerOrSeller,
                                 ManagerOrSeller (section 4.1 item 13)
  Common/Contracts/              (v1.1) CustomerSummary: { id, name, isActive }, the customer shown inside a sales order
                                 and a report row (design 2.3)
  Common/Validation/             (v1.1) BoolParser: the true/false query filter (unreadOnly, isActive), moved out of
                                 NotificationService; DayRange: the start and end of a whole UTC day for from/to
                                 filters, moved out of MovementService and used by the reports
  Domain/                        (v1.1) Customer, SalesOrder, SalesOrderLine, SalesOrderStatus; MovementType gains
                                 ShippedToCustomer; Movement gains SalesOrderNumber
  Data/Configurations/           (v1.1) CustomerConfiguration, SalesOrderConfiguration, SalesOrderLineConfiguration
  Data/Migrations/               (v1.1) one new migration, SalesOrders, hand-edited before its first commit (section 3.4)
tests/InventoryTracker.Api.Tests/
  Infrastructure/SalesFlow.cs    (v1.1) shared customer and sales order set-ups (create through the API, insert a sales
                                 order in any status through the DbContext, ship, cancel), like OrderFlow (decision #101)
  Integration/                   (v1.1) UpgradeTests, CustomersTests, SalesOrdersTests, ShipSalesOrderTests,
                                 CancelSalesOrderTests, ReportsTests
    Scenarios/                   (v1.1) SalesScenarioTests (worked examples), SalesPerformanceTests (v1.1 volume)
```

Inside a feature folder, for example `Features/ProductionOrders/`:

- `ProductionOrdersController.cs`: thin. Route, `[Authorize(Roles = ...)]`, call the service, return the result. No business rules.
- `ProductionOrderService.cs` (or one service per action, such as `SendOrderService.cs`, when a file grows past about 300 lines): every rule, check and calculation call.
- `ProductionOrderContracts.cs`: request and response records, exactly as in `docs/02-design.md` section 5.

Rules that keep tasks parallel-safe:

- Services are registered automatically (`FeatureRegistration`), so feature tasks do not edit `Program.cs`.
- Entity configurations are discovered automatically (`ApplyConfigurationsFromAssembly`), so feature tasks do not edit `AppDbContext.cs`.
- T-002 created the whole schema in one migration. Feature tasks should not need schema changes. If one does, it adds a migration and must not run in parallel with another task that touches `Data/`. As built there are four migrations: `InitialIdentity` (skeleton), `DomainModel` (T-002), `NotificationMessageUnbounded` (T-011, decision #63) and `PercentagePrecision` (T-011, decision #82). On SQLite the last two have empty `Up`/`Down`, because SQLite keeps no length or precision in its schema; their effect is in the model snapshot, which a PostgreSQL port will turn into real column changes (decision #98g).
- (v1.1) T-022 creates the whole v1.1 schema in one migration, `SalesOrders` (section 3.4), like T-002 did for v1. No later v1.1 task touches `Data/`. T-022 must not run at the same time as any other task.
- (v1.1) Every task that adds an endpoint also adds, in the same commit, its row in the role matrix (`RoleMatrixTests.Endpoints`), its OpenAPI expectations (`OpenApiTests`) and one sample request in `InventoryTracker.Api.http`. Three existing tests compare those lists with the endpoints the app serves, so the suite would fail otherwise (decision #164). As a result, endpoint tasks share these three files and run one at a time.

## 3. Data model

### 3.1 Conventions (T-002 implements these; they apply to every entity)

- **Quantities** are `decimal`, configured with `HasPrecision(18, 3)`. Recipe amounts per unit use `(18, 6)`. Stored percentages use `(28, 6)` (decision #82; the `DomainModel` migration created them as `(18, 6)`, and T-011's `PercentagePrecision` widened them in the model): the unrounded value rounded to 6 places, which is far finer than the 2 places shown. SQLite stores decimals as exact text; PostgreSQL will use `numeric`. Never `double` or `float`.
- **Percentages: one storage rule, one display rule** (decisions #58, #93, #94, #99), both in `Common/Quantities/Quantity.cs` and nowhere else. `Quantity.StoredPercent(value)` rounds the unrounded percentage to 6 places, half away from zero; the code rounds before saving because SQLite does not apply `HasPrecision` on write. `Quantity.DisplayPercent(value)` is `StoredPercent` followed by rounding to 2 places, half away from zero, so every displayed percentage (order responses, the estimate's `tolerancePercent`, notification messages and `data`) is round(round(x, 6), 2) and a notification can never disagree with its order by 0.01. A zero result is returned as a plain `0.00`, never a negative zero. Both have a `decimal?` overload that keeps `null` (decision #49). Whether a usage variance is above tolerance is decided from the unrounded value (AC-20.4, decision #47), never from either of these.
- **Input maximums (decision #80, owner).** Every quantity a client sends (receipt, ordered and produced quantities, sends, complementary requested amounts and sends, leftover entered, adjustments, returns) is at most **999,999,999.999**. A recipe amount per unit is at most **999,999.999999**. For an adjustment the limit applies to the size, so -999,999,999.999 is the lowest. A value above the limit is `400` `validation`, with the design 7.2 message under the field's JSON path ("{Field} must be 999,999,999.999 or less.", "Amount per unit must be 999,999.999999 or less.", and for adjustments below the minimum "Quantity must be between -999,999,999.999 and 999,999,999.999.").
  - **One shared check** in `Common/Quantities/Quantity.cs` (T-008a), used by every service, never re-typed: constants `Quantity.MaxQuantity` (999,999,999.999m) and `Quantity.MaxAmountPerUnit` (999,999.999999m); `Quantity.IsWithinMax(value)` (true when `Math.Abs(value) <= MaxQuantity`, so it serves signed adjustments too) and `Quantity.IsWithinMaxAmountPerUnit(value)`; and the message helpers `Quantity.AboveMaxMessage(fieldLabel)` ("{Field} must be 999,999,999.999 or less."), `Quantity.BelowMinMessage(fieldLabel)` ("{Field} must be between -999,999,999.999 and 999,999,999.999.", used only by adjustments, the one signed input, decision #103) and `Quantity.AmountPerUnitAboveMaxMessage`. `IsWithinMax` answers for both ends; the service picks `AboveMaxMessage` or `BelowMinMessage` by the sign. The same file holds the decimal-place limits `Quantity.MaxQuantityDecimals` (3) and `Quantity.MaxAmountPerUnitDecimals` (6). The messages are written as literal text, not through `MessageFormat.Number`, which keeps 3 places and would print 999,999.999999 as "1,000,000". Services add the message to their `ValidationErrors` collector, so it is reported with every other field error in one `400`.
  - **Order of checks on one field** (one message per field, decision #75): required, then the sign rule (greater than 0, or not zero for adjustments), then decimal places, then the maximum.
  - **Why these numbers (overflow).** A `decimal(18,3)` column holds 15 digits before the point, so its largest value is 999,999,999,999,999.999, just under 1e15. The largest product the system computes from input is amount per unit × quantity (planned amount, AC-13.1; required for actual output, D-9): 999,999.999999 × 999,999,999.999 = 999,999,999,998,000.000000001, which is below 1e15 and rounds (3 places) to 999,999,999,998,000.000. So every planned and required amount fits the column, and the multiplication itself, about 24 significant digits, is exact in C# `decimal` (28 digits). Sums and differences of these values (variances, committed material, leftover applied) stay inside the column too; only a stock total built from about a million maximum-size movements could pass it (section 9, risk 12). Send ranges (D-7) can reach twice a planned amount at a tolerance of 100%, but ranges are only calculated and shown, never stored. **Percentages are the exception** (decision #82): dividing by the smallest ordered or required amount (0.001) multiplies a variance by 100,000, so with capped inputs a variance percentage can reach about 1e14 (for example ordered 0.001, produced 999,999,999.999), and far more in a contrived usage case. `(18, 6)` allows only 12 digits before the point, so PostgreSQL would refuse the value with a `500`. `(28, 6)` allows 22, which covers every value the D-9 formulas can produce from capped inputs and stays within C# `decimal`.
  - **Computed movements are not input.** A `ConsumedInProduction` movement carries the actual usage (D-9), which can be larger than any single input (leftover applied plus sends), so `StockLedger` keeps its own guard at the column's capacity (`StockLedger.MaxQuantity`, 999,999,999,999,999.999): a programming-error check (`ArgumentException`, `500`), not a client check. Services must use `Quantity.MaxQuantity` for the `400`, never the ledger's constant (decision #81).
- **Decimals in queries.** EF Core 10 translates decimal `SUM`, comparisons and `ORDER BY` on SQLite (verified on this machine 2026-11-11). They are allowed, but stock sums must always be filtered by item (see 5.1).
- **Times** are `DateTime` in UTC, truncated to whole seconds by `Clock.UtcNow` (design 2.3). SQLite loses `DateTimeKind`, so T-002 adds a model-wide value converter (in `ConfigureConventions`) that marks every `DateTime` read from the database as UTC. JSON then writes the `Z` suffix. `DateTimeOffset` is not used: EF Core cannot sort or compare it on SQLite.
- **Enums** are stored as their names (`HasConversion<string>()`, max 30 characters), so the database is readable and enum values can be reordered.
- **Case-insensitive unique names** (units, items) use a `NameNormalized` column (NFC, trimmed, `ToUpperInvariant()`, computed by `NameNormalizer.Normalize`) with a unique index. Every create and rename goes through `Common/Validation/NameRules.Validate`, which converts the name to NFC, trims it, refuses names with no visible characters or that cannot be normalized, checks the length on the stored text and returns the name and its key (BUG-002, decisions #170, #171, #173, #174). For text already in NFC the key is the same as before v1.1, so stored rows need no migration (#172). This works the same on SQLite and PostgreSQL (SQLite's `NOCASE` is SQLite-only and ASCII-only). Emails use Identity's own normalized columns; their uniqueness comes from `NormalizedUserName` (see AppUser in 3.2).
- **Text lengths.** Short text fields have a maximum length, checked by the services with the design's messages. Text that the system composes from other fields and that has no natural upper bound (`Notification.Message`, `Notification.DataJson`) has no maximum length: EF Core maps an unlimited `string` to `TEXT` on SQLite and `text` on PostgreSQL, which in PostgreSQL is as efficient as `varchar(n)`. Composed text is never truncated.
- **Numbers.** Production orders, complementary orders and returns use their number as the primary key (`int`, database-generated, starting at 1). Each table has its own sequence (design 2.1). No separate `Id`.
- **Deletes.** Every foreign key is `DeleteBehavior.Restrict`, except recipe lines, which belong to their recipe (`Cascade`; saving a recipe replaces its lines, AC-08.1). Movements, orders, complementary orders, returns, notifications and users are never deleted (PRD section 5). Only units and unused items can be deleted.
- **Audit.** Every "who" column is a required or nullable `string` foreign key to `AspNetUsers.Id`, paired with a UTC "when" column (AC-05.3).
- (v1.1) **Customer names** follow the unit and item rule: `Name` in NFC, trimmed (via `NameRules`) and `NameNormalized` (`NameNormalizer.Normalize`) with a unique index (AC-29.2). A deleted customer's row is gone, so its name is free again (AC-29.5).
- (v1.1) **Numbers and ids.** Sales orders use their number as the primary key, with their own sequence starting at 1, like production orders (D-10). Customers use an integer `Id`. Both are `INTEGER PRIMARY KEY AUTOINCREMENT` on SQLite (EF Core's default for an integer key), so **a committed number or id is never reused**, even after a delete. A rolled-back insert leaves no gap (its number goes to the next insert), which is harmless because numbers are returned and logged only after the commit (decision #163). (Corrected after the T-022 review, which tested it on SQLite.) A deleted customer's id therefore stays `404` for ever (AC-29.5) and can never come to name a newer customer (decision #162).
- (v1.1) **Deletes.** Customers are the one new entity that can be deleted, and only when no sales order names them (AC-29.5). Every new foreign key is `Restrict`, sales order lines included: sales orders are never deleted (PRD section 5), so their lines need no cascade.
- (v1.1) **Name order for the reports** is ordinal on `NameNormalized` (design 2.5). SQLite compares `TEXT` with its default `BINARY` collation, byte by byte, which for UTF-8 is the same as ordinal code-point order, so `ORDER BY NameNormalized` is the rule as written. A PostgreSQL port must keep it: give `Items.NameNormalized` and `Customers.NameNormalized` the `"C"` collation (`UseCollation("C")` in their configurations), or the default locale-aware collation would change the order (section 9, risk 16).
- (v1.1) **Text lengths**: customer name 100 (`ColumnRules.CustomerNameLength`), customer reference 100 (`ColumnRules.CustomerReferenceLength`), customer notes, shipment note and cancellation reason 500 (`ColumnRules.TextLength`). All are counted in code points through `Text.Length` (decision #116) and checked by the services after trimming (design 2.6).

### 3.2 Entities

Table names are the plural class names. "FK" means a foreign key with `Restrict` unless stated.

#### AppUser (ASP.NET Core Identity, table `AspNetUsers`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | string | PK | Identity generates a GUID string. |
| Email, UserName | string(256) | required; NormalizedUserName unique | UserName always equals Email (trimmed, no spaces). Uniqueness ignores case (AC-03.2). |
| PasswordHash, SecurityStamp, LockoutEnd, AccessFailedCount, ... | Identity | | Standard Identity columns. Never returned (AC-03.4). |
| IsActive | bool | default true | AC-03.6. |
| CreatedAt | DateTime | required | |

**How email uniqueness holds (decision #64).** Identity's `EmailIndex` on `NormalizedEmail` is **not** unique; only `UserNameIndex` on `NormalizedUserName` is. `User.RequireUniqueEmail = true` is a check in `UserManager` code, not a database constraint, so two simultaneous creates could both pass it. Uniqueness is therefore guaranteed by the rule **UserName = Email**: every code path that creates a user (first-Manager seeding in T-003, `POST /users` in T-014) sets `UserName` to the same trimmed email, and an email containing spaces is refused. No endpoint changes an email in v1; if one is ever added, it must change `UserName` with it. The unique `UserNameIndex` then refuses a duplicate email, whatever its letter case, as a database constraint (a `DbUpdateException` from it becomes `409` `concurrent-change`, section 4.1 item 11). Sign-in looks users up by `NormalizedEmail` (`FindByEmailAsync`), which finds at most one user because of this rule.

Roles are Identity roles named `Warehouse`, `Production`, `Manager` (tables `AspNetRoles`, `AspNetUserRoles`), seeded at start-up. (v1.1) And `Seller`, the fourth role (AC-30.1). It is created at start-up like the other three, on a new database and on an existing version 1 database alike, and no user is given it (AC-30.3, section 3.4, decision #155).

#### UnitOfMeasure (`Units`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK | |
| Name | string(50) | required, trimmed, 1–50 | AC-06.1 |
| NameNormalized | string(50) | required, unique | AC-06.2 |

#### Item (`Items`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK | |
| Name | string(100) | required, 1–100 | design 2.6 |
| NameNormalized | string(100) | required, unique | AC-07.1 |
| Description | string(500)? | | |
| Type | ItemType (text) | required | RawMaterial or FinishedProduct |
| UnitId | int | FK → Units | AC-06.4 relies on Restrict |
| CreatedAt | DateTime | required | list order |

"In use" (AC-07.3, AC-07.4) means at least one movement, or a recipe line naming it (as raw material or through its own recipe), or an order line, or a production order for it as the finished product, or a complementary-order line, or a return. (v1.1) Or a sales order line, whatever the sales order's status (AC-28.5). `ItemService.IsInUseAsync` gains that check (on the `SalesOrderLines.ItemId` index), and the delete message becomes "{item} has movements, recipe lines, order lines or sales order lines and cannot be deleted." (design 7.2). `ItemsTests.Item_in_use_check_covers_every_foreign_key_to_items` reads every foreign key to `Items` from the model, so the check must be added in the same task as the table (T-022).

#### Recipe (`Recipes`) and RecipeLine (`RecipeLines`)

| Recipe field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| FinishedProductId | int | PK and FK → Items | one recipe per finished product |
| UpdatedAt | DateTime | required | design 5.4 `updatedAt` |
| UpdatedById | string | FK → users | |

| RecipeLine field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK | |
| FinishedProductId | int | FK → Recipes, Cascade | |
| LineNo | int | required | order as submitted; used for "recipe line order" in messages |
| RawMaterialId | int | FK → Items; unique with FinishedProductId | AC-08.2 no duplicates |
| AmountPerUnit | decimal(18,6) | > 0, at most 999,999.999999 | AC-08.2, decision #80 |

A finished product "has a recipe" when a `Recipe` row exists. A recipe always has at least one line (AC-08.2) and at most 500 (decision #123; `RequestLimits.MaxLines`, section 4.1 item 12). Orders copy their lines from the recipe (AC-13.1), so an order has at most 500 lines too.

#### Movement (`Movements`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK | |
| Type | MovementType (text) | required, indexed | D-2 |
| ItemId | int | FK → Items, indexed | |
| WarehouseChange | decimal(18,3)? | | signed; null when the Warehouse is not touched |
| ProductionStorageChange | decimal(18,3)? | | signed; null when Production storage is not touched |
| OccurredAt | DateTime | required, indexed | |
| UserId | string | FK → users | AC-05.3 |
| ProductionOrderNumber | int? | FK, indexed | required on SentToProduction (for a complementary send, the parent, design 5.6), ConsumedInProduction and ProducedOutput; optional on ReturnedToWarehouse (copied from the return's linked order, decision #107); never on Received or Adjustment |
| ComplementaryOrderNumber | int? | FK, indexed | SentToProduction only, optional; always with its parent's ProductionOrderNumber |
| ReturnNumber | int? | FK | required on ReturnedToWarehouse, and only there |
| ReceiptDate | DateOnly? | | required on Received, and only there |
| Note | string(500)? | | receipt note; Received only, optional |
| Reason | string(500)? | | adjustment reason; required on Adjustment, and only there |
| (v1.1) SalesOrderNumber | int? | FK → SalesOrders (Restrict); unique filtered index with ItemId | required on ShippedToCustomer, and only there (AC-28.1, decision #157) |

(v1.1) `MovementType` gains a seventh value, `ShippedToCustomer`, after `Adjustment` (design 2.3 order, which messages follow). Enums are stored as names, so adding it changes no stored row. **Index `IX_Movements_OneShipmentPerLine`**: unique on (`SalesOrderNumber`, `ItemId`) with filter `"SalesOrderNumber" IS NOT NULL`. It has two jobs. First, it is the database backstop for "exactly one ShippedToCustomer movement per line" (AC-26.10), as the filtered InProduction index is for AC-15.10. A sales order has each item at most once, so one movement per (sales order, item) is one per line. Second, it is the index the `salesOrderNumber` filter uses: SQLite uses a partial index when the query implies its filter, and `SalesOrderNumber = 5` implies `IS NOT NULL` (checked in the prototype: `SEARCH Movements USING COVERING INDEX IX_Movements_OneShipmentPerLine`).

Which change columns each type fills (D-2): Received W+; SentToProduction W−, P+ (same quantity); ConsumedInProduction P−; ProducedOutput W+; ReturnedToWarehouse W+, P− (same quantity); Adjustment exactly one of W or P, either sign; (v1.1) ShippedToCustomer W− only (Production storage null, D-11). No movement is ever created with a zero quantity (PRD row 17, AC-15.4). Movements are insert-only (AC-11.3): `Movement` properties are `init`, and `AppDbContext.SaveChanges` refuses Modified or Deleted movements (decision #35). The link and text rules in the Notes column (decision #87) are enforced by `StockLedger` (section 5.1); `Note` and `Reason` are stored trimmed, never blank, at most 500 characters.

#### ProductionOrder (`ProductionOrders`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Number | int | PK | "order #12" |
| FinishedProductId | int | FK → Items | |
| OrderedQuantity | decimal(18,3) | > 0 | |
| Status | OrderStatus (text) | required, indexed | Created, InProduction, Completed, Cancelled |
| CreatedAt / CreatedById | DateTime / string | required | |
| SentAt / SentById | DateTime? / string? | | set when it becomes InProduction |
| CompletedAt / CompletedById | DateTime? / string? | | |
| CancelledAt / CancelledById | DateTime? / string? | | |
| StatusChangedAt | DateTime | required | latest of the above; list summary field |
| ProducedQuantity | decimal(18,3)? | | |
| OutputVariance | decimal(18,3)? | | D-9 |
| OutputVariancePercent | decimal(28,6)? | | D-9; stored with `Quantity.StoredPercent` (decision #99); widened from (18,6) by T-011 (decision #82) |
| ToleranceAtCompletionPercent | decimal(28,6)? | | AC-20.10; stored with `Quantity.StoredPercent` (decision #98e); widened from (18,6) by T-011 (decision #82) |
| CompletionReason | string(500)? | | |
| CancellationReason | string(500)? | | |

Indexes: `CreatedAt`; and a unique filtered index on `Status` with filter `"Status" = 'InProduction'`, which lets the database itself refuse a second InProduction order (AC-15.10 backstop, section 5.2). Filtered indexes work on SQLite and PostgreSQL.

The status `history` in responses (design 5.7) is built from the four timestamp pairs. There is no history table, because each status is reached at most once.

#### ProductionOrderLine (`ProductionOrderLines`)

| Field | Type | Fixed when | Notes |
|-------|------|------------|-------|
| Id | int PK | | |
| OrderNumber | int FK | creation | unique with RawMaterialId |
| LineNo | int | creation | recipe line order |
| RawMaterialId | int FK → Items | creation | |
| AmountPerUnit | decimal(18,6) | creation | recipe snapshot (AC-08.4) |
| PlannedAmount | decimal(18,3) | creation | round3(AmountPerUnit × OrderedQuantity) |
| LeftoverApplied | decimal(18,3)? | send | D-6 |
| WarehouseSend | decimal(18,3)? | send | may be 0 |
| CarriedLeftover | decimal(18,3)? | send | D-8 |
| ComplementarySends | decimal(18,3)? | send (0), then each complementary send | D-3 |
| LeftoverEntered | decimal(18,3)? | completion | physical total (PRD row 1) |
| LeftoverRemaining | decimal(18,3)? | completion | D-9 |
| ActualUsage | decimal(18,3)? | completion | D-9 |
| RequiredForActualOutput | decimal(18,3)? | completion | D-9 |
| UsageVariance | decimal(18,3)? | completion | D-9 |
| UsageVariancePercent | decimal(28,6)? | completion | D-9; stored with `Quantity.StoredPercent`; null when the required amount rounds to 0 (decision #49); widened from (18,6) by T-011 (decision #82) |
| UsageVarianceAboveTolerance | bool? | completion | from the unrounded percent (AC-20.4) |

#### ComplementaryOrder (`ComplementaryOrders`) and ComplementaryOrderLine (`ComplementaryOrderLines`)

| ComplementaryOrder field | Type | Notes |
|-------|------|-------|
| Number | int PK | own sequence |
| ParentOrderNumber | int FK → ProductionOrders, indexed | |
| Status | ComplementaryOrderStatus (text), indexed | PendingApproval, Approved, Rejected, Sent, Cancelled |
| Reason | string(500) | required |
| RequestedAt / RequestedById | DateTime / string | |
| ApprovedAt / ApprovedById | nullable | |
| RejectedAt / RejectedById | nullable | |
| DecisionNote | string(500)? | optional rejection note |
| SentAt / SentById | nullable | |
| CancelledAt / CancelledById | nullable | automatic cancellation: the user who completed or cancelled the parent |
| CancellationNote | string(100)? | "Parent order completed" / "Parent order cancelled" |

| ComplementaryOrderLine field | Type | Notes |
|-------|------|-------|
| Id | int PK | |
| ComplementaryOrderNumber | int FK | unique with RawMaterialId |
| LineNo | int | |
| RawMaterialId | int FK → Items | must be on the parent order |
| RequestedAmount | decimal(18,3) | > 0 |
| SentQuantity | decimal(18,3)? | set when Sent |

#### LeftoverReturn (`Returns`)

| Field | Type | Notes |
|-------|------|-------|
| Number | int PK | own sequence |
| ItemId | int FK → Items | raw material |
| Quantity | decimal(18,3) | > 0 |
| Reason | string(500) | required |
| ProductionOrderNumber | int? FK | optional link, any status |
| Status | ReturnStatus (text) | Pending, Received, Cancelled; index on (Status, ItemId) |
| CreatedAt / CreatedById | DateTime / string | |
| ReceivedAt / ReceivedById | nullable | |
| CancelledAt / CancelledById | nullable | |

#### Notification (`Notifications`) and NotificationRead (`NotificationReads`)

| Notification field | Type | Notes |
|-------|------|-------|
| Id | int PK | |
| Type | NotificationType (text) | the five types |
| ProductionOrderNumber | int FK | every type relates to an order |
| ComplementaryOrderNumber | int? FK | ComplementaryAwaitingApproval only |
| CreatedAt | DateTime, indexed | |
| Message | text (no maximum length), required | design 7.3. Never truncated (decision #63). |
| DataJson | text (no maximum length), required | the `data` object of design 5.10, serialized camelCase |

Why `Message` has no maximum length (decision #63): one message lists every material of an order (AC-19.7, AC-21.3) or every line of a complementary request, plus names of up to 100 characters, units of up to 50, a reason of up to 500 and an email of up to 256. The T-005 review measured real messages at 846, 1,071 and 1,659 characters. Since SEC-01 a recipe, and so an order, has at most 500 lines (decision #123), which bounds a message at roughly 90,000 characters (500 materials of about 170 characters each, plus the reason and email); that is far beyond any sensible column limit, so the column stays unlimited and no fixed limit is safe. Truncating would drop materials (AC-19.7, AC-21.3) and facts (design 6). The `DomainModel` migration created the column with a 1,000-character limit; T-011 removed it with the migration `NotificationMessageUnbounded`, before any notification was saved. SQLite never enforced that limit (so that migration's `Up`/`Down` are empty on SQLite, decision #98g), but PostgreSQL would (`character varying(1000)`); the model snapshot now says "unlimited", which is what a port will use.

| NotificationRead field | Type | Notes |
|-------|------|-------|
| NotificationId + UserId | composite PK | a row means "this Manager has read it" |
| ReadAt | DateTime | |

Unread for a Manager means no `NotificationRead` row for that Manager. A Manager created later therefore sees every older notification as unread (AC-23.4) without any extra work.

#### (v1.1) Customer (`Customers`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK, AUTOINCREMENT | never reused (3.1) |
| Name | string(100) | required, trimmed, 1–100 | AC-29.1; shown everywhere as the current name (AC-29.3) |
| NameNormalized | string(100) | required, unique | AC-29.2, compared without case; ordinal sort key of the reports (3.1) |
| Notes | string(500)? | | AC-29.1; blank is stored as null (design 2.6). May hold contact details: never logged (section 6) |
| IsActive | bool | required, default true | AC-29.4 |
| CreatedAt / CreatedById | DateTime / string | required; index on CreatedAt | AC-29.1, AC-29.6 (list newest first) |

There are no "updated" or "deactivated" who-and-when columns: the PRD does not ask for them (design 9, v1.1 default 5). "Has sales orders" (AC-29.5) is `SalesOrders.Any(o => o.CustomerId == id)` on the `CustomerId` index, never a stored flag.

#### (v1.1) SalesOrder (`SalesOrders`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Number | int | PK, AUTOINCREMENT | "sales order #N", its own sequence from 1 (D-10) |
| CustomerId | int | FK → Customers (Restrict), indexed | AC-24.1; the Restrict FK is the database backstop of AC-29.5 and AC-29.8 |
| CustomerReference | string(100)? | | AC-24.1; blank is stored as null |
| Status | SalesOrderStatus (text) | required | Created, Shipped, Cancelled |
| CreatedAt / CreatedById | DateTime / string | required; index on CreatedAt | list order (design 2.5) |
| ShippedAt / ShippedById | DateTime? / string? | | set by the ship (AC-26.1); **ShippedAt is the shipment time of D-13** |
| ShipmentNote | string(500)? | | AC-26.1, AC-26.8; blank is stored as null |
| CancelledAt / CancelledById | DateTime? / string? | | AC-27.1 |
| CancellationReason | string(500)? | | AC-27.1, required when cancelled |
| StatusChangedAt | DateTime | required | latest status time; the list's `statusChangedAt` (design 5.12) |

Indexes: `CreatedAt` (list order); `CustomerId` (customer filter, the delete check); and (`Status`, `ShippedAt`), which serves the reports (Shipped, then a shipment-time range), open demand (Created) and the status filter. The status `history` (design 5.12) is built from the three timestamp pairs, as for production orders (decision #22): Created (note null), then Shipped (note = ShipmentNote) or Cancelled (note = CancellationReason). There is no history table, because each status is reached at most once.

#### (v1.1) SalesOrderLine (`SalesOrderLines`)

| Field | Type | Constraints | Notes |
|-------|------|-------------|-------|
| Id | int | PK | |
| SalesOrderNumber | int | FK → SalesOrders (Restrict); unique with ItemId | AC-24.3: one line per item |
| LineNo | int | required | position in the request body, from 1; "line order" in responses and messages (AC-24.1, AC-26.3) |
| ItemId | int | FK → Items (Restrict), indexed | any item type (decision #144). Makes the item "in use" (AC-28.5) |
| Quantity | decimal(18,3) | > 0, at most 3 places, at most 999,999,999.999 | AC-24.3, decision #80; in the item's unit |

A sales order has 1 to 500 lines (AC-24.3, AC-24.5). The unit is not copied onto the line: an item on any sales order line is in use, so its unit can never change (AC-28.5), and the line always shows the item's unit. The `ItemId` index serves open demand, the item filter of the list and the item in-use check.

### 3.3 Derived values (calculated, never stored)

| Value | Definition | Where calculated |
|-------|-----------|------------------|
| Stock of item in a location | SUM of WarehouseChange (or ProductionStorageChange) over the item's movements, null as 0 (D-2) | `Features/Stock/StockQueries` |
| Committed material (D-3) | LeftoverApplied + WarehouseSend + ComplementarySends of the InProduction order's line for that material | `Domain/Calculations` |
| Pending returns | SUM of Quantity of Pending returns of the item | stock queries |
| Free leftover (D-4) | max(0, Production storage stock − committed − pending returns) | `Domain/Calculations` |
| Estimates on Created orders (D-5, D-7) | allocation of free leftover to Created orders, oldest first (CreatedAt, then Number), per material | `Domain/Calculations/LeftoverAllocator`, fed by `Features/ProductionOrders/WaitingOrderQueue` |
| Complementary send range | round3(R × (1 − t/100)) to round3(R × (1 + t/100)), current t | `Domain/Calculations` |
| Status history | the timestamp/user pairs on the record | each feature's mapping code |
| (v1.1) Open demand (D-12) | SUM of `SalesOrderLines.Quantity` of the item over sales orders with status Created; 0 when none | `Features/Stock/StockQueries`, inside the same snapshot as the other stock figures (3.5) |
| (v1.1) Shipped quantity and sales order count (D-13) | SUM of line quantities and COUNT of lines, per item (or per customer and item), over sales orders with status Shipped and `ShippedAt` in the day range | `Features/Reports/ShippedTotalsService` (3.5) |
| (v1.1) Customer has sales orders (AC-29.5) | any `SalesOrders` row with that `CustomerId`, in any status | `Features/Customers/CustomerService` |

The pure calculation functions in `Domain/Calculations` (T-004) are the only implementation of D-3 to D-9. Services load the numbers, call them, and store or return the results.

- **One queue for estimates and sends (decision #97).** The waiting orders that share free leftover (Created orders older than or equal to the one in question, with their lines, oldest first) are loaded by one method, `WaitingOrderQueue.AllocateAsync`, used by both the order detail's estimate (`ProductionOrderService`) and the send (`SendOrderService`). So an estimate can never disagree with what the send then applies.
- **One snapshot per combined read (decisions #85, #92).** A figure built from several queries (Warehouse and Production storage stock, committed, pending returns, free leftover, and for the estimate the waiting orders too) must see one state of the database. `StockQueries.GetFiguresAsync` runs its queries inside one read transaction when no transaction is open, or inside the caller's write transaction when one is. `StockQueries.InReadTransactionAsync` is public so a read that combines stock figures with other tables takes them all from one snapshot; `ProductionOrderService.ReadDetailAsync` (the one builder of the order detail, used by T-009, T-010, T-011 and T-016) runs inside it, or inside the caller's write transaction (it throws `InvalidOperationException` if neither is open, or if the DbContext holds unsaved changes, because it reads the database, not the tracked objects). `InReadTransactionAsync` opens a *deferred* SQLite transaction (`BEGIN`), which takes only a read snapshot in WAL mode and never queues behind writers; it must not be called inside a write transaction (section 5.2).

### 3.4 (v1.1) Migration plan

**One migration, `SalesOrders`, created by T-022** (decision #154). It holds the whole v1.1 schema: the three new tables with their indexes, and `Movements.SalesOrderNumber` with its foreign key and the filtered unique index. It changes no existing row.

1. **Generate it**: `dotnet tool run dotnet-ef migrations add SalesOrders --project src/InventoryTracker.Api --output-dir Data/Migrations`.
2. **Hand-edit `Up` before the first commit.** This is allowed: the rule is "never edit a migration once it is committed". EF Core's SQLite provider cannot add a foreign key to an existing table. For the generated `AddColumn` plus `AddForeignKey` on `Movements` it therefore **rebuilds the whole `Movements` table**: it creates `ef_temp_Movements`, copies every row, drops the old table and renames the new one, then recreates the eight indexes. The prototype's `migrations script` output shows the problem: that work is spread over **three separate transactions**, with `PRAGMA foreign_keys = 0/1` between them (a pragma that has no effect inside a transaction), and the history row is written in the last one. If the process stopped part-way on the owner's database, `Movements` could be left without its indexes, or the migration left unrecorded. The next start would then try the migration again and fail on a column that already exists. That is a half-migrated database holding the owner's whole stock history. SQLite can instead add a nullable column with a `REFERENCES` clause in place. So T-022:
   - deletes the generated `migrationBuilder.AddColumn<int>(name: "SalesOrderNumber", table: "Movements", ...)` call (it comes first in `Up`) and the generated `migrationBuilder.AddForeignKey(name: "FK_Movements_SalesOrders_SalesOrderNumber", ...)` call (it comes last);
   - adds, **after** the `CreateTable` of `SalesOrders` and **before** the `CreateIndex` of `IX_Movements_OneShipmentPerLine`, exactly:

     ```csharp
     // Why not EF Core's own AddColumn + AddForeignKey: on SQLite they rebuild the whole Movements table over three
     // transactions (architecture 3.4). SQLite can add a nullable column with a REFERENCES clause in place, in this
     // migration's single transaction.
     migrationBuilder.Sql("ALTER TABLE \"Movements\" ADD \"SalesOrderNumber\" INTEGER NULL CONSTRAINT \"FK_Movements_SalesOrders_SalesOrderNumber\" REFERENCES \"SalesOrders\" (\"Number\") ON DELETE RESTRICT;");
     ```
   - leaves `Down`, the `.Designer.cs` file and the model snapshot exactly as generated. (`Down` rebuilds the table; it is for a developer's own database only and is never run on the owner's.)
3. **Check the script**: `dotnet tool run dotnet-ef migrations script PercentagePrecision --project src/InventoryTracker.Api` must show one `BEGIN TRANSACTION`, the `ALTER TABLE "Movements" ADD "SalesOrderNumber" ...` line, the `__EFMigrationsHistory` insert, and one `COMMIT`. It must show no `ef_temp_Movements` and no `PRAGMA`. The task report includes this output.

**Proved by the prototype (2026-11-24)**, on a database built at `PercentagePrecision` with v1 rows (users, roles, 500 items, 100,000 movements), then migrated with the edited migration. The migration took 180 ms. Movement count and `sqlite_sequence` were unchanged. `PRAGMA foreign_key_check` was empty, and `PRAGMA foreign_key_list(Movements)` lists the new key. An insert naming sales order 999 fails with `FOREIGN KEY constraint failed`. All seven old `Movements` indexes plus the new one are present, nothing is pending, and `HasPendingModelChanges()` is false. T-022's `UpgradeTests` repeats this permanently (section 7).

**The Seller role is not migration data** (decision #155). At every start, `StartupChecks.SeedRolesAndFirstManagerAsync` already creates any role in `Roles.All` that does not exist yet, inside the start-up write transaction (section 6). It runs after the migrations, and through `RoleManager`, which sets the normalized name and concurrency stamp. T-023 adds `Seller` to `Roles.All`, so the first v1.1 start of a v1 database creates the Seller role and nothing else. The first-Manager step does nothing when users exist (AC-02.2), so every user keeps exactly their roles and nobody holds Seller (AC-30.3). On an empty database the first user is still a Manager only (AC-02.1). A role seeded in a migration would need a hard-coded id and concurrency stamp, and would be a second way of creating roles.

**The owner's database** (`%LOCALAPPDATA%\InventoryTracker\inventory.db`, decision #128):

- No agent, test or script in the build phase opens it. Tests always use their own temporary files (`TestApiFactory`), and `UpgradeTests` builds its own v1 database from the v1 migrations.
- The API applies pending migrations at start-up (section 6, step 3), so the first start of v1.1 through `scripts\run.ps1` upgrades it. Before that first start, the owner backs it up with `scripts\backup-database.ps1` (`docs/07-release.md` section 7). The release phase must put that step in the v1.1 release notes and the release guide (section 9, risk 13).
- If the migration fails, its single transaction rolls back and the database stays exactly as v1 left it. The app stops with the error in the log, and the owner can restore or run v1 again.

### 3.5 (v1.1) Query design: open demand, the reports, the sales order list

Nothing is stored for any of these: each is calculated from the sales order tables when asked, as stock is from movements (decision #20). There is no total to drift.

**Open demand (D-12, US-31)** (decision #159). A fourth query in `StockQueries.ReadFiguresAsync`, filtered by the same item ids as the other three:

```csharp
db.SalesOrderLines
    .Where(line => line.SalesOrder.Status == SalesOrderStatus.Created && itemIds.Contains(line.ItemId))
    .GroupBy(line => line.ItemId)
    .Select(group => new { ItemId = group.Key, Quantity = group.Sum(line => line.Quantity) })
```

`StockFigures` gains `OpenDemand` (0 when the item has no Created line). Because it runs inside `ReadFiguresAsync`, it shares the one snapshot of decision #85. `GET /stock` and `GET /stock/{itemId}` show it for every item type, never null (design 5.5). No other figure uses it: sends, the ship, estimates and free leftover never read it (AC-31.3). Measured in the prototype for a page of 100 items over 27,500 lines: 11 ms.

**Shipped lines, the shared base of both reports (D-13)** (decision #160). `ShippedTotalsService` builds one filtered query and both reports group it:

```csharp
var lines = db.SalesOrderLines.Where(line => line.SalesOrder.Status == SalesOrderStatus.Shipped);
// from / to: whole UTC days on ShippedAt, both included, either optional (design 2.5), through Common/Validation/DayRange:
//   ShippedAt >= DayRange.Start(from)   and   ShippedAt < DayRange.EndExclusive(to)   (EndExclusive is null for 9999-12-31: no upper bound)
// customerId: line.SalesOrder.CustomerId == id;  itemType (by item only): line.Item.Type == type;  itemId (by customer only)
```

- **By item** (`GET /reports/shipped-by-item`): `lines.GroupBy(l => l.ItemId)` selecting `Sum(l => l.Quantity)` and `Count()`, then **joined** to `Items` (and `Units` for the name) and ordered by `Items.NameNormalized`, then `Items.Id`. Grouping first and joining after keeps the grouped set small, and the order key is the stored normalized name, so the sort is ordinal (design 2.5).
- **By customer** (`GET /reports/shipped-by-customer`): `lines.GroupBy(l => new { l.SalesOrder.CustomerId, l.ItemId })`, joined to `Customers` and `Items`, ordered by `Customers.NameNormalized`, `Customers.Id`, `Items.NameNormalized`, `Items.Id`.
- `salesOrderCount` is the `Count()` of lines in the group. A sales order has an item at most once (unique index), so it counts sales orders.
- `shippedQuantity` is above zero in every row, because a row exists only when at least one line shipped and every line quantity is above zero. No `HAVING` clause is needed.
- Paging is the usual `ToPagedResultAsync` on the joined query (one `COUNT` and one page). EF Core 10 translates all of this on SQLite, with the decimal sum through its `ef_sum` function. The prototype's generated SQL is a grouped subquery, inner joins, `ORDER BY` on the normalized names, then `LIMIT`/`OFFSET`.
- **Why it equals the movements (AC-32.4)**: the ship writes one `ShippedToCustomer` movement per line with the line's quantity, and `OccurredAt` equal to `ShippedAt` (section 5.3). So "lines of sales orders shipped in the period" and "ShippedToCustomer movements in the period" are the same set, and a day filter on either agrees to the second.
- **Measured in the prototype** (500 items, 500 customers, 5,000 sales orders with 1 to 10 lines, 27,500 lines, 80% Shipped over eleven months, plus 100,000 movements), median of 10 runs, count plus page: by item, all time 69 ms (any page), one month 8 ms, one customer 1 ms. By customer, all time 87 ms (first or deep page, 2,600 rows), one month 11 ms. The target is 1 s at the 95th percentile (PRD section 5); T-031 proves it through HTTP.

**Sales order list (AC-25.1)**: filters `status` (`Status ==`), `customerId` (`CustomerId ==`) and `itemId` (`Lines.Any(l => l.ItemId == id)`, an `EXISTS` on the `ItemId` index); ordered by `CreatedAt` descending, then `Number` descending (design 2.5). `lineCount` is `Lines.Count()` and `customer` is joined from `Customers`, so the list always shows the current name. Prototype: 4 ms with the item filter.

**The sales order detail** is built in one place, `SalesOrderService.ReadDetailAsync(number)` (decision #92's reasoning), and used by `GET /sales-orders/{number}`, the create, the ship and the cancel. It is a split query (`AsSplitQuery`: the order with its customer and users, then its lines with item and unit through `IX_SalesOrderLines_SalesOrderNumber_ItemId`), because the single-query form made SQLite scan every sales order line (T-031 review, decision #169). Two statements must read one snapshot, so `ReadDetailAsync` requires a transaction: `GET /sales-orders/{number}` runs it in `StockQueries.InReadTransactionAsync` (#85). The changing actions call it after `SaveChangesAsync`, still inside their write transaction, so the response is exactly what is committed.

## 4. Interface contract

**The contract is `docs/02-design.md` sections 2 and 4 to 7, adopted as written**: base path `/api/v1`, the 48 endpoints in section 4 ((v1.1) 62: the 48 of version 1 and 14 new ones, with the read roles of decision #147), the request and response shapes in section 5, the ProblemDetails format in section 6, and the messages and codes in section 7. It is not repeated here, so the two cannot drift apart. The points below add to it or make it exact. They are binding in the same way.

### 4.1 Additions and clarifications

1. **Infrastructure endpoints** (outside `/api/v1`): `GET /health` answers `200` with the text `Healthy` (or `503 Unhealthy` when the database cannot be reached). `GET /openapi/v1.json` is mapped in Development only. Both are anonymous. See open point 1 in section 9 and decision #28.
2. **Bodies that cannot be read.** Malformed JSON, or a value of the wrong JSON type (a string where a number is expected, which design 2.4 requires to be rejected), is rejected with `400` `validation` before the action runs. The framework must read the body first, so this comes before the `404` step in the design's check order (design 2.2, decision #23). `detail`: "The request body is not valid JSON." for syntax errors, "{field} has the wrong type." for type errors. A missing body where one is required: `400` `validation` "A request body is required."
   **A body the web server will not deliver** (SEC-02, decision #124) sits in the same place, after `401` and `403` and before the `404`: over 1 MB is `413` `request-too-large` "The request body is too large. The limit is 1 MB."; too slow is `408` `request-timeout` "The request body arrived too slowly. Send it again."; any other broken body (a malformed chunked body, a connection that ends early) is `400` `validation` "The request body could not be read." None of the three has an `errors` field (decision #41). Every endpoint that takes a body can answer them, sign-in included, because Kestrel refuses the body when model binding starts to read it. The line and role caps (item 12) also come before the `404`.
3. **Enum values in bodies and query strings** are received as strings and parsed by `Common/Validation` (case-insensitive, names only, numbers refused), so the error is the design's own message, for example "Type must be RawMaterial or FinishedProduct." Dates in query strings (`from`, `to`) are parsed the same way (`yyyy-MM-dd`).
   **Number-typed query values are the exception** (decisions #44, #113). `page`, `pageSize` and the id and number filters (`itemId`, `orderNumber`, `complementaryOrderNumber`, ...) are bound by MVC before our code runs. A value that is not a whole number in range (`?itemId=abc`, `?page=1.5`) gets `400` `validation` with MVC's own sentence, which names the C# property ("The value 'abc' is not valid for ItemId."; on some filters "The value 'abc' is not valid."), while the `errors` key is the camelCase query name. Accepted as is for v1; tests pin the current wording.
4. **Range limits are compared after rounding.** Send ranges (D-7) and complementary send ranges (AC-18.1) are rounded to 3 places, half away from zero, and a quantity is accepted when `min ≤ quantity ≤ max` using exactly the limits the API shows. Usage-variance thresholds compare the unrounded percentage (AC-20.4).
5. **Sign-in and lock-out** (AC-01.3, AC-01.4). In order: unknown email or inactive user → `401` `invalid-credentials` (no failure counted); account currently locked → `401` `account-locked`, even with the right password; wrong password → failure counted, `401` `invalid-credentials` (the fifth failure in a row also gets this message and starts the 15-minute lock); right password → failure count reset, `200`. The whole sequence runs one attempt at a time per account (section 5.2, decision #61).
6. **Token rules** (AC-01.2, AC-01.5, AC-03.5, AC-03.6). Lifetime exactly 8 hours, with no clock-skew allowance. Every authenticated request checks that the token's user exists, is active, and has the same security stamp as when the token was issued; otherwise `401` `not-authenticated`. Deactivating a user changes their stamp, so their old tokens stay invalid even after reactivation. Changing or resetting a password also changes the stamp (Identity's default), so that user's existing tokens stop working and they sign in again. Changing roles does **not** end existing tokens: the new roles apply from the next sign-in (AC-03.5, PRD row 15).
7. **401 and 403 bodies.** `401` responses carry the design 7.1 ProblemDetails body and the header `WWW-Authenticate: Bearer`. `403` bodies name the allowed roles from the endpoint's `[Authorize(Roles = ...)]`: "the Manager role", "the Warehouse or Manager role".
8. **Other framework errors**: unknown route `404` code `not-found` "No endpoint matches this path."; wrong method `405` code `method-not-allowed` "This method is not allowed on this path."; a body sent with a non-JSON `Content-Type` `415` code `unsupported-media-type` "The request body must be JSON, sent with Content-Type: application/json." (decision #40); a body the web server refuses `413` `request-too-large`, `408` `request-timeout` or `400` `validation` (item 2, decision #124), with the fixed titles "Request too large" and "Request timeout" for the first two; unhandled exception `500` code `internal-error` "An unexpected error occurred." (details only in the log). Whole-body errors (invalid JSON, missing body, wrong root type, a body that could not be read) and `408`/`413` have no `errors` field; field type errors do (decision #41). **Without a token, every path answers `401`, including an unknown one**: the fallback "signed-in" policy (section 6, Authorization) runs before routing can say `404`. With a valid token an unknown path gets the `404` above. This matches design 2.2's check order (authentication first).
9. **Times** in responses are whole seconds, UTC, with `Z` (stored that way, section 3.1).
10. **Stock list order**: by the item's `createdAt` descending, then id descending (design 2.5).
11. **Concurrency fallback** (decision #46): when a UNIQUE or PRIMARY KEY constraint (for example the unique InProduction index) rejects a write that the in-transaction checks did not catch, the response is `409` `concurrent-change` (design 7.2), logged at Warning. With the transaction rule in 5.2 this should never happen; it exists as a backstop. Every other database failure, including NOT NULL, CHECK and FOREIGN KEY constraint failures, is a programming error, not a race: it gives `500` `internal-error` and is logged at Error with the exception.
    **A token ended mid-change gives `401`, not `409`** (decision #70 as amended). Change-password checks the current password before its write transaction (section 5.2). Inside the transaction it reloads the user and proceeds only if the user is still active and the security stamp still equals the token's `stamp` claim. If a Manager's reset, a deactivation (even one followed by a reactivation) or a competing change-password finished in between, the stamp has changed: the request gets `401` `not-authenticated` (design 7.1), the same answer the token check would give on the next request, and nothing is saved. There is no `409` for this race.
12. **Request limits** (SEC-01, decisions #122, #123, #125; constants in `Common/Validation/RequestLimits.cs`). They bound the work and memory of any one request, so that no request can hold SQLite's single write lock (section 5.2) or build a huge response.
    - **Body: at most 1 MB** (`RequestLimits.MaxBodyBytes`, 1,000,000 bytes; Kestrel's own default is 30 MB). `Program.cs` sets `MaxRequestBodySize` for both Kestrel and IIS, so the limit holds however the release phase hosts the API (the IIS setting does nothing when the API is not in IIS). A larger body is refused with `413` before any service runs (item 2). The largest real body, 500 lines, is about 25 to 40 KB.
    - **Lines: at most 500** (`RequestLimits.MaxLines`) on a recipe (`PUT /items/{id}/recipe`), a send, a completion, a complementary request, a complementary send and (v1.1) a new sales order (see the last bullet). More is `400` `too-many-lines` "At most 500 lines are allowed." under `lines` (decision #123).
    - **Roles: at most 10** (`RequestLimits.MaxRoles`) on `POST /users` and `PUT /users/{id}/roles`. More is `400` `validation` "At most 10 roles are allowed." under `roles` (decision #125).
    - Both caps are checked by `RequestLimits.CheckLineCount` and `CheckRoleCount` as the first step of the service, before `InWriteTransactionAsync`, and answered on their own: the response holds only that one message, even when other fields are also wrong. So they come before the `404` for the record in the URL, where there is one (section 5.1). A missing list passes the cap; "required" stays the service's own check.
    - **Messages: a `400` lists at most 100** (`ValidationErrors.MaxMessages`, decision #122). Later messages are only counted, and `detail` ends with "{n} more problems are not shown." ("1 more problem is not shown."), with `{n}` formatted as design 6.1. That sentence is not in `errors`.
    - (v1.1) **`POST /sales-orders` has the line cap too** (AC-24.5): `RequestLimits.CheckLineCount(request.Lines)` is the service's first step, before the write transaction and before every other check, the customer check included (design 2.2).
13. (v1.1) **Role-based reads** (decision #147, design 4 and 7.1; decision #156). The Roles column of design 4 is exact for reads too. How the code says it:
    - **Each action carries its own `[Authorize(Roles = ...)]`.** A controller-level attribute is allowed only when every action of the controller has the same roles (today `UsersController` and `NotificationsController`, Manager only). The reason is that ASP.NET Core *combines* a class attribute and an action attribute with AND: the user must pass both. `[Authorize(Roles = "Warehouse,Production,Manager")]` on `ComplementaryOrdersController` plus `[Authorize(Roles = "Manager")]` on approve would still allow only Managers, but a Seller's `403` would then name the roles of both failed requirements ("Warehouse, Production or Manager"), which is the wrong message, and the role matrix would flag it.
    - **New constants in `Auth/Roles.cs`**, each written in `Role` order (Warehouse, Production, Manager, Seller). `ForbiddenResponseHandler.Message` names the roles in the order the requirement lists them, so the constant's order *is* the order of the `403` sentence (design 7.1):

      | Constant | Value | Used by |
      |----------|-------|---------|
      | `Roles.Seller` | `Seller` | the role itself |
      | `Roles.WarehouseProductionOrManager` | `Warehouse,Production,Manager` | `GET /items/{id}/recipe`, both `GET /movements` endpoints, both production order reads, both complementary order reads, both return reads |
      | `Roles.WarehouseManagerOrSeller` | `Warehouse,Manager,Seller` | both customer reads, both sales order reads, both reports |
      | `Roles.ManagerOrSeller` | `Manager,Seller` | customer create, edit, deactivate, reactivate and delete; sales order create and cancel |
      | `Roles.WarehouseOrManager` (v1) | `Warehouse,Manager` | also the ship |

    - **"All" stays attribute-free**: units, items, stock and change-password keep the fallback "signed-in" policy (AC-30.7, AC-31.4). Every user holds at least one role, so they never answer `403`.
    - `ForbiddenResponseHandler` needs no code change. It already joins three or more roles as "A, B or C" (it is built for any count), and `Distinct()` keeps the order. T-023 adds a unit test for the three-role sentence.
    - A read refused by role comes before every query-value check (`[Authorize]` runs before model binding), so a Production user's `GET /sales-orders?status=Done` is `403`, not `400` (design 2.2).
14. (v1.1) **Seller is an ordinary Identity role** (AC-30.1, decision #155). `Roles.All` becomes `[Warehouse, Production, Manager, Seller]`. That one list drives role parsing on `POST /users` and `PUT /users/{id}/roles` (any letter case), the order of roles in sign-in responses and tokens (decision #56), and the start-up seeding. The unknown-role message lists the four ("Unknown role 'Driver'. Roles are Warehouse, Production, Manager and Seller.", design 7.2). The last-Manager rule counts Manager only (AC-30.5).
15. (v1.1) **Request records** (design 5.11, 5.12; the `= null` rule of decision #110 for optional fields, which must also come last in a C# record):
    - `CustomerRequest(string? Name, string? Notes = null)`, for both `POST` and `PUT /customers/{id}`. `PUT` is a full replacement, so an absent or null `Notes` clears the notes (design 9, v1.1 default 6).
    - `CreateSalesOrderRequest(int? CustomerId, IReadOnlyList<SalesOrderLineRequest?>? Lines, string? CustomerReference = null)`, with `SalesOrderLineRequest(int? ItemId, decimal? Quantity)`.
    - `ShipSalesOrderRequest(string? Note = null)`, bound with `[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]`, so no body, `{}` and a null or blank note all mean no note. This is the complementary reject's pattern (`ComplementaryOrdersController.Reject`).
    - `CancelSalesOrderRequest(string? Reason)`.
    - Deactivate, reactivate and delete take no body; one that is sent is ignored, as for approve.
16. (v1.1) **Sales order line checks** (AC-24.3, design 7.2 "Sales orders"). The lines are new, not matched to an existing order, so `OrderLineMatcher` does not apply. `SalesOrderService` checks each line in body order, with one message per field (decision #75):
    - a line sent as `null` gets "Item is required." and "Quantity is required.";
    - `itemId`: required, then exists ("Item {itemId} does not exist."). The item names are loaded in **one** query for all ids (`WHERE Id IN (...)`), never one query per line;
    - every later line naming an item already seen gets "{item} appears on more than one line." under `lines[i].itemId`;
    - `quantity`: required, greater than 0, at most 3 decimal places ("Quantity can have at most 3 decimal places.", decision #95), then `Quantity.IsWithinMax` with `Quantity.AboveMaxMessage("Quantity")`.

    There is no item-type rule (decision #144). Customer and reference come first in the same collector: "Customer is required." / "Customer {customerId} does not exist." under `customerId`, and "Customer reference must be 100 characters or fewer." under `customerReference`. `lines` missing, null or empty gets "At least one line is required." under `lines`. After all field checks, an inactive customer is `409` `customer-inactive`.
17. (v1.1) **Customers inside records** are `CustomerSummary(int Id, string Name, bool IsActive)` (`Common/Contracts`), read by a join at request time, never copied onto the sales order (AC-29.3, AC-33.4).
18. (v1.1) **`405` on `PUT` and `DELETE /sales-orders/{number}`** (design 4) needs no code. The path exists for `GET`, so routing answers `405 method-not-allowed` (item 8). A test pins it.
19. (v1.1) **Query values of the new lists and reports**: `status` through `EnumParser` ("status must be Created, Shipped or Cancelled."), `itemType` through `EnumParser`, `from` and `to` through `DateParser` and `DateParser.CheckRange`, `isActive` through the shared `BoolParser` ("isActive must be true or false.", the rules of `unreadOnly`, decision #102a). The id filters (`customerId`, `itemId`, `salesOrderNumber`) are `int?` bound by MVC, with the accepted framework wording (item 3, decision #113). Every problem is reported in one `400`.
20. (v1.1) **The movement response gains `salesOrderNumber`** (design 5.6), between `returnNumber` and `receiptDate`, set only on `ShippedToCustomer` movements. It is filled by the one mapping, `MovementMapping.AsResponse`, so receipts, adjustments and the movement list all show it. `GET /movements` gains the filter `salesOrderNumber` (`Movement.SalesOrderNumber ==`). `orderNumber` keeps comparing `ProductionOrderNumber`, which is always null on a shipment, so it never matches one (AC-28.2). The `type` filter's message lists seven values.

### 4.2 Endpoint ownership

| Endpoints (design section 4) | Task |
|------------------------------|------|
| `POST /auth/sign-in` | T-003 |
| `POST /auth/change-password`, all `/users` | T-014 |
| `/units`, `/items` (not recipe) | T-006 |
| `/items/{id}/recipe` | T-007 |
| `POST /receipts`, `GET /stock`, `GET /stock/{itemId}` | T-008, including the committed, pending-return and free-leftover fields (AC-10.3); T-019 made Pending returns creatable through the API, and its tests check those fields end to end |
| `GET /movements`, `GET /movements/{id}` | T-013 |
| `POST /adjustments` | T-015 |
| `POST /production-orders`, `GET /production-orders`, `GET /production-orders/{number}` | T-009 |
| `POST /production-orders/{number}/send` | T-010 |
| `POST /production-orders/{number}/complete` | T-011 |
| `POST /production-orders/{number}/cancel` | T-016 |
| `POST /production-orders/{number}/complementary-orders`, `GET /complementary-orders`, `GET /complementary-orders/{number}`, approve, reject | T-017 |
| `POST /complementary-orders/{number}/send` | T-018 |
| `/returns` (all) | T-019 |
| `/notifications` (all) | T-012 |
| (v1.1) Read roles of the v1 reads (recipe, movements, production orders, complementary orders, returns) | T-023 |
| (v1.1) `/customers` (all seven) | T-024 |
| (v1.1) `POST /sales-orders`, `GET /sales-orders`, `GET /sales-orders/{number}` | T-025 |
| (v1.1) `salesOrderNumber` on movements, the `salesOrderNumber` filter, `ShippedToCustomer` | T-026 |
| (v1.1) `POST /sales-orders/{number}/ship` | T-027 |
| (v1.1) `POST /sales-orders/{number}/cancel` | T-028 |
| (v1.1) `openDemand` on `GET /stock` and `GET /stock/{itemId}` | T-029 |
| (v1.1) `GET /reports/shipped-by-item`, `GET /reports/shipped-by-customer` | T-030 |

## 5. Business rules and consistency

### 5.1 Where the rules live

- **Stock** is always the sum of movements (D-2). Nothing stores a running balance, so stock cannot drift from history, and AC-11.4 holds by construction. Measured on this machine: grouped SUM over 100,000 movements of 500 items takes about 140 ms, one item under 10 ms. Stock queries must always filter by the item ids they need (a page of `GET /stock`, or the lines of one order). They must never sum the whole table per request inside a loop.
- **Stock code is injected, scoped (decision #77).** `Features/Stock/StockLedger` and `Features/Stock/StockQueries` are registered by `FeatureRegistration` (classes ending in `Ledger` or `Queries`) with a scoped lifetime: one instance per HTTP request, built with that request's `AppDbContext`. Services ask for them in their constructor (`public sealed class SendOrderService(AppDbContext db, StockLedger ledger, StockQueries stock, Clock clock)`) and never create them with `new`. Scoped matters: the ledger must read and write through the same DbContext, and so the same transaction, as the service that called it.
- **Every movement goes through `StockLedger.AddAsync(movement, cancellationToken)`** (T-008). Nothing else adds a `Movement`: no code under `src/` other than `StockLedger` puts a `Movement` on the DbContext (`db.Movements.Add`, `db.Add`, `AddRange`) or inserts into `Movements` with SQL, and `code-reviewer` checks this by search on every change that touches movements (decision #112). Its contract, as built, in the order the checks run:
  1. **Inside a transaction.** It must be called inside `InWriteTransactionAsync`. It can only see that *a* transaction is open on the DbContext, and throws `InvalidOperationException` when none is; services open theirs only through `InWriteTransactionAsync`.
  2. **No stray movements (decision #86).** Its own `SaveChangesAsync` saves everything the DbContext tracks, so it throws `InvalidOperationException` if the change tracker holds any Added `Movement` it did not add itself. A movement put on the DbContext directly can therefore never ride along, unchecked, on a ledger save (the T-008 review found a direct −100 next to a ledger +1 taking the Warehouse to −99). Earlier ledger movements in the same request are already saved, so they do not trip it. This is not a full "only writer" guard at `SaveChanges` level: a direct add saved without any ledger call is stopped by the review rule above, not by code (decision #112).
  3. **Shape (D-2, AC-05.3, AC-11.3).** `Id` is 0 (added once only); change columns that match the type (table in 3.2 Movement); no zero change; at most 3 decimal places; within the `decimal(18,3)` capacity (`StockLedger.MaxQuantity`, section 3.1); a `UserId`; `OccurredAt` in UTC whole seconds from `Clock.UtcNow`.
  4. **Link and text fields (decision #87).** `ReceiptDate` required on Received and only there; `Note` only on Received; `Reason` required on Adjustment and only there; `ReturnNumber` required on ReturnedToWarehouse and only there; `ProductionOrderNumber` required on SentToProduction, ConsumedInProduction and ProducedOutput, optional on ReturnedToWarehouse, not allowed on Received or Adjustment; `ComplementaryOrderNumber` only on SentToProduction. `Note` and `Reason` must be trimmed, not blank, and at most 500 characters.
  5. **Complementary parent, from the database (decision #106e).** When `ComplementaryOrderNumber` is set, one primary-key read checks that the complementary order exists and that its `ParentOrderNumber` equals the movement's `ProductionOrderNumber`. A mismatch would misreport an order's material in `GET /movements?orderNumber=` for ever, because movements cannot be corrected.

  (v1.1) **ShippedToCustomer in the contract** (decision #157, T-026). In step 3, the shape rule for `ShippedToCustomer` is `WarehouseChange < 0` and `ProductionStorageChange` null (D-2, D-11). In step 4, `SalesOrderNumber` is required on `ShippedToCustomer` and allowed nowhere else. The existing rules already refuse `ProductionOrderNumber`, `ComplementaryOrderNumber`, `ReturnNumber`, `ReceiptDate`, `Note` and `Reason` on it, because none lists it as allowed (AC-28.1). Whether the sales order exists is left to the foreign key: an unknown number fails the insert with `FOREIGN KEY constraint failed`, a `500` like any other programming error (decision #46). A second movement for the same sales order and item fails `IX_Movements_OneShipmentPerLine`. That index is unique, so through the API this answers `409` `concurrent-change` (section 4.1 item 11); it exists only as a backstop. Both are refused by the database, so the ledger needs no extra read. The review rule of decision #112 now covers `ShipSalesOrderService` too: it adds its movements through `StockLedger.AddAsync` only.

  A failure of 3, 4 or 5 is a programming error, not a client error: `ArgumentException`, answered `500` `internal-error` and logged at Error. Then, for each location the movement takes stock from, it sums that item's current stock in that location through `StockQueries.GetStockAsync` and refuses the movement with `409` `insufficient-stock` "Not enough stock: {item} in {location} has {available} {unit}." if the result would be below zero. Reaching exactly zero is allowed (AC-12.3), and increases are not summed. Finally it adds the movement and calls `SaveChangesAsync` at once, still inside the transaction, so `movement.Id` is set and the next movement for the same item in the same request is included in the next sum. It does not check business rules (raw materials only, order status, ranges, input maximums): those belong to the calling service. Callers that must name every short line in one message (AC-15.7, AC-18.4), or need other wording (receiving a return), check all lines first with `StockQueries` and the public `StockLedger.InsufficientStockMessage` and `LocationName` helpers, throw their own `409`, and only then add the movements. The ledger checks again, so it is the last line of defence.
- **Calculations** (D-3 to D-9) are pure functions in `Domain/Calculations` (T-004), unit-tested against every worked example in the PRD.
- **Check order** follows design 2.2. Role checks (`401`/`403`) happen first, in the framework, because of `[Authorize]`. Next come the bodies that cannot be read, also in the framework, while model binding reads the body: `413`, `408` or `400` from the web server, then `400` for invalid JSON or a wrong JSON type (section 4.1 item 2). Then, in the service:
  1. the line cap or the role cap, where the body has one (`400` `too-many-lines` or `400` `validation`, section 4.1 item 12), answered on its own and **before** the write transaction opens, so an oversized list never takes the write lock (decisions #123, #125);
  2. open the write transaction, load the record named in the URL (`404`), validate the input shape (`400`), check state (`409`), check ranges (`400`), check stock (`409`), then write.

  Only the two caps move before the transaction. Every other body check keeps its design 2.2 place, inside the transaction after the `404` (the password operations in section 5.2 are the existing exception); with at most 500 lines or 10 roles those checks take well under a millisecond.

- (v1.1) **Check order of the new actions** (design 2.2, "Check order on the new actions"), as service steps. `[Authorize]` (`401`, `403`) and the unreadable-body answers (`413`, `408`, `400`) always come first, in the framework.

  | Action | Before the transaction | Inside one `InWriteTransactionAsync`, in this order |
  |--------|------------------------|------------------------------------------------------|
  | `POST /sales-orders` | `RequestLimits.CheckLineCount` (`400` `too-many-lines`, on its own) | field checks, all in one `400` (customer required and exists, reference, lines, items, quantities, duplicates; 4.1 item 16) → `409` `customer-inactive` → insert the order (status Created, `StatusChangedAt = CreatedAt`) and its lines (`LineNo` 1, 2, ...) → `SaveChangesAsync` → `ReadDetailAsync`. No stock read at all (AC-24.2). |
  | `POST /sales-orders/{number}/ship` | none | `404` → note longer than 500 (`400`) → status (`409` `already-shipped` / `sales-order-cancelled`) → stock check of every line (`409` `insufficient-stock`, every short line) → one movement per line through the ledger → status Shipped (section 5.3). |
  | `POST /sales-orders/{number}/cancel` | none | `404` → reason missing, blank or over 500 (`400`) → status (`409`) → status Cancelled, reason, who and when. No movement. |
  | `POST /customers` | none | name and notes (`400`) → `409` `customer-name-taken` → insert. |
  | `PUT /customers/{id}` | none | `404` → name and notes (`400`) → `409` `customer-name-taken` (any *other* customer with the same `NameNormalized`; its own name in another case is allowed) → update. |
  | `POST /customers/{id}/deactivate`, `/reactivate` | none | `404` → set `IsActive` (a no-op when it already has that value, AC-29.4). |
  | `DELETE /customers/{id}` | none | `404` → `409` `customer-has-sales-orders` (any sales order, any status) → delete. |
  | `GET` lists and reports | read-only | query values (`400`), then the query. No transaction: each is one statement, apart from the stock figures (section 3.3). (A detail is not a list: the production order and sales order details run in a read transaction, sections 3.5 and 5.2.) |

  Codes used only by sales orders (`already-shipped`, `sales-order-cancelled`, `customer-inactive`) are constants in `Features/SalesOrders`. Codes used only by customers (`customer-name-taken`, `customer-has-sales-orders`) are constants in `Features/Customers`. The ship's `insufficient-stock` is `StockLedger.InsufficientStockCode`. All follow the rule of section 6, Error handling.

### 5.2 Concurrency (PRD section 5)

**Rule: every request that changes data does all its reads, checks and writes inside one call to `db.InWriteTransactionAsync(...)`** (`Data/WriteTransaction.cs`, T-002). The helper begins a transaction, runs the work, calls `SaveChangesAsync`, and commits. Any exception rolls everything back, so a request either completes fully or saves nothing. Three deliberate exceptions are described below: sign-in's lock-out counter (decision #61), password hashing (decision #70), and the line and role caps (decisions #123, #125). (A request that reads several things and changes nothing uses the read helper described near the end of this section.)

Why this is safe on SQLite: EF Core's `BeginTransactionAsync` on SQLite issues `BEGIN IMMEDIATE`. This was checked on this machine: a second connection could not begin a transaction while the first was open. `BEGIN IMMEDIATE` takes the database's single write lock at the start, so write transactions run strictly one at a time, across all connections and processes. That is serializable isolation. A waiting request blocks for up to the connection's busy timeout (30 seconds, the Microsoft.Data.Sqlite default) and then continues with fresh data. WAL mode (set at start-up) lets read-only requests run while a write is in progress.

What this guarantees, rule by rule:

| Rule | How it holds under concurrent requests |
|------|----------------------------------------|
| Stock never below zero in either location (D-2, AC-12.3, AC-15.7) | The ledger's check and insert are in the same serialized transaction, so two movements can never both see the old stock. |
| Only one order InProduction (AC-15.10) | The send checks for an InProduction order inside the transaction. Backstop: the unique filtered index on `Status = 'InProduction'`. |
| One send per order (AC-15.9) | Status `Created` is checked and changed to `InProduction` in the same transaction. The second of two simultaneous sends sees `InProduction` and gets `already-sent`. |
| Leftover counted once (D-5, D-6) | Allocation is calculated inside the send transaction from current stock, Created orders and Pending returns, then fixed on the line. Afterwards the material is committed to the InProduction order, so it is excluded from free leftover (D-4). Returns and production-storage adjustments of that material are refused while the order runs (AC-12.5, AC-22.3). |
| Output registered once (AC-19.5) | Status `InProduction` is checked and changed to `Completed` in the same transaction. |
| Complementary send once (AC-18.3), return received once (AC-22.5) | The same status check-and-change inside the transaction. |
| Notifications match events | Notifications are added in the same transaction as the event that raises them. |
| (v1.1) One shipment per sales order (AC-26.10, first bullet) | Status `Created` is checked and changed to `Shipped` in the same transaction as the movements. The second of two simultaneous shipments sees `Shipped` and gets `already-shipped`, with no movement. Backstop: `IX_Movements_OneShipmentPerLine`. |
| (v1.1) Two shipments competing for the same stock (second bullet) | Each ship checks every line against Warehouse stock inside its transaction, then adds the movements. The second sees the stock the first left and gets `insufficient-stock` naming its short lines; the ledger checks again. |
| (v1.1) Ship and cancel of the same sales order (third bullet) | Both check and change the same status inside a transaction, one after the other. The loser gets `already-shipped` (a cancel that lost) or `sales-order-cancelled` (a ship that lost, with no movement). |
| (v1.1) Ship against an adjustment, a production send or a complementary send (fourth and fifth bullets) | All four write through the ledger in serialized transactions, so the Warehouse never goes below zero. Each loser gets its own `insufficient-stock` message. |
| (v1.1) Customer delete and a new sales order for it (AC-29.8) | The delete checks "has sales orders" and deletes in one transaction; the create checks "exists and is active" and inserts in another. They run one after the other: either the delete wins and the create gets `400` "Customer {id} does not exist.", or the create wins and the delete gets `409` `customer-has-sales-orders`. Backstop: the `Restrict` foreign key (a `500` if it were ever reached, decision #46). |
| (v1.1) Two customers created or renamed to the same name | The name check and the write are in one transaction. The loser gets `409` `customer-name-taken`; the unique `NameNormalized` index is the backstop (`409` `concurrent-change`). |
| (v1.1) Deactivation and a new sales order for the same customer | Serialized: the sales order is either created first (and stays, AC-29.4) or refused with `customer-inactive`. |

Losers of a race get the specific message (`already-sent`, `insufficient-stock`, `another-order-in-production`; (v1.1) `already-shipped`, `sales-order-cancelled`, `customer-has-sales-orders`, `customer-name-taken`, or the `400` unknown-customer message). `concurrent-change` is only the fallback (section 4.1 item 11).

**Sign-in and the lock-out counter (decision #61).** Sign-in does not use `InWriteTransactionAsync`: it changes only Identity's own columns, through `UserManager`. Identity's failure counting is **read-modify-write**: `AccessFailedAsync` reads the user row, adds one to `AccessFailedCount` in memory, and saves the row with a concurrency-stamp check. When several wrong passwords for one account arrive at the same moment, they all read the same count, one save wins and the others fail the stamp check, so failures are lost and simultaneous attempts can get past the 5-failure lock-out (AC-01.4). The rules:

- **One sign-in at a time per account.** `Auth/AccountLocks` holds an in-process async lock (`SemaphoreSlim`) keyed on the normalized email. `SignInService` takes it before step 1 of section 4.1 item 5 (find the user) and releases it after the last step (record the failure, or reset the count and issue the token), so every attempt reads the count the previous one wrote. An entry exists only while a request holds or waits for it, so attempts on unknown emails leave nothing in memory.
- **No global lock.** Attempts on different accounts run side by side. A flood of attempts on one account delays only that account.
- **Equal cost on every refusal (decision #62 as corrected).** Every refusal path costs one password-hash check: a wrong password is a real check; an unknown email, an inactive user and a locked account are each checked against a fixed dummy hash. So every refusal takes about the same time (about 45 ms measured). The outcome is decided inside the account's lock, and the dummy hash runs **after** the lock is released, so a flood on one account does not queue behind slow hashing. The reason is simplicity: one cost for every refusal is easy to reason about and to test. For unknown and inactive users it also means timing does not reveal which emails have accounts; for a locked account it hides nothing, because the distinct `account-locked` message already shows that the account exists.
- **Change-password failures count too (decision #71, owner).** A wrong current password on `POST /auth/change-password` counts towards the same lock-out as sign-in: 5 failures in a row, from either endpoint, lock the account for 15 minutes (AC-01.4). The counting runs inside the same per-account `AccountLocks` entry as sign-in, so the two endpoints cannot lose each other's counts. While locked, change-password is refused with the same `401` `account-locked` problem as sign-in, even with the right current password, and nothing is counted. A correct current password resets the count. Without this, a stolen token would allow unlimited guessing of the real password for 8 hours.
- **Single process only.** The lock lives in the API process. This is correct because the app runs as one process (SQLite, section 1). Running several instances would need the serialisation moved into the database (for example a row lock or advisory lock per account on PostgreSQL); see section 9, risk 9.

**Password hashing runs outside the write transaction (decision #70 as amended).** Checking or hashing a password takes about 45 ms on purpose. Inside `InWriteTransactionAsync` it would hold SQLite's single write lock for that long, so any signed-in user sending change-password requests in a loop could stall every write in the app (the T-014 review measured writes slowing from 7 ms to about 1 s). So, like sign-in, these three operations are an exception to "everything in one transaction": the slow work comes first, outside, and the transaction only re-confirms and saves, in a few milliseconds.

| Operation | Outside the transaction | Re-confirmed inside the transaction |
|-----------|------------------------|-------------------------------------|
| Change own password (`POST /auth/change-password`) | Inside the account's `AccountLocks` entry: reload the user, check active and stamp, check the lock-out, verify the current password, count the failure or reset the count (#71). Then check the new password's policy and hash it. | Reload the user. Proceed only if the user is active and the security stamp still equals the token's `stamp` claim; otherwise `401` `not-authenticated`, nothing saved (section 4.1 item 11). Save the hash with a new stamp. |
| Manager reset (`PUT /users/{id}/password`) | `404` check on a no-tracking read, policy check, hash. | Reload the user; save the hash with a new stamp, clear `LockoutEnd` and set `AccessFailedCount` to 0 in the same write (#72). Nothing depends on the old password, so there is nothing else to re-check: a later reset simply replaces an earlier change. |
| Create user (`POST /users`) | The role cap first, answered on its own (decision #125), then field checks, policy check, hash. | The duplicate-email check (`409` `email-taken`) and the save. The unique `UserNameIndex` is the backstop (section 4.1 item 11, decision #64). |

A new password check added later follows the same pattern: hash outside, re-confirm inside, never hash inside `InWriteTransactionAsync`.

**The line and role caps run before the write transaction (SEC-01, decisions #123, #125).** Before the fix, a body with millions of lines, or 1 MB of `null` roles, was checked entry by entry inside the transaction, and every other user's save waited behind it (seconds for lines, up to 1.3 s for roles). `RequestLimits.CheckLineCount` and `CheckRoleCount` read only the list's count, need nothing from the database, and run as the service's first step, so an oversized list is refused without ever taking the write lock. Nothing is read or written outside the transaction, so the rule above still holds for everything the request does to data. A new endpoint whose body carries a list follows the same pattern (section 4.1 item 12).

**Read-only requests that combine several queries** (stock figures, the order detail's estimate) take them from one snapshot through `StockQueries.InReadTransactionAsync` (section 3.3, decision #85). It is the one other transaction helper. It opens a deferred SQLite transaction directly on the connection, because EF Core's `BeginTransactionAsync` would issue `BEGIN IMMEDIATE` and make every read queue behind every write.

**PostgreSQL later:** change the two transaction helpers and nothing else in the product code. In `InWriteTransactionAsync`, begin the transaction and immediately run `SELECT pg_advisory_xact_lock(1)`, which gives the same one-writer-at-a-time behaviour without retry logic. In `StockQueries.InReadTransactionAsync`, which uses `SqliteConnection` directly, replace the body with `BeginTransactionAsync(IsolationLevel.RepeatableRead)`, which gives the same one-snapshot reads. Remove the WAL pragma. Everything else is portable.

### 5.3 (v1.1) The shipment, step by step

`ShipSalesOrderService.ShipAsync(number, request, userId)` (decision #158). It holds `AppDbContext`, `StockLedger`, `StockQueries`, `Clock`, `SalesOrderService` (for `ReadDetailAsync`, step 7) and an `ILogger` (step 8) from its constructor (decision #77), the same pattern as `SendOrderService`. Everything below runs in one `db.InWriteTransactionAsync`, so a shipment either completes fully or saves nothing (AC-26.3, PRD section 5):

1. Load the sales order with its lines, in `LineNo` order, and each line's item name and unit. Missing: `404` "Sales order #{number} not found.".
2. Clean the note (`Text.Clean`; blank becomes null, AC-26.8). More than 500 code points: `400` "Note must be 500 characters or fewer." under `note`.
3. Status: `Shipped` → `409` `already-shipped` "This sales order has already been shipped."; `Cancelled` → `409` `sales-order-cancelled` "This sales order is cancelled.". The customer's active status is **not** checked (AC-26.13), and neither are production orders, Pending returns or older sales orders (AC-26.5, AC-26.6, AC-26.11).
4. **Check every line before writing anything.** Call `StockQueries.GetFiguresAsync(lineItemIds)` once. Inside the write transaction it reads on the same connection. Take `Warehouse` only: production storage and Pending returns never count (D-11). For each line in `LineNo` order whose quantity is above the item's Warehouse stock, add the sentence "Not enough stock: {item} in Warehouse has {available} {unit}; sales order #{number} needs {quantity} {unit}." (`MessageFormat.Number` for both figures, design 6.1). If any line is short, throw one `ConflictException(StockLedger.InsufficientStockCode, sentences joined with a space)`, with no `errors` field (design 7.2). A line whose quantity equals the stock is covered (AC-26.4). Lines are distinct items (unique index), so no item is counted twice.
5. **One time for the whole shipment**: `var shippedAt = clock.UtcNow;`, read once (design 9, v1.1 default 14).
6. For each line in `LineNo` order: `await ledger.AddAsync(new Movement { Type = MovementType.ShippedToCustomer, ItemId = line.ItemId, WarehouseChange = -line.Quantity, OccurredAt = shippedAt, UserId = userId, SalesOrderNumber = number }, cancellationToken)`. The ledger checks the stock again (the last line of defence, section 5.1).
7. Set `Status = Shipped`, `ShippedAt = shippedAt`, `ShippedById = userId`, `ShipmentNote = note`, `StatusChangedAt = shippedAt`. Then `SaveChangesAsync`, and return `ReadDetailAsync(number)`.
8. After the commit, log "Sales order {number} shipped by user {userId}." (section 6, Logging).

A shipment of 500 lines holds the write lock for 500 ledger additions, the same as a 500-line production send. Real sales orders have 1 to 10 lines (PRD section 5). See section 9, risk 19.

## 6. Cross-cutting decisions

- **Authentication** (T-003). `AddIdentityCore<AppUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>()`. This is the API-only Identity set-up, with no cookies or UI pages. Password options: `RequiredLength = 8`, `RequireDigit`, `RequireLowercase`, `RequireUppercase` on, `RequireNonAlphanumeric` off (PRD section 5). Lock-out: `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 15 min`, `AllowedForNewUsers = true`. `User.RequireUniqueEmail = true`. Sign-in uses `UserManager` (`IsLockedOutAsync`, `CheckPasswordAsync`, `AccessFailedAsync`, `ResetAccessFailedCountAsync`) in the order of section 4.1 item 5, holding the account's lock from section 5.2 (decision #61). `Auth/AppUserManager` overrides `IsLockedOutAsync` and `AccessFailedAsync` so lock-out reads the app's `Clock`, not the computer clock (decision #50).
- **Tokens** (T-003). `JwtTokenService` issues HS256 tokens signed with `JWT_SIGNING_KEY` (at least 32 characters). The issuer and audience come from `Jwt:Issuer` and `Jwt:Audience`; the lifetime is `Jwt:TokenLifetimeHours` (8). Claims: `sub` (user id), `email`, one `role` claim per role, `stamp` (security stamp), `jti`. JwtBearer options: `MapInboundClaims = false`, `RoleClaimType = "role"`, `NameClaimType = "email"`, `ClockSkew = TimeSpan.Zero`. `JwtBearerEvents.OnTokenValidated` loads the user and fails the token if the user is missing or inactive, or the stamp differs (section 4.1 item 6). `OnChallenge` calls `HandleResponse()` and writes the `401` ProblemDetails.
- **Authorization** (T-003). A fallback policy requires an authenticated user on every endpoint. `[AllowAnonymous]` is allowed only on sign-in, `/health` and OpenAPI. Each action declares its roles from PRD 4.2 with `[Authorize(Roles = Roles.WarehouseOrManager)]`, where a comma in the roles string means "any of" (AC-05.2). A custom `IAuthorizationMiddlewareResultHandler` writes the `403` ProblemDetails body naming the roles, taken from the failed `RolesAuthorizationRequirement`. (v1.1) Reads are limited by role too. Each action carries its own attribute, and a controller-level one is used only when every action shares it (section 4.1 item 13, decision #156).
- **First Manager and start-up checks** (T-003, US-02, PRD row 18). In `Program.cs`, before the app listens: (1) tolerance 0–100; (2) `JWT_SIGNING_KEY` set and at least 32 characters; (3) migrations; (4) the three roles exist, created if missing; (5) if the database has no users, `ADMIN_EMAIL` and `ADMIN_PASSWORD` must be set and the password must pass the policy, and then one active user is created with the Manager role only. If any users exist, nothing about users is touched (AC-02.2). A failed check throws `StartupException` with the exact message from design 7.4. Program catches it, logs it at Critical level, and exits with code 1 without listening. (v1.1) Step 3 also applies `SalesOrders` to a v1 database (section 3.4). Step 4 creates the four roles of `Roles.All`, so an existing database gains `Seller` on its first v1.1 start, and nothing else changes (AC-30.3, decision #155).
- **Validation.** Hand-written in each service, using `Common/Validation/ValidationErrors`. The collector gathers messages keyed by camelCase JSON path (`lines[0].quantity`) and throws one `ValidationException`. Shared helpers: `Quantity.HasAtMostDecimals(value, 3)` (true when `Math.Round(value, n) == value`, so trailing zeros do not count), `Quantity.Round3` (`MidpointRounding.AwayFromZero`), the input maximums `Quantity.IsWithinMax` / `IsWithinMaxAmountPerUnit` with their messages `AboveMaxMessage`, `BelowMinMessage` and `AmountPerUnitAboveMaxMessage` (section 3.1, decisions #80, #103), `Text.Clean` (trim; empty becomes null), and for order bodies `Features/ProductionOrders/OrderLineMatcher` (item required, item on the order, no duplicates, the missing-lines message; options in `OrderLineRules`), used by the send, the completion, the complementary request and the complementary send (decisions #100, #105g, #106b). Each field gets at most one message, from the first rule it fails (decision #75). The collector keeps at most `ValidationErrors.MaxMessages` (100) messages; later ones are only counted, and `Detail` ends with the "{n} more problems are not shown." sentence, which `ToDictionary` (the `errors` field) leaves out (decision #122). Size limits live in `Common/Validation/RequestLimits`: `MaxBodyBytes` (1 MB, applied to Kestrel and IIS in `Program.cs`), and `MaxLines` (500) and `MaxRoles` (10) with their checks `CheckLineCount` and `CheckRoleCount`, which every service with a `lines` or `roles` body calls first, before its write transaction (section 4.1 item 12, section 5.2). `[ApiController]`'s automatic `400` stays on only for unreadable bodies (section 4.1 item 2), with a custom `InvalidModelStateResponseFactory` that produces the design's body. Request records carry no data annotations, so all other validation happens in the service, in the design's check order. (v1.1) Two helpers move into `Common/Validation`, so each rule exists once (decision #161). `BoolParser.ParseOptional(value, parameter, errors)` reads `true`/`false` in any letter case (missing or blank means not set; anything else gets "{parameter} must be true or false."); `NotificationService` (`unreadOnly`, unchanged behaviour and message) and `CustomerService` (`isActive`) use it. `DayRange.Start(day)` and `DayRange.EndExclusive(day)` give 00:00:00 UTC of a day and of the next one (`null` for 9999-12-31, which has no next day, decision #89); `MovementService` and `ShippedTotalsService` use them.
- **Error handling** (T-001). Services throw `ApiException` subclasses: `NotFoundException`, `ConflictException`, `BadRequestException`, `ValidationException`. Each carries a status, a `code` and a `detail`, plus optional field `errors`. A `code` used by more than one feature is a constant in `Common/Errors/ErrorCodes.cs` (`order-cancelled`, `material-in-use`, `too-many-lines`, and the framework codes, including `request-too-large` and `request-timeout`); a code used by one feature may be a constant in that feature (for example `StockLedger.InsufficientStockCode`). One `IExceptionHandler` turns them into ProblemDetails through `IProblemDetailsService`, adding `code`, the fixed `title` per status (design 6; `ProblemResponse` holds them, including "Request timeout" for `408` and "Request too large" for `413`) and `traceId`. A `BadHttpRequestException`, which Kestrel throws when model binding starts reading a body it will not deliver, is mapped by its own `StatusCode`: `413` → `413` `request-too-large`, `408` → `408` `request-timeout`, anything else → `400` `validation` "The request body could not be read.", none with `errors` (section 4.1 item 2, decision #124). Unknown exceptions become `500` `internal-error` and are logged with their stack trace. A `DbUpdateException` from a UNIQUE or PRIMARY KEY constraint becomes `409` `concurrent-change`; any other `DbUpdateException` is a `500` (section 4.1 item 11, decision #46). `UseStatusCodePages` covers bodiless `404` and `405` responses. Number formatting in messages follows design 6.1 through `Common/Quantities/MessageFormat` (invariant culture, `#,0.###`, percentages with a sign and two places).
- **JSON** (skeleton, `Program.cs`). camelCase; string enums; `NumberHandling = Strict` (strings for numbers refused); null fields always written.
- **Time.** Services get the time from `Clock` (T-001), which wraps .NET's `TimeProvider` and truncates to whole seconds. Tests replace `TimeProvider` with `FakeTimeProvider` to test token expiry, lock-out and newest-first ordering.
- **Logging.** Built-in `Microsoft.Extensions.Logging` to the console. Information for start-up and each state change, with the record's number and the user id: order sent, completed, cancelled; complementary order requested, approved, rejected, sent; return instructed, received, cancelled; sign-in, user created, roles set, deactivated, reactivated, password changed or set. Order, complementary-order and return changes are logged only after their write transaction has committed, so the log never reports one that was rolled back; new code follows this rule. (As built, the user and password services log inside the transaction, just before it commits; a failed commit there could leave a log line for a change that was not saved. Low impact; noted for a later tidy-up, not a v1 task.) Receipts and adjustments are not logged: the movement itself is the record (decision #103). (v1.1) Sales orders: "Sales order {number} created by user {userId}.", "... shipped by user {userId}.", "... cancelled by user {userId}.", each after its commit. Customers are master data, like units and items, and are not logged; their notes may hold business contact details and must never appear in a log line (decision #163). Warning for `concurrent-change`. Error for `500`, with the stack trace. A refused body (`413`, `408`, or the unreadable-body `400`, section 4.1 item 2) is one Information line from `ApiExceptionHandler`, with the status, method, path and Kestrel's reason, and no stack trace: anyone can send such requests without signing in, and before SEC-02 each one wrote about 27 lines at Error. In Production, `appsettings.Production.json` raises the level of the category `InventoryTracker.Api.Common.Errors.ApiExceptionHandler` to Warning, which drops those lines; its Warning and Error lines stay (decision #126, applied by #130). Development and the tests keep Information. Passwords, tokens, the signing key and request bodies are never logged. EF SQL logging is at Warning.
- **Configuration.** Non-secret settings are in `appsettings.json`, then `appsettings.{Environment}.json` (`appsettings.Production.json` in Production), then environment variables; later sources win. Secrets come only from environment variables, or from `dotnet user-secrets` in Development; Production never reads user-secrets. Both feed the same configuration keys. Nothing secret is in a tracked file (PRD section 5, AC-02.5). `docs/07-release.md` section 3 is the owner's full list for Production.

| Key | Kind | Default | Notes |
|-----|------|---------|-------|
| `ADMIN_EMAIL` | secret | none | used only when there are no users |
| `ADMIN_PASSWORD` | secret | none | used only when there are no users; must pass the policy |
| `JWT_SIGNING_KEY` | secret | none | at least 32 characters; required to start |
| `ConnectionStrings__Default` | setting | `Data Source=inventory.db` | a relative path resolves to `src/InventoryTracker.Api/` |
| `ConnectionStrings__Default` in Production (`scripts\run.ps1`) | setting | `Data Source=%LOCALAPPDATA%\InventoryTracker\inventory.db` | `run.ps1` sets this when the variable is not set, and creates the folder on the first start. A value it is given must name an absolute path, or the script stops before starting the API (relative paths would resolve against a content root that differs between source and published runs). Outside the repository and inside the user's profile (SEC-16, decision #128); a separate file from the Development database. |
| `Inventory__TolerancePercent` | setting | `2` | 0 to 100, else start-up stops (PRD section 5) |
| `Jwt__Issuer`, `Jwt__Audience` | setting | `InventoryTracker` | |
| `Jwt__TokenLifetimeHours` | setting | `8` | AC-01.2 |
| `ASPNETCORE_ENVIRONMENT` | setting | `Development` via launch profile | user-secrets and OpenAPI are Development-only |
| `ASPNETCORE_ENVIRONMENT` in Production (`scripts\run.ps1`) | setting | always `Production` | `run.ps1` sets the variable and passes `--environment Production`, which wins over any other value; it never uses the launch profile (`dotnet run --no-launch-profile` from source, or the published `InventoryTracker.Api.exe`) (SEC-13, decision #128). It binds `http://127.0.0.1:<port>` and `http://[::1]:<port>` only (`--urls`, port 5133 by default, `-Port` to change) with no option for another address, and refuses to start if any `Kestrel__*` variable is set (SEC-03, #119). |
| `Logging__LogLevel__InventoryTracker.Api.Common.Errors.ApiExceptionHandler` | setting | `Information`; `Warning` in Production | set to `Warning` by `appsettings.Production.json` (decisions #126, #130); see Logging above |

(In `appsettings.json` the double underscore is written as a section, for example `"Inventory": { "TolerancePercent": 2 }`.)

(v1.1) Version 1.1 adds no configuration key. The sample requests gain one `.env` name, `SAMPLE_SELLER_PASSWORD`, which the API never reads (T-032, decision #135).

- **Hosting (v1).** One process on the owner's Windows machine, reachable from that machine only (owner decision #119), started by `scripts\run.ps1` (`docs/07-release.md` section 6). Before starting, the script checks `JWT_SIGNING_KEY` (set, at least 32 characters) on every start, `ADMIN_EMAIL` and `ADMIN_PASSWORD` only when the database file does not exist yet, warns when `ADMIN_PASSWORD` is still set afterwards, and refuses to start while another copy of the API is running or the database file is open (`Assert-ApiStopped` in `scripts\common.ps1`; section 9, risk 9). Plain HTTP is acceptable only because traffic never leaves the machine. `Program.cs` calls `UseHttpsRedirection` in Production, but with no HTTPS endpoint it redirects nothing and logs one "Failed to determine the https port" warning at start-up (expected; release guide section 10). HTTPS with HSTS, and a sign-in rate limit, are required before the API is reachable from any other machine (SEC-03, SEC-05, release guide section 9). No container image in v1 (decision #132).

- **OpenAPI document** (T-021, decision #110). `/openapi/v1.json` (Development only) is generated from the code. Each controller action declares its success status and its `409` codes with `[ProducesResponseType]`, so the document cannot drift from the code. Shared rules in `Common/OpenApi` add the Bearer scheme, the `400`/`401`/`403`/`404` ProblemDetails responses, `413` on every operation that reads a body (SEC-01, SEC-02), decimal quantities as numbers and enum names. A request field is shown as required unless its record parameter has a default: **every new optional request field must be declared with `= null`** (for example `string? Note = null`), or the document marks it required. `PageQuery.Skip` is `[BindNever]` and hidden. The minimal-API JSON options copy the controllers' number and enum settings so the document matches; this has no effect on error bodies.
- **Code comments.** The owner's comment standard (CLAUDE.md) applies to every file. `GenerateDocumentationFile` is on, so the build warns (CS1591) about any public member without a `///` comment. The build must have 0 warnings.

## 7. Testing strategy

- **Framework:** xUnit v3 (4.0.3) on Microsoft.Testing.Platform (selected in `global.json`), with plain `Assert`. One test project: `tests/InventoryTracker.Api.Tests`.
- **Unit tests** (`Unit/`): pure code with no database. These cover the D-3 to D-9 calculations with every PRD worked example (AC-15.2 to AC-15.5, AC-18.2, AC-20.4 to AC-20.8), rounding and decimal-place rules, message formatting, and notification messages.
- **Integration tests** (`Integration/<Feature>Tests.cs`): HTTP calls against the real app through `TestApiFactory` (`WebApplicationFactory<Program>`). Each factory gets its own temporary SQLite file, not in-memory, so locking behaves as in production. Users are created through the API or through test helpers, and tests sign in with real tokens. Every acceptance criterion a task lists gets at least one test. The test's comment names the criterion (`/// AC-15.3: ...`). Each endpoint's tests include its `401` and `403` cases.
- **Concurrency tests:** fire conflicting requests with `Task.WhenAll` on separate `HttpClient`s and assert that exactly one succeeds and stock is never negative (T-010, T-020). Because a plain `Task.WhenAll` usually lets one request finish first, the race tests also **force each ordering**: `Infrastructure/ForcedOrder` and the interceptors (`BeforeSaveInterceptor`, `AfterFirstReadInterceptor`) hold one request at a chosen point (for example before it writes to `Movements`) until the other has run, so both "A first" and "B first" are proved every run (decisions #105h, #109d).
- **Request-limit tests** (SEC-01, SEC-02). The in-memory `TestServer` behind `TestApiFactory` has no body size limit, so it cannot show the 1 MB limit. `Integration/RequestBodyLimitTests` therefore starts the same app on real Kestrel, on a free local port (its nested `KestrelApiFactory`, `UseKestrel(0)`), and writes raw HTTP over a TCP socket, because `HttpClient` cannot send a `Content-Length` that lies or a malformed chunk. It checks the `413`, `400` and log-level outcomes. `RequestLinesLimitTests` and `RequestRolesLimitTests` prove the caps run before the write transaction with `Infrastructure/HeldWriteLock`, which holds SQLite's write lock from the test's own connection (`BEGIN IMMEDIATE`, then `ROLLBACK`, so nothing changes): the over-cap request must still be answered, while a control request at the cap is shown to be waiting for the lock. New tests of "refused before the transaction" use this helper, not a copy.
- **Test helpers are shared, not copied** (decision #101). Order, complementary-order and return set-ups live in `Infrastructure/OrderFlow` (imported with `using static`); expected messages are typed out literally in `Infrastructure/ExpectedMessages`, never built from the code under test (decision #88). Some tests deliberately insert rows, including movements, straight through the DbContext to build a state no endpoint can produce; that is allowed in tests only (decision #112).
- **Role matrix** (`Integration/Scenarios/RoleMatrixTests.cs`, decision #109b): compares its table of all 48 ((v1.1) 62) endpoints with ASP.NET Core's `EndpointDataSource` (not the OpenAPI document, which exists in Development only) and checks every allowed and refused role, so a new or changed endpoint fails it until the table is updated.
- **Performance tests:** two tests fill a database with 100,000 movements and assert a 95th percentile under 1 s for `GET /stock`, `GET /stock/{itemId}`, `GET /movements` with each filter and a deep page, and `GET /movements/{id}` (PRD section 5). They run in the xUnit collection `PerformanceCollection`, alone, after the parallel tests (decision #109a).
- (v1.1) **Role matrix for every endpoint × role** (decisions #147, #164). `RoleMatrixTests` stays the one table, and every v1.1 task keeps it complete:
  - `Prd42` becomes the v1.1 PRD 4.2 table, row for row. The v1 row "View units, items, recipes, ..." splits into "View units, items, stock" (all four roles) and "View recipes, movement history, orders, complementary orders, returns" (Warehouse, Production, Manager). Four rows are new: view customers, sales orders and reports (W, M, S); manage customers (M, S); create or cancel a sales order (M, S); ship (W, M). Every v1 row keeps its v1 roles; Seller is blank in all of them except sign-in and own password.
  - **Callers**: Warehouse, Production, Manager, Seller (one per role, AC-05.1, AC-30.4), Warehouse plus Production (AC-05.2) and Production plus Seller (AC-30.8). At 62 endpoints that is 372 role cases plus 62 no-token cases (v1: 192 plus 48).
  - `MatrixRow.ExpectedAuthorization` already treats "all roles" as no attribute (`Action.Allowed.Length == Roles.All.Count`), so once `Roles.All` has four roles the split View rows check exactly the strings of section 4.1 item 13. `PrdAction.ForbiddenMessage` gains the three new sentences, typed out in `ExpectedMessages` (decision #88): `WarehouseProductionOrManager`, `WarehouseManagerOrSeller`, `ManagerOrSeller`.
  - The literal `48` in `RoleMatrixTests` and `OpenApiTests` becomes one constant, `RoleMatrixTests.DesignEndpointCount`. Each endpoint task raises it by the endpoints it adds (T-024 to 55, T-025 to 58, T-027 to 59, T-028 to 60, T-030 to 62).
  - New rows arrange their records through the API, like the v1 rows: a fresh customer to edit, deactivate, reactivate or delete; a Created sales order for a fresh item with Warehouse stock to ship (each case its own item, so cases never compete for stock); a Created sales order to cancel; and one shared customer and sales order for the reads.
- (v1.1) **Upgrade from a version 1 database** (`Integration/UpgradeTests.cs`, T-022 and T-023). The test builds its own v1 database with `IMigrator.MigrateAsync("20261114053033_PercentagePrecision")` on a temporary file. It inserts v1 rows with SQL (three roles, users with roles, a unit, items, movements, a completed order, a return, a notification), then starts `TestApiFactory` on that file (`DatabasePath`). It asserts that `SalesOrders` is applied and nothing is pending, that every row count and sampled value is unchanged, that `PRAGMA foreign_key_check` is empty, that the new foreign key and index exist (a duplicate shipment movement and an unknown sales order number are both refused), that `sqlite_sequence` is unchanged, and (from T-023) that `Seller` exists while every user's roles are exactly as before (AC-30.3). It never touches the owner's database.
- (v1.1) **Race tests** use the existing `ForcedOrder` and interceptors to force both orderings (decisions #105h, #109d): two ships of one sales order and two ships competing for stock (`ForcedOrder("Movements")`); ship against cancel (`ForcedOrder("SalesOrders")`); customer delete against a new sales order (AC-29.8, forced on `Customers` and on `SalesOrders`); ship against a negative adjustment, a production send and a complementary send (T-031, beside the v1 cross-endpoint races).
- (v1.1) **Shared helpers** (decision #101): `Infrastructure/SalesFlow.cs`, imported with `using static`, holds `CreateCustomerAsync`, `CreateSalesOrderAsync`, `ShipAsync`, `CancelSalesOrderAsync`, `ReadSalesOrderAsync`, and `InsertSalesOrderAsync` (any status, through the DbContext inside a write transaction, for states such as "Shipped" before the ship endpoint exists). Expected PRD sentences go into `ExpectedMessages`, typed out.
- (v1.1) **Report and shipment times** are set with `FakeTimeProvider` (`TestApiFactory.FakeTime`), so AC-32.3's shipments on 2026-12-02 and 2026-12-05 are made through the real ship endpoint.
- (v1.1) **Performance at the v1.1 volume** (`Scenarios/SalesPerformanceTests`, in `PerformanceCollection`, T-031). It seeds, with set-based SQL as the existing seeds do, 100,000 movements, 1,000 orders, 500 items, 500 customers and 5,000 sales orders of 1 to 10 lines (mixed statuses, shipments spread over a year). It then times 20 calls each (95th percentile under 1 s, PRD section 5) of: `GET /stock` (page of 100, with open demand), `GET /stock/{itemId}`, both reports (all time, one month, each filter, a deep page), `GET /sales-orders` (each filter), `GET /sales-orders/{number}`, `GET /customers`, and the writes `POST /sales-orders`, ship and cancel.
- (v1.1) **Suite time.** The role matrix roughly doubles and the v1.1 performance test adds a seed of about 30,000 rows. Expect the full suite to grow from about 2 minutes 15 seconds (measured 2026-11-24) to 3 or 4 minutes. That is well inside CI's 25-minute timeout (decision #131).
- **Acceptance tests:** written by `qa-engineer` in phase 5. Suggested location: `tests/InventoryTracker.Api.Tests/Acceptance/`, so the one command still runs everything.
- **Run everything:** `dotnet test` from the repository root. The full suite takes about 1.5 to 2 minutes on the owner's machine, mostly because each test factory migrates a fresh database (decision #109f). Two runs at the same time in one working tree must use separate `--artifacts-path` folders, or they lock each other's `bin/` files (decision #67). `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1` runs the same suite after a Release build with warnings as errors (`scripts\build.ps1`), and CI runs those steps on every push and pull request to `main` (decision #131). None of them needs secrets: the tests set their own values.

## 8. Running locally

Prerequisites: .NET SDK 10.0.x (10.0.401 confirmed on this machine). Nothing else. Commands are for PowerShell on Windows, from the repository root.

```powershell
# once per machine: install the pinned dotnet-ef tool
dotnet tool restore

# once per machine: store the secrets outside the repository (Development only)
dotnet user-secrets set ADMIN_EMAIL "owner@example.com" --project src/InventoryTracker.Api
# replace the placeholder with your own password: 8+ characters, an upper-case and a lower-case letter, a digit
dotnet user-secrets set ADMIN_PASSWORD "<choose-your-own-strong-password>" --project src/InventoryTracker.Api
$bytes = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
dotnet user-secrets set JWT_SIGNING_KEY ([Convert]::ToBase64String($bytes)) --project src/InventoryTracker.Api

# build and test
dotnet build
dotnet test

# run (creates and migrates src/InventoryTracker.Api/inventory.db on first start)
dotnet run --project src/InventoryTracker.Api
# in a second window:
Invoke-RestMethod http://localhost:5133/health        # -> Healthy
```

This is the Development run: the launch profile selects Development, which reads user-secrets and maps `/openapi/v1.json`. To run the release on this machine, use `scripts\run.ps1` instead (`docs/07-release.md` sections 2, 3 and 6): it always runs Production on `127.0.0.1` and `[::1]` only, reads the secrets from environment variables (never user-secrets), and uses its own database, `%LOCALAPPDATA%\InventoryTracker\inventory.db` by default (section 6, Configuration and Hosting).

- (v1.1) Upgrading an existing database: nothing to run by hand. The first start of v1.1 applies the `SalesOrders` migration and creates the Seller role (section 3.4). For the owner's Production database, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup-database.ps1` **before** that first start (`docs/07-release.md` section 7). A Development database (`src/InventoryTracker.Api/inventory.db`) is upgraded the same way, or deleted to start fresh.
- Start again with an empty Development database: stop the app and delete `src/InventoryTracker.Api/inventory.db*`. This does not touch the Production database; to back it up or restore it, use the scripts in `docs/07-release.md` section 7.
- Add a migration after changing entities: `dotnet tool run dotnet-ef migrations add <Name> --project src/InventoryTracker.Api --output-dir Data/Migrations`.
- Sample requests: `src/InventoryTracker.Api/InventoryTracker.Api.http` (VS Code REST Client, or Visual Studio 17.12+) walks through the main flow against a fresh database. Its sign-in request reads `ADMIN_EMAIL` and `ADMIN_PASSWORD` with `$dotenv` from `src/InventoryTracker.Api/.env` (git-ignored), not from a `.env` at the repository root. The app itself never reads a `.env` file (decision #32).

## 9. Risks and open points

1. **Anonymous `/health` and OpenAPI vs AC-01.5** ("every endpoint except sign-in returns 401 without a token"). Both are infrastructure endpoints outside `/api/v1`, and both reveal nothing: one word, and the published API description, in Development only. Default: anonymous (decision #28). If the owner reads AC-01.5 strictly, make `/health` require a token. That is a one-line change.
2. **Unreadable bodies and the two caps come before the 404 check** (section 4.1 items 2 and 12, section 5.1). Unreadable bodies (`413`, `408`, `400`) come first because the framework reads the body before the action runs; the line and role caps because they are checked before the write transaction (decisions #123, #125). Design 2.2 now states both. QA should not expect `404` for a request whose body is not valid JSON, is over 1 MB, or has more than 500 lines or 10 roles.
3. **SQLite has a single writer.** Fine for one small company, because writes take milliseconds. The request limits (section 4.1 item 12) keep it that way: no body can make one write transaction check more than 500 lines or 10 roles. Under heavy parallel load, requests queue for up to 30 seconds and then fail with `500`. The PRD's performance target (95% under 1 s with 100,000 movements) is checked for the read endpoints by T-020's performance tests (section 7); it was not measured for writes under parallel load, which QA may want to sample. Sums per item were measured at under 10 ms.
4. **Identity and role changes. Resolved by T-014 (decision #66a).** In Identity 10.0.15, `AddToRolesAsync` / `RemoveFromRolesAsync` do not change the security stamp (tested), so a role change leaves existing tokens working with their old roles until they expire or the user signs in again (section 4.1 item 6, AC-03.5). If a future Identity version starts changing the stamp on role changes, that test fails and the user would simply be signed out at once, which is stricter than the PRD and acceptable.
5. **Testing start-up refusal** (AC-02.3, AC-02.4). Program exits without listening, so `WebApplicationFactory.CreateClient()` throws. T-003 tests assert that it throws, capture the logged message with a test logger provider, and check that the database has no users.
6. **PostgreSQL later:** the two transaction helpers change (section 5.2: `InWriteTransactionAsync` and `StockQueries.InReadTransactionAsync`), the WAL pragma goes, migrations are generated for the new provider from the model, which already holds the unlimited `Message` and the `(28, 6)` percentages (the SQLite versions of those two migrations are empty, decision #98g), the tests' SQL seeds (performance tests) use SQLite functions and need rewriting, and order numbers may get gaps after rolled-back inserts (PostgreSQL sequences are not transactional). Everything else in the model is portable, including the unlimited `text` notification columns (decision #63). The in-process sign-in lock stays correct on PostgreSQL as long as the API runs as one process (risk 9).
7. **Tooling.** xUnit v3 needs the Microsoft.Testing.Platform runner. `global.json` selects it, and without it `dotnet test` fails on .NET 10. Visual Studio Test Explorer needs VS 2022 17.14 or later, or VS 2026.
8. **The leftover rules are subtle** (D-5 to D-9). The pure-function task (T-004) comes early and is unit-tested against every worked example, so the order tasks build on proven arithmetic.
9. **Sign-in lock-out assumes one process** (section 5.2, decision #61). The per-account sign-in lock is in memory. If the API ever runs as several instances (behind a load balancer, or after a move to PostgreSQL), simultaneous wrong passwords sent to different instances can again lose failure counts and get past the lock-out. Before running more than one instance, move the serialisation into the database (for example `SELECT ... FOR UPDATE` on the user row, or `pg_advisory_xact_lock` keyed on the user, inside the sign-in steps). The same applies to anything else that relies on one process. On the owner's machine `scripts\run.ps1` refuses to start a second copy of the API while one is running (section 6, Hosting), but nothing stops a copy started some other way.
10. **Notification messages have no length limit** (decision #63). A notification for an order with many lines is long, and is returned whole by `GET /notifications`. This is accepted: the alternative loses facts. The 500-line cap (decision #123) bounds each message at roughly 90,000 characters, and paging (at most 100 per page) bounds the response size.
11. **Gap between the token check and the write transaction** (accepted, decision #83; T-014 review finding 3). The token, including the user's active flag and security stamp, is checked once, when the request is authenticated (`OnTokenValidated`, section 6). The write transaction starts later, and may wait up to 30 seconds for SQLite's write lock under load. If a Manager deactivates the user, resets their password, or the user changes it elsewhere, in that interval, a request that already passed the check still completes. Every later request with that token gets `401`. Accepted because the window is short, the user was entitled to act when the request arrived, and the result is the same as if the request had arrived a moment earlier. Change-password closes the gap for itself, because it relies on the old password (section 5.2). If the owner ever needs the gap closed everywhere, change one place: `InWriteTransactionAsync` re-reads the caller's `IsActive` and `SecurityStamp` after `BEGIN` and throws `401` `not-authenticated` when either has changed.
12. **Extreme values under the input maximums** (decision #80). Two consequences of the limits are accepted. (a) An order whose planned amount for a material is above 999,999,999.999 (possible with a large amount per unit times a large ordered quantity) can be created, but unless enough leftover is applied its send range cannot be met by any allowed send quantity, so it can only be cancelled. No real factory value is near this. (b) Stock is a sum and is not capped. Only about a million maximum-size movements of one item could take a total, or a value stored from it, past the `decimal(18,3)` capacity. C# `decimal` and PostgreSQL `numeric` sums do not overflow at that size, so only storing such a value (`CarriedLeftover`, or a `ConsumedInProduction` movement, which the ledger then refuses with a `500`) could fail.
13. (v1.1) **Upgrading the owner's database.** This is the first migration run on a database with real data. Mitigations: the migration alters `Movements` in place and runs in one transaction (section 3.4); the prototype proved it on 100,000 movements; `UpgradeTests` keeps proving it; and the owner backs up before the first v1.1 start, as `docs/07-release.md` section 6 step 3 already says for every update. The release phase must repeat that step in the v1.1 release notes. What remains: an unexpected failure rolls back and the app does not start. The fix is to restore or run v1 again; no data is lost.
14. (v1.1) **Role-based reads could lock a role out by mistake.** One wrong attribute string, or an attribute on a controller that combines with the actions' own (section 4.1 item 13), would refuse a read the PRD allows. The role matrix checks every endpoint for six callers against the PRD 4.2 table and compares each endpoint's declared roles, so such a mistake fails the suite. No v1 user loses a read: Warehouse, Production and Manager keep every v1 read; only the new role and the new endpoints are limited.
15. (v1.1) **Hand-edited migration.** The `SalesOrders` migration's `Up` differs from what `dotnet-ef` would generate (section 3.4). A developer who deletes and regenerates it would bring the table rebuild back. Mitigations: the comment in the migration, the script check in T-022's criteria, and `UpgradeTests`, which asserts the `Movements` row count and `sqlite_sequence` (a rebuild keeps those) **and** that the migration's SQL contains no `ef_temp_` table, via `IMigrator.GenerateScript`.
16. (v1.1) **Ordinal name order after a PostgreSQL port** (design 2.5). SQLite's default `BINARY` comparison gives the ordinal order the design asks for. PostgreSQL's default collation follows the locale (letters before punctuation, accents next to their base letter), so the report order would change unless `NameNormalized` gets the `"C"` collation at the port (section 3.1). A test in T-030 pins the order with names that differ between the two ("Émile" after "Zeta", "apple" between "Apple" and "Banana" when upper-cased), so a port that forgets this fails at once.
17. (v1.1) **Suite time.** About 1.8 times as many role-matrix cases, plus a second large performance seed (section 7). Acceptable. If it becomes a problem, the pre-migrated database copy of decision #109f is still the first remedy.
18. (v1.1) **Report and open-demand speed beyond the PRD volume.** They are computed on request (section 3.5). At the PRD volume the worst case is under 100 ms. Both grow linearly with shipped lines; at ten times the volume (275,000 lines) the all-time reports would be near 1 s. A later version that needs more could add a period summary table, but none is needed now.
19. (v1.1) **A 500-line shipment** holds the write lock for 500 ledger additions, each with a per-item stock sum, which could take a few seconds on a large history. A 500-line production send already does the same in v1. The line cap (AC-24.5) bounds it, and real sales orders have 1 to 10 lines.
20. (v1.1) **The customer list holds contact details** (PRD section 11). Customer notes are readable by Warehouse, Manager and Seller only (the role attribute on both customer reads), never logged (section 6), and kept on the owner's machine (#119). Like everything else they are in the database file and its backups, which `docs/07-release.md` section 7 already tells the owner to protect.

## 10. (v1.1) Tech-lead decisions for version 1.1

Made in this architecture update, for the delivery manager to log in `docs/decisions.md` with these numbers (the sections above already cite them):

| # | Decision | Section |
|---|----------|---------|
| 154 | One migration, `SalesOrders` (T-022), for the whole v1.1 schema. Its `Up` is hand-edited before the first commit: `Movements.SalesOrderNumber` is added in place with `ALTER TABLE ... ADD ... REFERENCES`, instead of EF Core's three-transaction table rebuild, so the upgrade of the owner's database is one transaction. `Down` stays as generated. Back up before the first v1.1 start. | 3.4 |
| 155 | The Seller role is created by the existing start-up role step (`Roles.All` = Warehouse, Production, Manager, Seller), on new and existing databases; there is no role data in any migration. | 3.4, 6 |
| 156 | Role-based reads: `[Authorize(Roles = ...)]` on each action; a controller-level attribute only when every action shares it, because class and action attributes combine with AND. New constants, written in role order so the `403` sentence matches design 7.1: `WarehouseProductionOrManager`, `WarehouseManagerOrSeller`, `ManagerOrSeller`. | 4.1 item 13 |
| 157 | `Movements.SalesOrderNumber`: a `Restrict` foreign key to `SalesOrders`, required on `ShippedToCustomer` and allowed nowhere else (a ledger rule); `ShippedToCustomer` is W− only. The unique filtered index `IX_Movements_OneShipmentPerLine` (`SalesOrderNumber`, `ItemId`) is both the one-shipment backstop and the index of the `salesOrderNumber` filter. | 3.2, 5.1 |
| 158 | The ship checks every line against Warehouse stock first (one `409` naming every short line, in line order), then adds one ledger movement per line. One `Clock.UtcNow` read gives `ShippedAt`, `StatusChangedAt` and every movement's `OccurredAt`. | 5.3 |
| 159 | Open demand is a fourth query in `StockQueries.ReadFiguresAsync`, in the same snapshot as the other figures, and is shown for every item type. | 3.5 |
| 160 | Both reports are computed on request from `SalesOrderLines` joined to `SalesOrders` (Shipped, `ShippedAt` in whole UTC days), grouped, then joined to `Items` and `Customers` and ordered by `NameNormalized`, then id. No stored totals. A PostgreSQL port must give `NameNormalized` the `"C"` collation. | 3.5, 3.1 |
| 161 | Shared helpers, each in one place: `BoolParser` (from `NotificationService`) and `DayRange` (from `MovementService`) in `Common/Validation`; `CustomerSummary` in `Common/Contracts`; `SalesOrderService.ReadDetailAsync` as the one builder of the sales order detail. | 2, 3.5, 6 |
| 162 | Sales order numbers and customer ids are `AUTOINCREMENT` keys and never reused (a deleted customer's id stays `404`). Sales order lines: `Restrict` foreign keys, unique (sales order, item), `LineNo` = position in the body. | 3.1, 3.2 |
| 163 | Logging: sales order created, shipped and cancelled are logged after the commit with the number and the user id. Customers are not logged (master data); customer notes are never logged. | 6 |
| 164 | Tests: role-matrix callers are Warehouse, Production, Manager, Seller, Warehouse+Production and Production+Seller. Every endpoint task adds its matrix rows, OpenAPI expectations and `.http` sample in its own commit, and raises `DesignEndpointCount`. `UpgradeTests` builds its own v1 database. | 2, 7 |
