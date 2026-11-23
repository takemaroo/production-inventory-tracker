using System.Globalization;
using System.Net;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// (v1.1) T-022, AC-30.3 ("no other data changes"), architecture 3.4 and 7, risk 15: version 1.1 starting on a
/// version 1 database upgrades it with the <c>SalesOrders</c> migration and changes nothing that was there.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never touches the owner's database.</b> <see cref="UpgradedV1Database"/> builds its own version 1 database on
/// a temporary file, from the version 1 migrations (up to <c>PercentagePrecision</c>), and fills it with plain SQL: the
/// v1.1 <c>AppDbContext</c> cannot write v1 rows, because its model already has the new <c>Movements</c> column. Then
/// it starts the real app on that file, which applies the pending migration exactly as the owner's first v1.1 start
/// will.
/// </para>
/// <para>
/// The fixture records the database before and right after the start, before any test runs, so a test that signs in
/// (and so may touch a user row) cannot disturb what another test compares. T-023 extends this class with the Seller
/// role checks; at that point <c>AspNetRoles</c> gains one row on purpose.
/// </para>
/// </remarks>
public sealed class UpgradeTests(UpgradeTests.UpgradedV1Database upgraded) : IClassFixture<UpgradeTests.UpgradedV1Database>
{
    /// <summary>The last version 1 migration: the owner's database is at this point before the first v1.1 start.</summary>
    public const string LastV1Migration = "20261114053033_PercentagePrecision";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Architecture 3.4 step 3: the one statement that adds the new column, its foreign key included, in place.</summary>
    private const string AddColumnInPlace =
        "ALTER TABLE \"Movements\" ADD \"SalesOrderNumber\" INTEGER NULL CONSTRAINT \"FK_Movements_SalesOrders_SalesOrderNumber\" REFERENCES \"SalesOrders\" (\"Number\") ON DELETE RESTRICT;";

    /// <summary>AC-30.3, decision #154: after the first start, SalesOrders is the last applied migration and nothing is pending.</summary>
    [Fact]
    public async Task Start_applies_the_SalesOrders_migration_and_leaves_nothing_pending()
    {
        Assert.EndsWith("_PercentagePrecision", upgraded.Before.AppliedMigrations[^1]);
        Assert.DoesNotContain(upgraded.Before.AppliedMigrations, m => m.EndsWith("_SalesOrders"));

        await using var scope = upgraded.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var applied = db.Database.GetAppliedMigrations().ToList();

        Assert.Equal(5, applied.Count);
        Assert.EndsWith("_SalesOrders", applied[^1]);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancel));
        Assert.Equal(upgraded.Before.AppliedMigrations, applied.Take(4)); // the v1 history rows are untouched

        // The three new tables exist, and the upgrade put nothing in them.
        Assert.Equal(0, await db.Customers.CountAsync(Cancel));
        Assert.Equal(0, await db.SalesOrders.CountAsync(Cancel));
        Assert.Equal(0, await db.SalesOrderLines.CountAsync(Cancel));
        Assert.Equal(0, await db.Movements.CountAsync(m => m.SalesOrderNumber != null, Cancel));
    }

    /// <summary>
    /// AC-30.3: every version 1 table has the same number of rows, and every row the same value in every version 1
    /// column, after the upgrade. The counts are checked against what the fixture inserted too, so the comparison is
    /// not of two empty tables.
    /// </summary>
    /// <remarks>
    /// From T-023 (decision #155) the first start also seeds the Seller role. This test must then expect
    /// <c>AspNetRoles</c> as before plus exactly one Seller row, and every other table still identical.
    /// </remarks>
    [Fact]
    public void Every_v1_table_keeps_its_rows_and_values()
    {
        var before = upgraded.Before.Tables;
        var after = upgraded.After.Tables;

        // What the fixture put in: three roles, three users with four role links, a unit, three items, 304 movements, a
        // Completed order with one line, a Pending return and a notification.
        Assert.Equal(3, before["AspNetRoles"].Count);
        Assert.Equal(3, before["AspNetUsers"].Count);
        Assert.Equal(4, before["AspNetUserRoles"].Count);
        Assert.Single(before["Units"]);
        Assert.Equal(3, before["Items"].Count);
        Assert.Equal(UpgradedV1Database.MovementCount, before["Movements"].Count);
        Assert.Single(before["ProductionOrders"]);
        Assert.Single(before["ProductionOrderLines"]);
        Assert.Single(before["Returns"]);
        Assert.Single(before["Notifications"]);

        Assert.All(before, table =>
        {
            Assert.Equal(table.Value.Count, after[table.Key].Count);
            Assert.Equal(table.Value, after[table.Key]); // row by row, column by column
        });
    }

    /// <summary>
    /// AC-30.3, sampled values: one movement's every column, read after the upgrade, is exactly what version 1 stored,
    /// and its new column is null. A user with two roles still has exactly those two.
    /// </summary>
    [Fact]
    public async Task A_sampled_movement_and_a_users_roles_are_unchanged()
    {
        await using var connection = await upgraded.OpenAsync();

        var movement = await ReadRowAsync(connection, "SELECT * FROM \"Movements\" WHERE \"Id\" = 1");
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["Id"] = 1L,
                ["Type"] = "Received",
                ["ItemId"] = (long)UpgradedV1Database.FlourId,
                ["WarehouseChange"] = "10.5",
                ["ProductionStorageChange"] = null,
                ["OccurredAt"] = "2026-11-01 08:00:00",
                ["UserId"] = UpgradedV1Database.WarehouseUserId,
                ["ProductionOrderNumber"] = null,
                ["ComplementaryOrderNumber"] = null,
                ["ReturnNumber"] = null,
                ["ReceiptDate"] = "2026-11-01",
                ["Note"] = "Delivery note 1",
                ["Reason"] = null,
                ["SalesOrderNumber"] = null, // the new column: no v1 movement belongs to a sales order
            },
            movement);

        var roles = await ReadStringsAsync(connection, $"""
            SELECT r."Name" FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE ur."UserId" = '{UpgradedV1Database.ProductionUserId}' ORDER BY r."Name"
            """);
        Assert.Equal([Roles.Production, Roles.Warehouse], roles);
    }

    /// <summary>
    /// Architecture 3.4, risk 15: <c>sqlite_sequence</c> (the last id each AUTOINCREMENT table handed out) is unchanged,
    /// for <c>Movements</c> above all: the next movement id follows the last v1 one, and no id is used twice.
    /// </summary>
    [Fact]
    public void Sqlite_sequence_is_unchanged()
    {
        Assert.Equal(UpgradedV1Database.MovementCount, upgraded.Before.Sequences["Movements"]);
        Assert.Equal(upgraded.Before.Sequences, upgraded.After.Sequences);
    }

    /// <summary>
    /// Architecture 3.4: the upgraded database passes SQLite's own foreign key check, and the new
    /// <c>Movements.SalesOrderNumber</c> key to <c>SalesOrders</c> (Restrict) sits beside the five version 1 keys.
    /// </summary>
    [Fact]
    public async Task Foreign_keys_hold_and_movements_reference_sales_orders()
    {
        await using var connection = await upgraded.OpenAsync();

        // Each row PRAGMA foreign_key_check returns is a row whose foreign key points at nothing.
        Assert.Empty(await ReadStringsAsync(connection, "PRAGMA foreign_key_check"));

        var keys = await ReadStringsAsync(connection,
            "SELECT \"table\" || '.' || \"from\" || '->' || \"to\" || ' ' || on_delete FROM pragma_foreign_key_list('Movements')");
        Assert.Equal(upgraded.Before.MovementForeignKeys.Count + 1, keys.Count);
        Assert.All(upgraded.Before.MovementForeignKeys, key => Assert.Contains(key, keys));
        Assert.Contains("SalesOrders.SalesOrderNumber->Number RESTRICT", keys);
    }

    /// <summary>
    /// Architecture 3.4, risk 15: all eight <c>Movements</c> indexes exist: the seven of version 1 (a table rebuild that
    /// stopped part-way could lose them) and <c>IX_Movements_OneShipmentPerLine</c>, unique and filtered.
    /// </summary>
    [Fact]
    public async Task All_eight_movement_indexes_exist()
    {
        string[] v1Indexes =
        [
            "IX_Movements_ComplementaryOrderNumber", "IX_Movements_ItemId", "IX_Movements_OccurredAt",
            "IX_Movements_ProductionOrderNumber", "IX_Movements_ReturnNumber", "IX_Movements_Type", "IX_Movements_UserId",
        ];
        Assert.Equal(v1Indexes, upgraded.Before.MovementIndexes);

        await using var connection = await upgraded.OpenAsync();
        var indexes = await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Movements' AND sql IS NOT NULL ORDER BY name");
        Assert.Equal(v1Indexes.Append("IX_Movements_OneShipmentPerLine").Order(StringComparer.Ordinal), indexes);

        var oneShipment = Assert.Single(await ReadStringsAsync(connection,
            "SELECT sql FROM sqlite_master WHERE name = 'IX_Movements_OneShipmentPerLine'"));
        Assert.Equal(
            "CREATE UNIQUE INDEX \"IX_Movements_OneShipmentPerLine\" ON \"Movements\" (\"SalesOrderNumber\", \"ItemId\") WHERE \"SalesOrderNumber\" IS NOT NULL",
            oneShipment);
    }

    /// <summary>
    /// Architecture 7: on the upgraded database the new rules work. A movement naming a sales order that does not exist
    /// is refused (foreign key), and a second movement for the same sales order and item is refused (filtered unique
    /// index). Done in a transaction that is rolled back, so the other tests still see the version 1 data.
    /// </summary>
    [Fact]
    public async Task New_rules_work_on_the_upgraded_database()
    {
        await using var connection = await upgraded.OpenAsync();
        await ExecuteAsync(connection, "BEGIN;");
        try
        {
            await AssertNewRulesAsync(connection);
        }
        finally
        {
            await ExecuteAsync(connection, "ROLLBACK;");
        }
    }

    private static async Task AssertNewRulesAsync(SqliteConnection connection)
    {
        var unknown = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, InsertShipmentSql(salesOrderNumber: 999)));
        Assert.Contains("FOREIGN KEY constraint failed", unknown.Message);

        await ExecuteAsync(connection, $"""
            INSERT INTO "Customers" ("Name", "NameNormalized", "IsActive", "CreatedAt", "CreatedById")
            VALUES ('Deli Norte', 'DELI NORTE', 1, '2026-11-24 09:00:00', '{UpgradedV1Database.ManagerUserId}');
            INSERT INTO "SalesOrders" ("CustomerId", "Status", "CreatedAt", "CreatedById", "StatusChangedAt")
            VALUES (last_insert_rowid(), 'Shipped', '2026-11-24 09:00:00', '{UpgradedV1Database.ManagerUserId}', '2026-11-24 09:00:00');
            """);
        var salesOrderNumber = Assert.Single(await ReadStringsAsync(connection, "SELECT CAST(last_insert_rowid() AS TEXT)"));
        Assert.Equal("1", salesOrderNumber); // D-10: its own sequence, though the database already holds production order #1

        await ExecuteAsync(connection, InsertShipmentSql(salesOrderNumber: 1));
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, InsertShipmentSql(salesOrderNumber: 1)));
        Assert.Contains("UNIQUE constraint failed: Movements.SalesOrderNumber, Movements.ItemId", duplicate.Message);
    }

    /// <summary>
    /// Risk 15: the SQL that EF Core generates for the upgrade from the last version 1 migration has no table rebuild.
    /// If someone deletes and regenerates the SalesOrders migration, EF Core's <c>ef_temp_Movements</c> copy and its
    /// <c>PRAGMA</c> lines come back and this fails. The hand-edited statement is there, inside the one transaction.
    /// </summary>
    [Fact]
    public async Task Upgrade_script_alters_movements_in_place_in_one_transaction()
    {
        await using var scope = upgraded.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // IMigrator.GenerateScript writes the SQL that "dotnet ef migrations script PercentagePrecision" prints.
        var script = db.GetService<IMigrator>().GenerateScript(fromMigration: LastV1Migration);

        Assert.DoesNotContain("ef_temp_", script);
        Assert.DoesNotContain("PRAGMA", script);
        Assert.Equal(1, Occurrences(script, "BEGIN TRANSACTION;"));
        Assert.Equal(1, Occurrences(script, "COMMIT;"));
        Assert.Equal(1, Occurrences(script, AddColumnInPlace));

        // The history row is written inside the transaction, so the migration is recorded only if all of it worked.
        var history = script.IndexOf("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal);
        Assert.InRange(history, script.IndexOf(AddColumnInPlace, StringComparison.Ordinal), script.IndexOf("COMMIT;", StringComparison.Ordinal));
    }

    /// <summary>
    /// AC-30.3, AC-10.1: a version 1 user still signs in with their password, and <c>GET /stock</c> shows the version 1
    /// figures, calculated from the version 1 movements, order and return.
    /// </summary>
    [Fact]
    public async Task Stock_shows_the_v1_figures()
    {
        using var client = upgraded.App.CreateClientWithToken(await upgraded.App.SignInAsync(UpgradedV1Database.WarehouseEmail));

        var response = await client.GetAsync("/api/v1/stock", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await ApiJson.ReadAsync(response)).GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("itemId").GetInt32());

        Assert.Equal(3, rows.Count);

        // Flour: 300 receipts of 10.5 (3,150), 1,000 sent, 2.5 written off; in production storage 1,000 sent, 950 used.
        // The Pending return of 20 is taken off free leftover (D-4), and no order is InProduction.
        var flour = rows[UpgradedV1Database.FlourId];
        Assert.Equal(2147.5m, flour.GetProperty("warehouse").GetDecimal());
        Assert.Equal(50m, flour.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(0m, flour.GetProperty("committedToOrder").GetDecimal());
        Assert.Equal(20m, flour.GetProperty("pendingReturns").GetDecimal());
        Assert.Equal(30m, flour.GetProperty("freeLeftover").GetDecimal());

        // Bread: the 480 bags order #1 produced.
        var bread = rows[UpgradedV1Database.BreadId];
        Assert.Equal(480m, bread.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, bread.GetProperty("productionStorage").GetDecimal());
        Assert.Equal(JsonValueKind.Null, bread.GetProperty("freeLeftover").ValueKind);

        // Salt: never moved.
        var salt = rows[UpgradedV1Database.SaltId];
        Assert.Equal(0m, salt.GetProperty("warehouse").GetDecimal());
        Assert.Equal(0m, salt.GetProperty("productionStorage").GetDecimal());
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>A shipment-shaped movement of Bread for <paramref name="salesOrderNumber"/>, in plain SQL.</summary>
    private static string InsertShipmentSql(int salesOrderNumber) => $"""
        INSERT INTO "Movements" ("Type", "ItemId", "WarehouseChange", "OccurredAt", "UserId", "SalesOrderNumber")
        VALUES ('Adjustment', {UpgradedV1Database.BreadId}, '-1.0', '2026-11-24 10:00:00', '{UpgradedV1Database.WarehouseUserId}', {salesOrderNumber})
        """;

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Cancel);
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var values = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>One row as column name → raw SQLite value (long, string, double or null), exactly as stored.</summary>
    private static async Task<Dictionary<string, object?>> ReadRowAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        Assert.True(await reader.ReadAsync(Cancel));
        return Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    }

    /// <summary>
    /// A version 1 database on a temporary file, filled with version 1 rows, recorded, then upgraded by starting the
    /// real app on it (architecture 7, v1.1). Shared by the tests of <see cref="UpgradeTests"/>.
    /// </summary>
    /// <remarks>
    /// xUnit creates one instance per test class (a <em>class fixture</em>) and, because it implements
    /// <see cref="IAsyncLifetime"/>, awaits <see cref="InitializeAsync"/> before the first test and
    /// <see cref="DisposeAsync"/> after the last.
    /// </remarks>
    public sealed class UpgradedV1Database : IAsyncLifetime
    {
        /// <summary>Movements the fixture inserts: 300 receipts, a send, a consumption, an output and an adjustment.</summary>
        public const int MovementCount = 304;

        /// <summary>Ids of the v1 users. Identity's ids are GUID strings; fixed ones keep the SQL readable.</summary>
        public const string ManagerUserId = "00000000-0000-0000-0000-00000000000a";

        /// <inheritdoc cref="ManagerUserId"/>
        public const string WarehouseUserId = "00000000-0000-0000-0000-00000000000b";

        /// <inheritdoc cref="ManagerUserId"/>
        public const string ProductionUserId = "00000000-0000-0000-0000-00000000000c";

        /// <summary>Sign-in email of the Warehouse-only v1 user.</summary>
        public const string WarehouseEmail = "warehouse@v1.test";

        /// <summary>Item ids: the v1 database is empty before the fixture fills it, so they are 1, 2 and 3.</summary>
        public const int FlourId = 1;

        /// <inheritdoc cref="FlourId"/>
        public const int BreadId = 2;

        /// <inheritdoc cref="FlourId"/>
        public const int SaltId = 3;

        /// <summary>The app, started on the upgraded file. Its <c>DisposeAsync</c> deletes the file.</summary>
        public TestApiFactory App { get; } = new();

        /// <summary>The database as version 1 left it, before the v1.1 app first started.</summary>
        public DatabaseState Before { get; private set; } = null!;

        /// <summary>The same database right after the first v1.1 start, before any test ran.</summary>
        public DatabaseState After { get; private set; } = null!;

        /// <summary>A new connection to the database file, with foreign keys enforced, as the app's own connections are.</summary>
        public async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = App.DatabasePath,
                ForeignKeys = true,
                Pooling = false, // so no connection is left open on the file when the fixture deletes it
            }.ToString());
            await connection.OpenAsync(Cancel);
            return connection;
        }

        /// <summary>Builds and fills the v1 database, records it, starts the app (which upgrades it), and records it again.</summary>
        public async ValueTask InitializeAsync()
        {
            await MigrateToVersion1Async();
            await using (var connection = await OpenAsync())
            {
                await ExecuteAsync(connection, Version1RowsSql());
                Before = await DatabaseState.ReadAsync(connection);
            }

            // Starts the app, as the owner's first v1.1 start does: migrations, then the role and first-Manager step.
            App.CreateClient().Dispose();

            await using (var connection = await OpenAsync())
            {
                After = await DatabaseState.ReadAsync(connection, Before.Tables.Keys);
            }
        }

        /// <summary>Stops the app and deletes the temporary database.</summary>
        public ValueTask DisposeAsync() => App.DisposeAsync();

        /// <summary>
        /// Creates the file with exactly the version 1 schema: every migration up to and including
        /// <see cref="LastV1Migration"/>, through EF Core's <see cref="IMigrator"/>, so the schema is what version 1 made.
        /// </summary>
        private async Task MigrateToVersion1Async()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={App.DatabasePath};Pooling=False").Options;
            await using var db = new AppDbContext(options);
            await db.GetService<IMigrator>().MigrateAsync(LastV1Migration, Cancel);
        }

        /// <summary>
        /// The version 1 rows, in plain SQL and in the formats EF Core writes (times as "yyyy-MM-dd HH:mm:ss" UTC,
        /// decimals and dates as text, enums as names). The passwords are real hashes of <see cref="TestApiFactory.UserPassword"/>,
        /// so a v1 user can still sign in after the upgrade.
        /// </summary>
        private static string Version1RowsSql()
        {
            var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), TestApiFactory.UserPassword);
            const string dataJson = """{"orderNumber":1}""";
            return $"""
                INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp") VALUES
                    ('role-w', 'Warehouse', 'WAREHOUSE', 'stamp-w'),
                    ('role-p', 'Production', 'PRODUCTION', 'stamp-p'),
                    ('role-m', 'Manager', 'MANAGER', 'stamp-m');

                INSERT INTO "AspNetUsers" ("Id", "IsActive", "CreatedAt", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                    "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumberConfirmed", "TwoFactorEnabled",
                    "LockoutEnabled", "AccessFailedCount") VALUES
                    ('{ManagerUserId}', 1, '2026-11-01 07:00:00', 'manager@v1.test', 'MANAGER@V1.TEST', 'manager@v1.test', 'MANAGER@V1.TEST',
                        0, '{hash}', 'SECURITY-STAMP-A', 'concurrency-a', 0, 0, 1, 0),
                    ('{WarehouseUserId}', 1, '2026-11-01 07:05:00', '{WarehouseEmail}', '{WarehouseEmail.ToUpperInvariant()}', '{WarehouseEmail}',
                        '{WarehouseEmail.ToUpperInvariant()}', 0, '{hash}', 'SECURITY-STAMP-B', 'concurrency-b', 0, 0, 1, 0),
                    ('{ProductionUserId}', 1, '2026-11-01 07:10:00', 'production@v1.test', 'PRODUCTION@V1.TEST', 'production@v1.test',
                        'PRODUCTION@V1.TEST', 0, '{hash}', 'SECURITY-STAMP-C', 'concurrency-c', 0, 0, 1, 0);

                INSERT INTO "AspNetUserRoles" ("UserId", "RoleId") VALUES
                    ('{ManagerUserId}', 'role-m'),
                    ('{WarehouseUserId}', 'role-w'),
                    ('{ProductionUserId}', 'role-p'),
                    ('{ProductionUserId}', 'role-w');

                INSERT INTO "Units" ("Name", "NameNormalized") VALUES ('kg', 'KG');

                INSERT INTO "Items" ("Name", "NameNormalized", "Description", "Type", "UnitId", "CreatedAt") VALUES
                    ('Flour', 'FLOUR', 'Wheat flour, type 550', 'RawMaterial', 1, '2026-11-01 07:20:00'),
                    ('Bread', 'BREAD', NULL, 'FinishedProduct', 1, '2026-11-01 07:21:00'),
                    ('Salt', 'SALT', NULL, 'RawMaterial', 1, '2026-11-01 07:22:00');

                INSERT INTO "ProductionOrders" ("FinishedProductId", "OrderedQuantity", "Status", "CreatedAt", "CreatedById",
                    "SentAt", "SentById", "CompletedAt", "CompletedById", "StatusChangedAt", "ProducedQuantity", "OutputVariance",
                    "OutputVariancePercent", "ToleranceAtCompletionPercent", "CompletionReason") VALUES
                    ({BreadId}, '500.0', 'Completed', '2026-11-02 08:00:00', '{ManagerUserId}', '2026-11-02 09:00:00', '{WarehouseUserId}',
                        '2026-11-02 17:00:00', '{ProductionUserId}', '2026-11-02 17:00:00', '480.0', '-20.0', '-4.0', '2.0', 'Oven fault');

                INSERT INTO "ProductionOrderLines" ("OrderNumber", "LineNo", "RawMaterialId", "AmountPerUnit", "PlannedAmount",
                    "LeftoverApplied", "WarehouseSend", "CarriedLeftover", "ComplementarySends", "LeftoverEntered", "LeftoverRemaining",
                    "ActualUsage", "RequiredForActualOutput", "UsageVariance", "UsageVariancePercent", "UsageVarianceAboveTolerance") VALUES
                    (1, 1, {FlourId}, '2.0', '1000.0', '0.0', '1000.0', '0.0', '0.0', '50.0', '50.0', '950.0', '960.0', '-10.0',
                        '-1.041667', 0);

                INSERT INTO "Returns" ("ItemId", "Quantity", "Reason", "ProductionOrderNumber", "Status", "CreatedAt", "CreatedById") VALUES
                    ({FlourId}, '20.0', 'Leftover from order #1', 1, 'Pending', '2026-11-03 08:00:00', '{ManagerUserId}');

                INSERT INTO "Notifications" ("Type", "ProductionOrderNumber", "CreatedAt", "Message", "DataJson") VALUES
                    ('OutputVariance', 1, '2026-11-02 17:00:00', 'Order #1 produced 480 Bread; ordered 500 (-4.00%).', '{dataJson}');

                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300)
                INSERT INTO "Movements" ("Type", "ItemId", "WarehouseChange", "OccurredAt", "UserId", "ReceiptDate", "Note")
                SELECT 'Received', {FlourId}, '10.5', strftime('%Y-%m-%d %H:%M:%S', '2026-11-01 08:00:00', '+' || (i - 1) || ' minutes'),
                    '{WarehouseUserId}', '2026-11-01', 'Delivery note ' || i FROM n;

                INSERT INTO "Movements" ("Type", "ItemId", "WarehouseChange", "ProductionStorageChange", "OccurredAt", "UserId",
                    "ProductionOrderNumber", "Reason") VALUES
                    ('SentToProduction', {FlourId}, '-1000.0', '1000.0', '2026-11-02 09:00:00', '{WarehouseUserId}', 1, NULL),
                    ('ConsumedInProduction', {FlourId}, NULL, '-950.0', '2026-11-02 17:00:00', '{ProductionUserId}', 1, NULL),
                    ('ProducedOutput', {BreadId}, '480.0', NULL, '2026-11-02 17:00:00', '{ProductionUserId}', 1, NULL),
                    ('Adjustment', {FlourId}, '-2.5', NULL, '2026-11-03 10:00:00', '{ManagerUserId}', NULL, 'Spilled sack');
                """;
        }
    }

    /// <summary>
    /// What the upgrade tests compare, read straight from the file with SQL: every table's rows, the applied
    /// migrations, <c>sqlite_sequence</c>, and the indexes and foreign keys of <c>Movements</c>.
    /// </summary>
    public sealed class DatabaseState
    {
        /// <summary>Every row of every table, by table name, in rowid order. Each row is its column values written as text.</summary>
        public required Dictionary<string, List<string>> Tables { get; init; }

        /// <summary>The <c>__EFMigrationsHistory</c> ids, in order.</summary>
        public required List<string> AppliedMigrations { get; init; }

        /// <summary><c>sqlite_sequence</c>: the last id each AUTOINCREMENT table handed out.</summary>
        public required Dictionary<string, long> Sequences { get; init; }

        /// <summary>The names of the indexes on <c>Movements</c> (SQLite's own automatic ones excluded), sorted.</summary>
        public required List<string> MovementIndexes { get; init; }

        /// <summary>The foreign keys of <c>Movements</c>, as "table.from->to ON_DELETE".</summary>
        public required List<string> MovementForeignKeys { get; init; }

        /// <summary>
        /// Reads the state. <paramref name="tables"/> names the tables to read; null means every table in the file now.
        /// After the upgrade, the tests pass the version 1 tables, so both states hold the same tables.
        /// </summary>
        public static async Task<DatabaseState> ReadAsync(SqliteConnection connection, IEnumerable<string>? tables = null)
        {
            // The history table changes on purpose (one new row) and sqlite_sequence is compared on its own.
            var tableNames = tables?.ToList() ?? await ReadStringsAsync(connection,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'");

            var rows = new Dictionary<string, List<string>>();
            foreach (var table in tableNames)
            {
                rows[table] = await ReadTableAsync(connection, table);
            }

            var sequences = new Dictionary<string, long>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name, seq FROM sqlite_sequence";
                await using var reader = await command.ExecuteReaderAsync(Cancel);
                while (await reader.ReadAsync(Cancel))
                {
                    sequences[reader.GetString(0)] = reader.GetInt64(1);
                }
            }

            return new DatabaseState
            {
                Tables = rows,
                AppliedMigrations = await ReadStringsAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\""),
                Sequences = sequences,
                MovementIndexes = await ReadStringsAsync(connection,
                    "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Movements' AND sql IS NOT NULL ORDER BY name"),
                MovementForeignKeys = await ReadStringsAsync(connection,
                    "SELECT \"table\" || '.' || \"from\" || '->' || \"to\" || ' ' || on_delete FROM pragma_foreign_key_list('Movements')"),
            };
        }

        /// <summary>
        /// Every row of <paramref name="table"/>, oldest rowid first, as "column=value | column=value ..." text. The
        /// columns version 1.1 adds are left out, so a table is read the same way before and after the upgrade and any
        /// changed, added or lost row shows up as a difference.
        /// </summary>
        private static async Task<List<string>> ReadTableAsync(SqliteConnection connection, string table)
        {
            // Columns by name, not by position: this compares data, and a table rebuild (which risk 15's script test
            // catches) would reorder the columns without changing a value.
            var columns = await ReadStringsAsync(connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY name");
            columns = columns.Where(column => !V1_1Columns.Contains($"{table}.{column}")).ToList();

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))} FROM \"{table}\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync(Cancel);
            var rows = new List<string>();
            while (await reader.ReadAsync(Cancel))
            {
                rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i =>
                    $"{columns[i]}={(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))}")));
            }

            return rows;
        }

        /// <summary>Columns that version 1.1 adds to version 1 tables; they are not part of the before-and-after comparison.</summary>
        private static readonly HashSet<string> V1_1Columns = ["Movements.SalesOrderNumber"];
    }
}
