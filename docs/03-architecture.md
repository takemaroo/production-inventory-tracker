# Architecture: Production & Inventory Tracker API

Based on `docs/01-prd.md` (approved 2026-11-11) and `docs/02-design.md`. Decisions referenced as "#n" are rows in `docs/decisions.md`. When this document and the code disagree, the code is wrong: change this document first, then the code. Checked against the code as built (T-001 to T-021, HEAD d09c6db) on 2026-11-21, before QA (decision #114), and updated for the security fixes SEC-01 and SEC-02 (commit 0961b35, decisions #122 to #126).

## 1. Stack

Checked on the owner's machine on 2026-11-11: .NET SDK 10.0.401 (runtimes 10.0.12 and 8.0.18), git 2.55, Node 24.21 (not needed in v1). Docker is not installed and not needed. Package versions are the latest stable 10.0.x releases on nuget.org on that date.

| Layer | Choice | Version | Why |
|-------|--------|---------|-----|
| Runtime | .NET | 10 (LTS, supported to Nov 2028), SDK pinned to 10.0.x by `global.json` | Installed on the owner's machine. .NET 11.0 shipped this month, but it is short-term support and not installed. |
| Back end | ASP.NET Core Web API with controllers (`[ApiController]`) | 10.0 | Owner's preferred stack. Controllers are the most widely documented style, and attribute routing and `[Authorize]` keep each endpoint readable. |
| Data access | Entity Framework Core + `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.15 | Mainstream ORM. Migrations are versioned schema changes. PostgreSQL later means changing the provider, not rewriting queries. |
| Database | SQLite (one file) | bundled with the provider | PRD section 5. Nothing to install. |
| Users and passwords | ASP.NET Core Identity (`Microsoft.AspNetCore.Identity.EntityFrameworkCore`) | 10.0.15 | Decision #8. Salted password hashing, password policy, lock-out and roles are already built and tested. |
| Tokens | JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`) | 10.0.15 | Decision #8. Standard bearer tokens, validated by the framework. |
| API description | `Microsoft.AspNetCore.OpenApi` | 10.0.15 | Built-in OpenAPI generator (PRD section 5). It uses the `///` comments as descriptions. |
| Health check | `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | 10.0.15 | `GET /health` proves the app and its database are reachable. |
| Tests | xUnit v3 on Microsoft.Testing.Platform, `Microsoft.AspNetCore.Mvc.Testing` | xunit.v3 4.0.3, Mvc.Testing 10.0.15 | xUnit is the most common .NET test framework. `WebApplicationFactory` runs the real app in memory. T-003 adds `Microsoft.Extensions.TimeProvider.Testing` 10.11.0 for token-expiry and lock-out tests. |
| Tools | `dotnet-ef` as a local tool (`dotnet-tools.json`) | 10.0.15 | `dotnet tool restore` installs the same version on any machine. |
| Front end | none | | No user interface in v1 (PRD section 3). |

Departures from the owner's preferred stack: none. Deliberately not used, to keep the code small and plain: MediatR, AutoMapper, FluentValidation, FluentAssertions (commercial licence since v8), Swashbuckle. Validation and mapping are plain C#.

## 2. Project structure

```
InventoryTracker.slnx            solution file (the .NET 10 default format); open in VS 2022 17.13+, VS 2026, Rider or VS Code
global.json                      pins the SDK to 10.0.x and selects the Microsoft.Testing.Platform test runner
dotnet-tools.json                local tool manifest (dotnet-ef)
.env.example                     names of the secrets, no values (AC-02.5)
src/InventoryTracker.Api/
  Program.cs                     composition root only: service registrations, middleware order, start-up work
  appsettings.json               non-secret settings: connection string, tolerance, JWT issuer/audience/lifetime
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

Inside a feature folder, for example `Features/ProductionOrders/`:

- `ProductionOrdersController.cs`: thin. Route, `[Authorize(Roles = ...)]`, call the service, return the result. No business rules.
- `ProductionOrderService.cs` (or one service per action, such as `SendOrderService.cs`, when a file grows past about 300 lines): every rule, check and calculation call.
- `ProductionOrderContracts.cs`: request and response records, exactly as in `docs/02-design.md` section 5.

Rules that keep tasks parallel-safe:

- Services are registered automatically (`FeatureRegistration`), so feature tasks do not edit `Program.cs`.
- Entity configurations are discovered automatically (`ApplyConfigurationsFromAssembly`), so feature tasks do not edit `AppDbContext.cs`.
- T-002 created the whole schema in one migration. Feature tasks should not need schema changes. If one does, it adds a migration and must not run in parallel with another task that touches `Data/`. As built there are four migrations: `InitialIdentity` (skeleton), `DomainModel` (T-002), `NotificationMessageUnbounded` (T-011, decision #63) and `PercentagePrecision` (T-011, decision #82). On SQLite the last two have empty `Up`/`Down`, because SQLite keeps no length or precision in its schema; their effect is in the model snapshot, which a PostgreSQL port will turn into real column changes (decision #98g).

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
- **Case-insensitive unique names** (units, items) use a `NameNormalized` column (trimmed, `ToUpperInvariant()`) with a unique index. This works the same on SQLite and PostgreSQL (SQLite's `NOCASE` is SQLite-only and ASCII-only). Emails use Identity's own normalized columns; their uniqueness comes from `NormalizedUserName` (see AppUser in 3.2).
- **Text lengths.** Short text fields have a maximum length, checked by the services with the design's messages. Text that the system composes from other fields and that has no natural upper bound (`Notification.Message`, `Notification.DataJson`) has no maximum length: EF Core maps an unlimited `string` to `TEXT` on SQLite and `text` on PostgreSQL, which in PostgreSQL is as efficient as `varchar(n)`. Composed text is never truncated.
- **Numbers.** Production orders, complementary orders and returns use their number as the primary key (`int`, database-generated, starting at 1). Each table has its own sequence (design 2.1). No separate `Id`.
- **Deletes.** Every foreign key is `DeleteBehavior.Restrict`, except recipe lines, which belong to their recipe (`Cascade`; saving a recipe replaces its lines, AC-08.1). Movements, orders, complementary orders, returns, notifications and users are never deleted (PRD section 5). Only units and unused items can be deleted.
- **Audit.** Every "who" column is a required or nullable `string` foreign key to `AspNetUsers.Id`, paired with a UTC "when" column (AC-05.3).

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

Roles are Identity roles named `Warehouse`, `Production`, `Manager` (tables `AspNetRoles`, `AspNetUserRoles`), seeded at start-up.

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

"In use" (AC-07.3, AC-07.4) means at least one movement, or a recipe line naming it (as raw material or through its own recipe), or an order line, or a production order for it as the finished product, or a complementary-order line, or a return.

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

Which change columns each type fills (D-2): Received W+; SentToProduction W−, P+ (same quantity); ConsumedInProduction P−; ProducedOutput W+; ReturnedToWarehouse W+, P− (same quantity); Adjustment exactly one of W or P, either sign. No movement is ever created with a zero quantity (PRD row 17, AC-15.4). Movements are insert-only (AC-11.3): `Movement` properties are `init`, and `AppDbContext.SaveChanges` refuses Modified or Deleted movements (decision #35). The link and text rules in the Notes column (decision #87) are enforced by `StockLedger` (section 5.1); `Note` and `Reason` are stored trimmed, never blank, at most 500 characters.

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

The pure calculation functions in `Domain/Calculations` (T-004) are the only implementation of D-3 to D-9. Services load the numbers, call them, and store or return the results.

- **One queue for estimates and sends (decision #97).** The waiting orders that share free leftover (Created orders older than or equal to the one in question, with their lines, oldest first) are loaded by one method, `WaitingOrderQueue.AllocateAsync`, used by both the order detail's estimate (`ProductionOrderService`) and the send (`SendOrderService`). So an estimate can never disagree with what the send then applies.
- **One snapshot per combined read (decisions #85, #92).** A figure built from several queries (Warehouse and Production storage stock, committed, pending returns, free leftover, and for the estimate the waiting orders too) must see one state of the database. `StockQueries.GetFiguresAsync` runs its queries inside one read transaction when no transaction is open, or inside the caller's write transaction when one is. `StockQueries.InReadTransactionAsync` is public so a read that combines stock figures with other tables takes them all from one snapshot; `ProductionOrderService.ReadDetailAsync` (the one builder of the order detail, used by T-009, T-010, T-011 and T-016) runs inside it, or inside the caller's write transaction (it throws `InvalidOperationException` if neither is open, or if the DbContext holds unsaved changes, because it reads the database, not the tracked objects). `InReadTransactionAsync` opens a *deferred* SQLite transaction (`BEGIN`), which takes only a read snapshot in WAL mode and never queues behind writers; it must not be called inside a write transaction (section 5.2).

## 4. Interface contract

**The contract is `docs/02-design.md` sections 2 and 4 to 7, adopted as written**: base path `/api/v1`, the 48 endpoints in section 4, the request and response shapes in section 5, the ProblemDetails format in section 6, and the messages and codes in section 7. It is not repeated here, so the two cannot drift apart. The points below add to it or make it exact. They are binding in the same way.

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
    - **Lines: at most 500** (`RequestLimits.MaxLines`) on a recipe (`PUT /items/{id}/recipe`), a send, a completion, a complementary request and a complementary send. More is `400` `too-many-lines` "At most 500 lines are allowed." under `lines` (decision #123).
    - **Roles: at most 10** (`RequestLimits.MaxRoles`) on `POST /users` and `PUT /users/{id}/roles`. More is `400` `validation` "At most 10 roles are allowed." under `roles` (decision #125).
    - Both caps are checked by `RequestLimits.CheckLineCount` and `CheckRoleCount` as the first step of the service, before `InWriteTransactionAsync`, and answered on their own: the response holds only that one message, even when other fields are also wrong. So they come before the `404` for the record in the URL, where there is one (section 5.1). A missing list passes the cap; "required" stays the service's own check.
    - **Messages: a `400` lists at most 100** (`ValidationErrors.MaxMessages`, decision #122). Later messages are only counted, and `detail` ends with "{n} more problems are not shown." ("1 more problem is not shown."), with `{n}` formatted as design 6.1. That sentence is not in `errors`.

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

  A failure of 3, 4 or 5 is a programming error, not a client error: `ArgumentException`, answered `500` `internal-error` and logged at Error. Then, for each location the movement takes stock from, it sums that item's current stock in that location through `StockQueries.GetStockAsync` and refuses the movement with `409` `insufficient-stock` "Not enough stock: {item} in {location} has {available} {unit}." if the result would be below zero. Reaching exactly zero is allowed (AC-12.3), and increases are not summed. Finally it adds the movement and calls `SaveChangesAsync` at once, still inside the transaction, so `movement.Id` is set and the next movement for the same item in the same request is included in the next sum. It does not check business rules (raw materials only, order status, ranges, input maximums): those belong to the calling service. Callers that must name every short line in one message (AC-15.7, AC-18.4), or need other wording (receiving a return), check all lines first with `StockQueries` and the public `StockLedger.InsufficientStockMessage` and `LocationName` helpers, throw their own `409`, and only then add the movements. The ledger checks again, so it is the last line of defence.
- **Calculations** (D-3 to D-9) are pure functions in `Domain/Calculations` (T-004), unit-tested against every worked example in the PRD.
- **Check order** follows design 2.2. Role checks (`401`/`403`) happen first, in the framework, because of `[Authorize]`. Next come the bodies that cannot be read, also in the framework, while model binding reads the body: `413`, `408` or `400` from the web server, then `400` for invalid JSON or a wrong JSON type (section 4.1 item 2). Then, in the service:
  1. the line cap or the role cap, where the body has one (`400` `too-many-lines` or `400` `validation`, section 4.1 item 12), answered on its own and **before** the write transaction opens, so an oversized list never takes the write lock (decisions #123, #125);
  2. open the write transaction, load the record named in the URL (`404`), validate the input shape (`400`), check state (`409`), check ranges (`400`), check stock (`409`), then write.

  Only the two caps move before the transaction. Every other body check keeps its design 2.2 place, inside the transaction after the `404` (the password operations in section 5.2 are the existing exception); with at most 500 lines or 10 roles those checks take well under a millisecond.

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

Losers of a race get the specific message (`already-sent`, `insufficient-stock`, `another-order-in-production`). `concurrent-change` is only the fallback (section 4.1 item 11).

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

## 6. Cross-cutting decisions

- **Authentication** (T-003). `AddIdentityCore<AppUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>()`. This is the API-only Identity set-up, with no cookies or UI pages. Password options: `RequiredLength = 8`, `RequireDigit`, `RequireLowercase`, `RequireUppercase` on, `RequireNonAlphanumeric` off (PRD section 5). Lock-out: `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 15 min`, `AllowedForNewUsers = true`. `User.RequireUniqueEmail = true`. Sign-in uses `UserManager` (`IsLockedOutAsync`, `CheckPasswordAsync`, `AccessFailedAsync`, `ResetAccessFailedCountAsync`) in the order of section 4.1 item 5, holding the account's lock from section 5.2 (decision #61). `Auth/AppUserManager` overrides `IsLockedOutAsync` and `AccessFailedAsync` so lock-out reads the app's `Clock`, not the computer clock (decision #50).
- **Tokens** (T-003). `JwtTokenService` issues HS256 tokens signed with `JWT_SIGNING_KEY` (at least 32 characters). The issuer and audience come from `Jwt:Issuer` and `Jwt:Audience`; the lifetime is `Jwt:TokenLifetimeHours` (8). Claims: `sub` (user id), `email`, one `role` claim per role, `stamp` (security stamp), `jti`. JwtBearer options: `MapInboundClaims = false`, `RoleClaimType = "role"`, `NameClaimType = "email"`, `ClockSkew = TimeSpan.Zero`. `JwtBearerEvents.OnTokenValidated` loads the user and fails the token if the user is missing or inactive, or the stamp differs (section 4.1 item 6). `OnChallenge` calls `HandleResponse()` and writes the `401` ProblemDetails.
- **Authorization** (T-003). A fallback policy requires an authenticated user on every endpoint. `[AllowAnonymous]` is allowed only on sign-in, `/health` and OpenAPI. Each action declares its roles from PRD 4.2 with `[Authorize(Roles = Roles.WarehouseOrManager)]`, where a comma in the roles string means "any of" (AC-05.2). A custom `IAuthorizationMiddlewareResultHandler` writes the `403` ProblemDetails body naming the roles, taken from the failed `RolesAuthorizationRequirement`.
- **First Manager and start-up checks** (T-003, US-02, PRD row 18). In `Program.cs`, before the app listens: (1) tolerance 0–100; (2) `JWT_SIGNING_KEY` set and at least 32 characters; (3) migrations; (4) the three roles exist, created if missing; (5) if the database has no users, `ADMIN_EMAIL` and `ADMIN_PASSWORD` must be set and the password must pass the policy, and then one active user is created with the Manager role only. If any users exist, nothing about users is touched (AC-02.2). A failed check throws `StartupException` with the exact message from design 7.4. Program catches it, logs it at Critical level, and exits with code 1 without listening.
- **Validation.** Hand-written in each service, using `Common/Validation/ValidationErrors`. The collector gathers messages keyed by camelCase JSON path (`lines[0].quantity`) and throws one `ValidationException`. Shared helpers: `Quantity.HasAtMostDecimals(value, 3)` (true when `Math.Round(value, n) == value`, so trailing zeros do not count), `Quantity.Round3` (`MidpointRounding.AwayFromZero`), the input maximums `Quantity.IsWithinMax` / `IsWithinMaxAmountPerUnit` with their messages `AboveMaxMessage`, `BelowMinMessage` and `AmountPerUnitAboveMaxMessage` (section 3.1, decisions #80, #103), `Text.Clean` (trim; empty becomes null), and for order bodies `Features/ProductionOrders/OrderLineMatcher` (item required, item on the order, no duplicates, the missing-lines message; options in `OrderLineRules`), used by the send, the completion, the complementary request and the complementary send (decisions #100, #105g, #106b). Each field gets at most one message, from the first rule it fails (decision #75). The collector keeps at most `ValidationErrors.MaxMessages` (100) messages; later ones are only counted, and `Detail` ends with the "{n} more problems are not shown." sentence, which `ToDictionary` (the `errors` field) leaves out (decision #122). Size limits live in `Common/Validation/RequestLimits`: `MaxBodyBytes` (1 MB, applied to Kestrel and IIS in `Program.cs`), and `MaxLines` (500) and `MaxRoles` (10) with their checks `CheckLineCount` and `CheckRoleCount`, which every service with a `lines` or `roles` body calls first, before its write transaction (section 4.1 item 12, section 5.2). `[ApiController]`'s automatic `400` stays on only for unreadable bodies (section 4.1 item 2), with a custom `InvalidModelStateResponseFactory` that produces the design's body. Request records carry no data annotations, so all other validation happens in the service, in the design's check order.
- **Error handling** (T-001). Services throw `ApiException` subclasses: `NotFoundException`, `ConflictException`, `BadRequestException`, `ValidationException`. Each carries a status, a `code` and a `detail`, plus optional field `errors`. A `code` used by more than one feature is a constant in `Common/Errors/ErrorCodes.cs` (`order-cancelled`, `material-in-use`, `too-many-lines`, and the framework codes, including `request-too-large` and `request-timeout`); a code used by one feature may be a constant in that feature (for example `StockLedger.InsufficientStockCode`). One `IExceptionHandler` turns them into ProblemDetails through `IProblemDetailsService`, adding `code`, the fixed `title` per status (design 6; `ProblemResponse` holds them, including "Request timeout" for `408` and "Request too large" for `413`) and `traceId`. A `BadHttpRequestException`, which Kestrel throws when model binding starts reading a body it will not deliver, is mapped by its own `StatusCode`: `413` → `413` `request-too-large`, `408` → `408` `request-timeout`, anything else → `400` `validation` "The request body could not be read.", none with `errors` (section 4.1 item 2, decision #124). Unknown exceptions become `500` `internal-error` and are logged with their stack trace. A `DbUpdateException` from a UNIQUE or PRIMARY KEY constraint becomes `409` `concurrent-change`; any other `DbUpdateException` is a `500` (section 4.1 item 11, decision #46). `UseStatusCodePages` covers bodiless `404` and `405` responses. Number formatting in messages follows design 6.1 through `Common/Quantities/MessageFormat` (invariant culture, `#,0.###`, percentages with a sign and two places).
- **JSON** (skeleton, `Program.cs`). camelCase; string enums; `NumberHandling = Strict` (strings for numbers refused); null fields always written.
- **Time.** Services get the time from `Clock` (T-001), which wraps .NET's `TimeProvider` and truncates to whole seconds. Tests replace `TimeProvider` with `FakeTimeProvider` to test token expiry, lock-out and newest-first ordering.
- **Logging.** Built-in `Microsoft.Extensions.Logging` to the console. Information for start-up and each state change, with the record's number and the user id: order sent, completed, cancelled; complementary order requested, approved, rejected, sent; return instructed, received, cancelled; sign-in, user created, roles set, deactivated, reactivated, password changed or set. Order, complementary-order and return changes are logged only after their write transaction has committed, so the log never reports one that was rolled back; new code follows this rule. (As built, the user and password services log inside the transaction, just before it commits; a failed commit there could leave a log line for a change that was not saved. Low impact; noted for a later tidy-up, not a v1 task.) Receipts and adjustments are not logged: the movement itself is the record (decision #103). Warning for `concurrent-change`. Error for `500`, with the stack trace. A refused body (`413`, `408`, or the unreadable-body `400`, section 4.1 item 2) is one Information line from `ApiExceptionHandler`, with the status, method, path and Kestrel's reason, and no stack trace: anyone can send such requests without signing in, and before SEC-02 each one wrote about 27 lines at Error. The release configuration may raise the level of the category `InventoryTracker.Api.Common.Errors.ApiExceptionHandler` to Warning to drop those lines; its Warning and Error lines stay (decision #126, optional for localhost-only v1). Passwords, tokens, the signing key and request bodies are never logged. EF SQL logging is at Warning.
- **Configuration.** Non-secret settings are in `appsettings.json`. Secrets come only from environment variables, or from `dotnet user-secrets` in Development. Both feed the same configuration keys. Nothing secret is in a tracked file (PRD section 5, AC-02.5).

| Key | Kind | Default | Notes |
|-----|------|---------|-------|
| `ADMIN_EMAIL` | secret | none | used only when there are no users |
| `ADMIN_PASSWORD` | secret | none | used only when there are no users; must pass the policy |
| `JWT_SIGNING_KEY` | secret | none | at least 32 characters; required to start |
| `ConnectionStrings__Default` | setting | `Data Source=inventory.db` | a relative path resolves to `src/InventoryTracker.Api/` |
| `Inventory__TolerancePercent` | setting | `2` | 0 to 100, else start-up stops (PRD section 5) |
| `Jwt__Issuer`, `Jwt__Audience` | setting | `InventoryTracker` | |
| `Jwt__TokenLifetimeHours` | setting | `8` | AC-01.2 |
| `ASPNETCORE_ENVIRONMENT` | setting | `Development` via launch profile | user-secrets and OpenAPI are Development-only |

(In `appsettings.json` the double underscore is written as a section, for example `"Inventory": { "TolerancePercent": 2 }`.)

- **OpenAPI document** (T-021, decision #110). `/openapi/v1.json` (Development only) is generated from the code. Each controller action declares its success status and its `409` codes with `[ProducesResponseType]`, so the document cannot drift from the code. Shared rules in `Common/OpenApi` add the Bearer scheme, the `400`/`401`/`403`/`404` ProblemDetails responses, `413` on every operation that reads a body (SEC-01, SEC-02), decimal quantities as numbers and enum names. A request field is shown as required unless its record parameter has a default: **every new optional request field must be declared with `= null`** (for example `string? Note = null`), or the document marks it required. `PageQuery.Skip` is `[BindNever]` and hidden. The minimal-API JSON options copy the controllers' number and enum settings so the document matches; this has no effect on error bodies.
- **Code comments.** The owner's comment standard (CLAUDE.md) applies to every file. `GenerateDocumentationFile` is on, so the build warns (CS1591) about any public member without a `///` comment. The build must have 0 warnings.

## 7. Testing strategy

- **Framework:** xUnit v3 (4.0.3) on Microsoft.Testing.Platform (selected in `global.json`), with plain `Assert`. One test project: `tests/InventoryTracker.Api.Tests`.
- **Unit tests** (`Unit/`): pure code with no database. These cover the D-3 to D-9 calculations with every PRD worked example (AC-15.2 to AC-15.5, AC-18.2, AC-20.4 to AC-20.8), rounding and decimal-place rules, message formatting, and notification messages.
- **Integration tests** (`Integration/<Feature>Tests.cs`): HTTP calls against the real app through `TestApiFactory` (`WebApplicationFactory<Program>`). Each factory gets its own temporary SQLite file, not in-memory, so locking behaves as in production. Users are created through the API or through test helpers, and tests sign in with real tokens. Every acceptance criterion a task lists gets at least one test. The test's comment names the criterion (`/// AC-15.3: ...`). Each endpoint's tests include its `401` and `403` cases.
- **Concurrency tests:** fire conflicting requests with `Task.WhenAll` on separate `HttpClient`s and assert that exactly one succeeds and stock is never negative (T-010, T-020). Because a plain `Task.WhenAll` usually lets one request finish first, the race tests also **force each ordering**: `Infrastructure/ForcedOrder` and the interceptors (`BeforeSaveInterceptor`, `AfterFirstReadInterceptor`) hold one request at a chosen point (for example before it writes to `Movements`) until the other has run, so both "A first" and "B first" are proved every run (decisions #105h, #109d).
- **Request-limit tests** (SEC-01, SEC-02). The in-memory `TestServer` behind `TestApiFactory` has no body size limit, so it cannot show the 1 MB limit. `Integration/RequestBodyLimitTests` therefore starts the same app on real Kestrel, on a free local port (its nested `KestrelApiFactory`, `UseKestrel(0)`), and writes raw HTTP over a TCP socket, because `HttpClient` cannot send a `Content-Length` that lies or a malformed chunk. It checks the `413`, `400` and log-level outcomes. `RequestLinesLimitTests` and `RequestRolesLimitTests` prove the caps run before the write transaction with `Infrastructure/HeldWriteLock`, which holds SQLite's write lock from the test's own connection (`BEGIN IMMEDIATE`, then `ROLLBACK`, so nothing changes): the over-cap request must still be answered, while a control request at the cap is shown to be waiting for the lock. New tests of "refused before the transaction" use this helper, not a copy.
- **Test helpers are shared, not copied** (decision #101). Order, complementary-order and return set-ups live in `Infrastructure/OrderFlow` (imported with `using static`); expected messages are typed out literally in `Infrastructure/ExpectedMessages`, never built from the code under test (decision #88). Some tests deliberately insert rows, including movements, straight through the DbContext to build a state no endpoint can produce; that is allowed in tests only (decision #112).
- **Role matrix** (`Integration/Scenarios/RoleMatrixTests.cs`, decision #109b): compares its table of all 48 endpoints with ASP.NET Core's `EndpointDataSource` (not the OpenAPI document, which exists in Development only) and checks every allowed and refused role, so a new or changed endpoint fails it until the table is updated.
- **Performance tests:** two tests fill a database with 100,000 movements and assert a 95th percentile under 1 s for `GET /stock`, `GET /stock/{itemId}`, `GET /movements` with each filter and a deep page, and `GET /movements/{id}` (PRD section 5). They run in the xUnit collection `PerformanceCollection`, alone, after the parallel tests (decision #109a).
- **Acceptance tests:** written by `qa-engineer` in phase 5. Suggested location: `tests/InventoryTracker.Api.Tests/Acceptance/`, so the one command still runs everything.
- **Run everything:** `dotnet test` from the repository root. The full suite takes about 1.5 to 2 minutes on the owner's machine, mostly because each test factory migrates a fresh database (decision #109f). Two runs at the same time in one working tree must use separate `--artifacts-path` folders, or they lock each other's `bin/` files (decision #67).

## 8. Running locally

Prerequisites: .NET SDK 10.0.x (10.0.401 confirmed on this machine). Nothing else. Commands are for PowerShell on Windows, from the repository root.

```powershell
# once per machine: install the pinned dotnet-ef tool
dotnet tool restore

# once per machine: store the secrets outside the repository (Development only)
dotnet user-secrets set ADMIN_EMAIL "owner@example.com" --project src/InventoryTracker.Api
dotnet user-secrets set ADMIN_PASSWORD "Choose-A-Strong1" --project src/InventoryTracker.Api
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

Outside Development, set the same names as environment variables instead (for example `$env:JWT_SIGNING_KEY = "..."`).

- Start again with an empty database: stop the app and delete `src/InventoryTracker.Api/inventory.db*`.
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
9. **Sign-in lock-out assumes one process** (section 5.2, decision #61). The per-account sign-in lock is in memory. If the API ever runs as several instances (behind a load balancer, or after a move to PostgreSQL), simultaneous wrong passwords sent to different instances can again lose failure counts and get past the lock-out. Before running more than one instance, move the serialisation into the database (for example `SELECT ... FOR UPDATE` on the user row, or `pg_advisory_xact_lock` keyed on the user, inside the sign-in steps). The same applies to anything else that relies on one process.
10. **Notification messages have no length limit** (decision #63). A notification for an order with many lines is long, and is returned whole by `GET /notifications`. This is accepted: the alternative loses facts. The 500-line cap (decision #123) bounds each message at roughly 90,000 characters, and paging (at most 100 per page) bounds the response size.
11. **Gap between the token check and the write transaction** (accepted, decision #83; T-014 review finding 3). The token, including the user's active flag and security stamp, is checked once, when the request is authenticated (`OnTokenValidated`, section 6). The write transaction starts later, and may wait up to 30 seconds for SQLite's write lock under load. If a Manager deactivates the user, resets their password, or the user changes it elsewhere, in that interval, a request that already passed the check still completes. Every later request with that token gets `401`. Accepted because the window is short, the user was entitled to act when the request arrived, and the result is the same as if the request had arrived a moment earlier. Change-password closes the gap for itself, because it relies on the old password (section 5.2). If the owner ever needs the gap closed everywhere, change one place: `InWriteTransactionAsync` re-reads the caller's `IsActive` and `SecurityStamp` after `BEGIN` and throws `401` `not-authenticated` when either has changed.
12. **Extreme values under the input maximums** (decision #80). Two consequences of the limits are accepted. (a) An order whose planned amount for a material is above 999,999,999.999 (possible with a large amount per unit times a large ordered quantity) can be created, but unless enough leftover is applied its send range cannot be met by any allowed send quantity, so it can only be cancelled. No real factory value is near this. (b) Stock is a sum and is not capped. Only about a million maximum-size movements of one item could take a total, or a value stored from it, past the `decimal(18,3)` capacity. C# `decimal` and PostgreSQL `numeric` sums do not overflow at that size, so only storing such a value (`CarriedLeftover`, or a `ConsumedInProduction` movement, which the ledger then refuses with a `500`) could fail.
