using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Data.Configurations;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-002: the database schema, the domain migration and the write transaction (architecture 3 and 5.2). (v1.1) T-022:
/// the sales order tables, the <c>SalesOrders</c> migration and their database rules.
/// </summary>
/// <remarks>
/// These tests talk to the database directly through <see cref="AppDbContext"/>, not over HTTP, because no
/// endpoint exists yet. Each test opens its own DI scope, which gives it its own DbContext and connection, as two
/// HTTP requests would have. Tests in this class share one database, so each one uses unique names.
/// </remarks>
public class DataModelTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly DateTime SampleTime = new(2026, 11, 11, 14, 5, 0, DateTimeKind.Utc);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Migrations ---------------------------------------------------------------------------------------

    /// <summary>
    /// T-002, T-011: on an empty database, start-up applies InitialIdentity, the single DomainModel migration, and then
    /// T-011's NotificationMessageUnbounded (decision #63) and PercentagePrecision (decision #82), in that order.
    /// (v1.1) T-022, decision #154: and last the one v1.1 migration, SalesOrders, which creates the three sales tables.
    /// </summary>
    [Fact]
    public async Task Startup_on_an_empty_database_applies_the_domain_migration()
    {
        await using var scope = CreateScope();
        var db = Db(scope);

        var applied = db.Database.GetAppliedMigrations().ToList();
        Assert.Equal(5, applied.Count);
        Assert.EndsWith("_InitialIdentity", applied[0]);
        Assert.EndsWith("_DomainModel", applied[1]);
        Assert.EndsWith("_NotificationMessageUnbounded", applied[2]);
        Assert.EndsWith("_PercentagePrecision", applied[3]);
        Assert.EndsWith("_SalesOrders", applied[4]);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancel));

        var tables = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(Cancel);
        string[] domainTables =
        [
            "Units", "Items", "Recipes", "RecipeLines", "Movements", "ProductionOrders", "ProductionOrderLines",
            "ComplementaryOrders", "ComplementaryOrderLines", "Returns", "Notifications", "NotificationReads",
            "Customers", "SalesOrders", "SalesOrderLines",
        ];
        Assert.All(domainTables, table => Assert.Contains(table, tables));
    }

    /// <summary>T-002: a database that already has only InitialIdentity (and a user) is upgraded at start-up, keeping its data.</summary>
    [Fact]
    public async Task Startup_upgrades_a_database_that_has_only_the_identity_migration()
    {
        await using var freshFactory = new TestApiFactory();
        var user = NewUser();

        // Build the "old" database by hand, before the app starts: apply InitialIdentity only, add a user.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={freshFactory.DatabasePath}")
            .Options;
        await using (var oldDb = new AppDbContext(options))
        {
            var initialIdentity = oldDb.Database.GetMigrations().Single(m => m.EndsWith("_InitialIdentity"));
            await oldDb.Database.GetService<IMigrator>().MigrateAsync(initialIdentity, Cancel);
            oldDb.Users.Add(user);
            await oldDb.SaveChangesAsync(Cancel);
            Assert.DoesNotContain(oldDb.Database.GetAppliedMigrations(), m => m.EndsWith("_DomainModel"));
        }

        freshFactory.CreateClient(); // starts the app, which applies the missing migration

        await using var scope = freshFactory.Services.CreateAsyncScope();
        var db = Db(scope);
        Assert.Contains(db.Database.GetAppliedMigrations(), m => m.EndsWith("_DomainModel"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancel));
        Assert.True(await db.Users.AnyAsync(u => u.Id == user.Id, Cancel));
        Assert.Equal(0, await db.Units.CountAsync(Cancel)); // the new tables exist and can be queried
    }

    /// <summary>
    /// T-011, decisions #63 and #82: a database created before T-011 (InitialIdentity and DomainModel only, with a
    /// notification already in it) is upgraded at start-up: both new migrations are applied, nothing is pending, and the
    /// old row is unchanged. A notification whose message is far longer than the old 1,000-character limit, with accented
    /// letters, quotes and a line break, is then saved and read back unchanged, character for character.
    /// </summary>
    [Fact]
    public async Task Startup_upgrades_a_database_created_before_the_completion_migrations()
    {
        await using var freshFactory = new TestApiFactory();
        var oldMessage = new string('a', 1000);
        int oldNotificationId;
        int orderNumber;

        // Build the "old" database by hand, before the app starts: migrate only up to DomainModel, then add rows.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={freshFactory.DatabasePath}")
            .Options;
        await using (var oldDb = new AppDbContext(options))
        {
            var domainModel = oldDb.Database.GetMigrations().Single(m => m.EndsWith("_DomainModel"));
            await oldDb.Database.GetService<IMigrator>().MigrateAsync(domainModel, Cancel);
            Assert.DoesNotContain(oldDb.Database.GetAppliedMigrations(), m => m.EndsWith("_NotificationMessageUnbounded"));
            Assert.DoesNotContain(oldDb.Database.GetAppliedMigrations(), m => m.EndsWith("_PercentagePrecision"));

            var user = await AddUserAsync(oldDb);
            var order = await AddOrderAsync(oldDb, await AddItemAsync(oldDb, ItemType.FinishedProduct), user, OrderStatus.Completed);
            orderNumber = order.Number;
            var old = NewNotification(orderNumber, oldMessage);
            oldDb.Notifications.Add(old);
            await oldDb.SaveChangesAsync(Cancel);
            oldNotificationId = old.Id;
        }

        freshFactory.CreateClient(); // starts the app, which applies the missing migrations

        await using (var scope = freshFactory.Services.CreateAsyncScope())
        {
            var db = Db(scope);
            var applied = db.Database.GetAppliedMigrations().ToList();
            Assert.Contains(applied, m => m.EndsWith("_NotificationMessageUnbounded"));
            Assert.Contains(applied, m => m.EndsWith("_PercentagePrecision"));
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancel));
            Assert.Equal(oldMessage, (await db.Notifications.SingleAsync(n => n.Id == oldNotificationId, Cancel)).Message);
        }

        // Decision #63: 2,600 characters, more than twice the old limit. Built from varied text so a truncation, a
        // trimmed end or a changed character anywhere would show.
        var longMessage = string.Concat(Enumerable.Range(0, 100).Select(i => $"Línea {i:000}: «Queso» \"x\"\n"))
            .PadRight(2600, 'é');
        Assert.Equal(2600, longMessage.Length);
        int longId;
        await using (var scope = freshFactory.Services.CreateAsyncScope())
        {
            var db = Db(scope);
            var notification = NewNotification(orderNumber, longMessage);
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(Cancel);
            longId = notification.Id;
        }

        await using (var scope = freshFactory.Services.CreateAsyncScope())
        {
            var stored = await Db(scope).Notifications.AsNoTracking().SingleAsync(n => n.Id == longId, Cancel);
            Assert.Equal(longMessage, stored.Message);
        }
    }

    /// <summary>
    /// T-011, decisions #63 and #82: both the model the app runs with and the migration snapshot (the model as of the
    /// last migration, which a PostgreSQL port would build its schema from) give the three stored percentages
    /// precision (28, 6), keep amounts per unit at (18, 6), and give Notification.Message no maximum length. And the
    /// migrations are complete: the model has no change that no migration records ((v1.1) T-022: including the sales
    /// tables, so the hand edit of the SalesOrders migration left the snapshot exactly as generated).
    /// </summary>
    [Fact]
    public async Task Model_and_snapshot_have_wide_percentages_and_unbounded_messages()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        // The snapshot class is internal to the API project; EF Core's migrations service hands it out.
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot
            ?? throw new InvalidOperationException("The API assembly has no model snapshot.");
        IReadOnlyModel[] models = [db.Model, snapshot.Model];

        foreach (var model in models)
        {
            var percentages = new[]
            {
                Property(model, typeof(ProductionOrder), nameof(ProductionOrder.OutputVariancePercent)),
                Property(model, typeof(ProductionOrder), nameof(ProductionOrder.ToleranceAtCompletionPercent)),
                Property(model, typeof(ProductionOrderLine), nameof(ProductionOrderLine.UsageVariancePercent)),
            };
            Assert.All(percentages, property =>
            {
                Assert.Equal(28, property.GetPrecision());
                Assert.Equal(6, property.GetScale());
            });

            var amountPerUnit = Property(model, typeof(ProductionOrderLine), nameof(ProductionOrderLine.AmountPerUnit));
            Assert.Equal(18, amountPerUnit.GetPrecision());
            Assert.Equal(6, amountPerUnit.GetScale());

            Assert.Null(Property(model, typeof(Notification), nameof(Notification.Message)).GetMaxLength());
            Assert.Null(Property(model, typeof(Notification), nameof(Notification.DataJson)).GetMaxLength());
        }

        // EF Core compares the code's model with the snapshot: true would mean a configuration change with no migration.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    /// <summary>T-002: the start-up warning "No instantiatable types implementing IEntityTypeConfiguration" (EF event 10632) is gone.</summary>
    [Fact]
    public async Task Startup_does_not_warn_about_missing_entity_configurations()
    {
        var logs = new CapturingLoggerProvider();
        await using var freshFactory = new TestApiFactory();
        await using var app = freshFactory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));

        app.CreateClient();

        // Proves the capture works: EF Core's "Applying migration" message (event 20402) was seen.
        Assert.Contains(logs.Entries, entry => entry.EventId.Id == 20402);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId.Id == 10632);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("IEntityTypeConfiguration"));
    }

    /// <summary>
    /// Architecture 2, T-002 and (v1.1) T-022: every domain entity has its own IEntityTypeConfiguration class. Twelve
    /// from version 1, plus Customer, SalesOrder and SalesOrderLine.
    /// </summary>
    [Fact]
    public void Every_domain_entity_has_its_own_configuration_class()
    {
        var configuredTypes = typeof(AppDbContext).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();

        Assert.Equal(15, DomainEntityTypes().Count);
        Assert.All(DomainEntityTypes(), entity => Assert.Contains(entity.ClrType, configuredTypes));
    }

    // ---- Enums, decimals, foreign keys --------------------------------------------------------------------

    /// <summary>
    /// T-002: enum names match design 2.3 exactly (these are the strings stored in the tables). (v1.1) T-022:
    /// SalesOrderStatus is Created, Shipped, Cancelled; MovementType is unchanged here (T-026 adds ShippedToCustomer).
    /// </summary>
    [Fact]
    public void Enum_names_match_design_section_2_3()
    {
        Assert.Equal(new[] { "RawMaterial", "FinishedProduct" }, Enum.GetNames<ItemType>());
        Assert.Equal(new[] { "Warehouse", "ProductionStorage" }, Enum.GetNames<Location>());
        Assert.Equal(
            new[] { "Received", "SentToProduction", "ConsumedInProduction", "ProducedOutput", "ReturnedToWarehouse", "Adjustment" },
            Enum.GetNames<MovementType>());
        Assert.Equal(new[] { "Created", "InProduction", "Completed", "Cancelled" }, Enum.GetNames<OrderStatus>());
        Assert.Equal(
            new[] { "PendingApproval", "Approved", "Rejected", "Sent", "Cancelled" },
            Enum.GetNames<ComplementaryOrderStatus>());
        Assert.Equal(new[] { "Pending", "Received", "Cancelled" }, Enum.GetNames<ReturnStatus>());
        Assert.Equal(new[] { "Created", "Shipped", "Cancelled" }, Enum.GetNames<SalesOrderStatus>());
        Assert.Equal(
            new[] { "UsageVariance", "OutputVariance", "LeftoverAtCompletion", "ComplementaryAwaitingApproval", "OrderCancelledWithMaterial" },
            Enum.GetNames<NotificationType>());
    }

    /// <summary>Architecture 3.1: every enum column stores the name as text (max 30), never the number.</summary>
    [Fact]
    public async Task Enums_are_stored_as_their_names()
    {
        await using var scope = CreateScope();
        var db = Db(scope);

        var enumProperties = DomainEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType).IsEnum)
            .ToList();
        // Item, Movement, ProductionOrder, ComplementaryOrder, Return, Notification and (v1.1) SalesOrder.
        Assert.Equal(7, enumProperties.Count);
        Assert.All(enumProperties, property =>
        {
            // The type mapping is how EF Core will actually read and write the column.
            Assert.Equal(typeof(string), property.GetTypeMapping().Converter?.ProviderClrType);
            Assert.Equal(30, property.GetMaxLength());
        });

        // And the raw column value really is the name.
        var user = await AddUserAsync(db);
        var product = await AddItemAsync(db, ItemType.FinishedProduct);
        var order = await AddOrderAsync(db, product, user, OrderStatus.Completed);

        var storedType = await db.Database
            .SqlQuery<string>($"SELECT \"Type\" AS Value FROM \"Items\" WHERE \"Id\" = {product.Id}")
            .SingleAsync(Cancel);
        var storedStatus = await db.Database
            .SqlQuery<string>($"SELECT \"Status\" AS Value FROM \"ProductionOrders\" WHERE \"Number\" = {order.Number}")
            .SingleAsync(Cancel);
        Assert.Equal("FinishedProduct", storedType);
        Assert.Equal("Completed", storedStatus);

        // (v1.1) The new enum column too.
        var salesOrder = await AddSalesOrderAsync(db, await AddCustomerAsync(db, user), user, SalesOrderStatus.Shipped, product.Id);
        var storedSalesStatus = await db.Database
            .SqlQuery<string>($"SELECT \"Status\" AS Value FROM \"SalesOrders\" WHERE \"Number\" = {salesOrder.Number}")
            .SingleAsync(Cancel);
        Assert.Equal("Shipped", storedSalesStatus);
    }

    /// <summary>
    /// Architecture 3.1: quantities are decimal(18,3), recipe amounts per unit decimal(18,6), and stored percentages
    /// decimal(28,6) (decision #82, widened by T-011).
    /// </summary>
    [Fact]
    public void Decimals_have_the_architecture_precision()
    {
        var decimals = DomainEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(decimal))
            .ToList();

        string[] amountsPerUnit = ["RecipeLine.AmountPerUnit", "ProductionOrderLine.AmountPerUnit"];
        string[] percentages =
        [
            "ProductionOrderLine.UsageVariancePercent", "ProductionOrder.OutputVariancePercent",
            "ProductionOrder.ToleranceAtCompletionPercent",
        ];
        Assert.All(decimals, property =>
        {
            var name = $"{property.DeclaringType.ClrType.Name}.{property.Name}";
            Assert.Equal(percentages.Contains(name) ? 28 : 18, property.GetPrecision());
            Assert.Equal(amountsPerUnit.Contains(name) || percentages.Contains(name) ? 6 : 3, property.GetScale());
        });
        Assert.Equal(amountsPerUnit.Length + percentages.Length, decimals.Count(property => property.GetScale() == 6));
        Assert.Equal(percentages.Length, decimals.Count(property => property.GetPrecision() == 28));
    }

    /// <summary>Architecture 3.1: every foreign key is Restrict, except recipe lines, which cascade with their recipe.</summary>
    [Fact]
    public void Foreign_keys_restrict_deletes_except_recipe_lines()
    {
        var foreignKeys = DomainEntityTypes().SelectMany(entity => entity.GetForeignKeys()).ToList();

        var cascading = Assert.Single(foreignKeys, fk => fk.DeleteBehavior != DeleteBehavior.Restrict);
        Assert.Equal(typeof(RecipeLine), cascading.DeclaringEntityType.ClrType);
        Assert.Equal(typeof(Recipe), cascading.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, cascading.DeleteBehavior);
    }

    /// <summary>AC-06.4 relies on Restrict: the database refuses to delete a unit that an item uses.</summary>
    [Fact]
    public async Task Database_refuses_to_delete_a_unit_in_use()
    {
        int unitId;
        await using (var writeScope = CreateScope())
        {
            unitId = (await AddItemAsync(Db(writeScope), ItemType.RawMaterial)).UnitId;
        }

        // A new DbContext that has loaded only the unit, so the delete reaches the database and the database's
        // foreign key decides (with the item loaded too, EF Core would refuse it in memory first).
        await using var scope = CreateScope();
        var db = Db(scope);
        db.Units.Remove(await db.Units.SingleAsync(u => u.Id == unitId, Cancel));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("FOREIGN KEY constraint failed", error.InnerException?.Message);
    }

    // ---- Times --------------------------------------------------------------------------------------------

    /// <summary>T-002, design 2.3: a DateTime saved as UTC is read back with Kind = Utc (nullable ones and Identity's too).</summary>
    [Fact]
    public async Task DateTime_saved_as_utc_is_read_back_as_utc()
    {
        int itemId, orderNumber;
        string userId;
        await using (var writeScope = CreateScope())
        {
            var db = Db(writeScope);
            var user = await AddUserAsync(db);
            var item = await AddItemAsync(db, ItemType.FinishedProduct);
            var order = await AddOrderAsync(db, item, user, OrderStatus.Completed);
            (itemId, orderNumber, userId) = (item.Id, order.Number, user.Id);
        }

        // A new scope means a new DbContext, so the values really come from the database, not from memory.
        await using var readScope = CreateScope();
        var readDb = Db(readScope);
        var readItem = await readDb.Items.AsNoTracking().SingleAsync(i => i.Id == itemId, Cancel);
        var readOrder = await readDb.ProductionOrders.AsNoTracking().SingleAsync(o => o.Number == orderNumber, Cancel);
        var readUser = await readDb.Users.AsNoTracking().SingleAsync(u => u.Id == userId, Cancel);

        Assert.Equal(DateTimeKind.Utc, readItem.CreatedAt.Kind);
        Assert.Equal(SampleTime, readItem.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, readOrder.SentAt!.Value.Kind);
        Assert.Equal(SampleTime.AddMinutes(1), readOrder.SentAt);
        Assert.Equal(DateTimeKind.Utc, readUser.CreatedAt.Kind);
    }

    /// <summary>Architecture 3.1: a Local DateTime is converted to UTC before it is stored, so local time is never saved by mistake.</summary>
    [Fact]
    public async Task Local_DateTime_is_stored_as_utc()
    {
        var local = SampleTime.ToLocalTime();
        int itemId;
        await using (var writeScope = CreateScope())
        {
            var db = Db(writeScope);
            itemId = (await AddItemAsync(db, ItemType.RawMaterial, createdAt: local)).Id;
        }

        await using var readScope = CreateScope();
        var readItem = await Db(readScope).Items.AsNoTracking().SingleAsync(i => i.Id == itemId, Cancel);
        Assert.Equal(SampleTime, readItem.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, readItem.CreatedAt.Kind);
    }

    // ---- Unique rules -------------------------------------------------------------------------------------

    /// <summary>T-002, AC-15.10 backstop: a second ProductionOrder with status InProduction fails with DbUpdateException.</summary>
    [Fact]
    public async Task Second_InProduction_order_is_refused_by_the_database()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var product = await AddItemAsync(db, ItemType.FinishedProduct);

        // The index is filtered: many orders in the other statuses are fine.
        await AddOrderAsync(db, product, user, OrderStatus.Created);
        await AddOrderAsync(db, product, user, OrderStatus.Created);
        await AddOrderAsync(db, product, user, OrderStatus.Completed);
        await AddOrderAsync(db, product, user, OrderStatus.InProduction);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => AddOrderAsync(db, product, user, OrderStatus.InProduction));
        Assert.Contains("UNIQUE constraint failed: ProductionOrders.Status", error.InnerException?.Message);

        // The realistic path: a Created order changed to InProduction while another one is in production.
        await using var otherScope = CreateScope();
        var otherDb = Db(otherScope);
        var created = await otherDb.ProductionOrders
            .FirstAsync(o => o.FinishedProductId == product.Id && o.Status == OrderStatus.Created, Cancel);
        created.Status = OrderStatus.InProduction;
        await Assert.ThrowsAsync<DbUpdateException>(() => otherDb.SaveChangesAsync(Cancel));
    }

    /// <summary>T-002, AC-06.2: two units whose names differ only in case cannot both be saved (unique NameNormalized).</summary>
    [Fact]
    public async Task Unit_names_differing_only_in_case_cannot_both_be_saved()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var name = $"Kg-{Guid.NewGuid():N}";

        db.Units.Add(new UnitOfMeasure { Name = name.ToLowerInvariant(), NameNormalized = NameNormalizer.Normalize(name.ToLowerInvariant()) });
        await db.SaveChangesAsync(Cancel);
        db.Units.Add(new UnitOfMeasure { Name = $" {name.ToUpperInvariant()} ", NameNormalized = NameNormalizer.Normalize($" {name.ToUpperInvariant()} ") });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("UNIQUE constraint failed: Units.NameNormalized", error.InnerException?.Message);
    }

    /// <summary>AC-07.1: two items whose names differ only in case cannot both be saved.</summary>
    [Fact]
    public async Task Item_names_differing_only_in_case_cannot_both_be_saved()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var first = await AddItemAsync(db, ItemType.RawMaterial);

        db.Items.Add(new Item
        {
            Name = first.Name.ToUpperInvariant(),
            NameNormalized = NameNormalizer.Normalize(first.Name.ToUpperInvariant()),
            Type = ItemType.RawMaterial,
            UnitId = first.UnitId,
            CreatedAt = SampleTime,
        });

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("UNIQUE constraint failed: Items.NameNormalized", error.InnerException?.Message);
    }

    // ---- (v1.1) Sales order tables: the database backstops -------------------------------------------------

    /// <summary>T-022, AC-29.2: two customers whose names differ only in case cannot both be saved (unique NameNormalized).</summary>
    [Fact]
    public async Task Customer_names_differing_only_in_case_cannot_both_be_saved()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var first = await AddCustomerAsync(db, user);

        db.Customers.Add(NewCustomer($" {first.Name.ToUpperInvariant()} ", user));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("UNIQUE constraint failed: Customers.NameNormalized", error.InnerException?.Message);
    }

    /// <summary>
    /// T-022, AC-24.3, decision #162: a second line for the same item on one sales order is refused by the database,
    /// while the same item on another sales order is fine.
    /// </summary>
    [Fact]
    public async Task Second_line_for_the_same_item_on_a_sales_order_is_refused()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var customer = await AddCustomerAsync(db, user);
        var item = await AddItemAsync(db, ItemType.FinishedProduct);

        // The same item on two different sales orders is allowed.
        await AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Created, item.Id);
        await AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Created, item.Id);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Created, item.Id, item.Id));
        Assert.Contains("UNIQUE constraint failed: SalesOrderLines.SalesOrderNumber, SalesOrderLines.ItemId", error.InnerException?.Message);
    }

    /// <summary>
    /// T-022, AC-29.5 and AC-29.8 backstop: deleting a customer that a sales order names is refused by the database
    /// (Restrict), even when the sales order is Cancelled; the customer is still there afterwards.
    /// </summary>
    [Fact]
    public async Task Database_refuses_to_delete_a_customer_that_a_sales_order_names()
    {
        int customerId;
        await using (var writeScope = CreateScope())
        {
            var db = Db(writeScope);
            var user = await AddUserAsync(db);
            var customer = await AddCustomerAsync(db, user);
            await AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Cancelled, (await AddItemAsync(db, ItemType.RawMaterial)).Id);
            customerId = customer.Id;
        }

        // A new DbContext that has loaded only the customer, so the delete reaches the database and its foreign key
        // decides (with the sales order loaded too, EF Core would refuse it in memory first).
        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            db.Customers.Remove(await db.Customers.SingleAsync(c => c.Id == customerId, Cancel));

            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
            Assert.Contains("FOREIGN KEY constraint failed", error.InnerException?.Message);
        }

        await using var readScope = CreateScope();
        Assert.True(await Db(readScope).Customers.AnyAsync(c => c.Id == customerId, Cancel));
    }

    /// <summary>
    /// T-022, AC-28.1, decision #157: a movement naming a sales order number that does not exist is refused by the
    /// foreign key, and nothing is saved.
    /// </summary>
    [Fact]
    public async Task Movement_naming_an_unknown_sales_order_is_refused()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var item = await AddItemAsync(db, ItemType.FinishedProduct);

        db.Movements.Add(NewSalesOrderMovement(item.Id, user.Id, salesOrderNumber: 999_999));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("FOREIGN KEY constraint failed", error.InnerException?.Message);

        await using var readScope = CreateScope();
        Assert.False(await Db(readScope).Movements.AnyAsync(m => m.ItemId == item.Id, Cancel));
    }

    /// <summary>
    /// T-022, AC-26.10 backstop, decision #157: two movements with the same SalesOrderNumber and ItemId are refused by
    /// <c>IX_Movements_OneShipmentPerLine</c>. The index is filtered, so two movements of the same item with a null
    /// SalesOrderNumber are fine, and so is a movement for another item on the same sales order (another line).
    /// </summary>
    [Fact]
    public async Task Second_movement_for_the_same_sales_order_and_item_is_refused()
    {
        await using var scope = CreateScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var cheese = await AddItemAsync(db, ItemType.RawMaterial);
        var bread = await AddItemAsync(db, ItemType.FinishedProduct);
        var salesOrder = await AddSalesOrderAsync(
            db, await AddCustomerAsync(db, user), user, SalesOrderStatus.Shipped, cheese.Id, bread.Id);

        // Outside the index: any number of movements without a sales order number.
        db.Movements.Add(NewSalesOrderMovement(cheese.Id, user.Id, salesOrderNumber: null));
        db.Movements.Add(NewSalesOrderMovement(cheese.Id, user.Id, salesOrderNumber: null));
        await db.SaveChangesAsync(Cancel);

        // One movement per line of the sales order is fine.
        db.Movements.Add(NewSalesOrderMovement(cheese.Id, user.Id, salesOrder.Number));
        db.Movements.Add(NewSalesOrderMovement(bread.Id, user.Id, salesOrder.Number));
        await db.SaveChangesAsync(Cancel);

        db.Movements.Add(NewSalesOrderMovement(cheese.Id, user.Id, salesOrder.Number));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancel));
        Assert.Contains("UNIQUE constraint failed: Movements.SalesOrderNumber, Movements.ItemId", error.InnerException?.Message);

        await using var readScope = CreateScope();
        Assert.Equal(3, await Db(readScope).Movements.CountAsync(m => m.ItemId == cheese.Id, Cancel));
    }

    /// <summary>
    /// T-022, D-10: sales orders have their own number sequence, so the first sales order is number 1 even when
    /// production orders exist, and the next one is 2. Uses a database of its own, so "first" really is the first.
    /// </summary>
    [Fact]
    public async Task First_sales_order_is_number_1_even_when_production_orders_exist()
    {
        await using var freshFactory = new TestApiFactory();
        freshFactory.CreateClient(); // starts the app, which creates and migrates the database
        await using var scope = freshFactory.Services.CreateAsyncScope();
        var db = Db(scope);
        var user = await AddUserAsync(db);
        var product = await AddItemAsync(db, ItemType.FinishedProduct);

        var productionOrders = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            productionOrders.Add((await AddOrderAsync(db, product, user, OrderStatus.Completed)).Number);
        }

        var customer = await AddCustomerAsync(db, user);
        var first = await AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Created, product.Id);
        var second = await AddSalesOrderAsync(db, customer, user, SalesOrderStatus.Created, product.Id);

        Assert.Equal([1, 2, 3], productionOrders);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
    }

    /// <summary>
    /// T-022, AC-29.5, decision #162: after a customer is deleted, the next customer does not get its id. The deleted one
    /// is the newest (highest id), which is exactly the case where SQLite without AUTOINCREMENT would hand the same id out
    /// again (it would take the largest id in the table plus one). With AUTOINCREMENT, the deleted id stays unused.
    /// </summary>
    [Fact]
    public async Task Deleted_customer_id_is_never_reused()
    {
        int deletedId;
        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            var user = await AddUserAsync(db);
            await AddCustomerAsync(db, user);
            var newest = await AddCustomerAsync(db, user);
            deletedId = newest.Id;

            db.Customers.Remove(newest);
            await db.SaveChangesAsync(Cancel);
            Assert.Equal(deletedId - 1, await db.Customers.MaxAsync(c => c.Id, Cancel)); // the deleted id is now above every row

            var next = await AddCustomerAsync(db, user);
            Assert.True(next.Id > deletedId, $"The next customer got id {next.Id}; the deleted one had {deletedId}.");
        }

        await using var readScope = CreateScope();
        Assert.False(await Db(readScope).Customers.AnyAsync(c => c.Id == deletedId, Cancel));
    }

    /// <summary>Architecture 3.1: the normalized name is trimmed and upper-cased with the invariant culture.</summary>
    [Fact]
    public void NameNormalizer_trims_and_upper_cases()
    {
        Assert.Equal("POTATO STARCH", NameNormalizer.Normalize("  Potato starch "));
        Assert.Equal(NameNormalizer.Normalize("kg"), NameNormalizer.Normalize("KG"));
    }

    // ---- Movements are insert-only ------------------------------------------------------------------------

    /// <summary>AC-11.3: there is no update path for movements; changing or deleting a saved one is refused and nothing changes.</summary>
    [Fact]
    public async Task Saved_movement_cannot_be_changed_or_deleted()
    {
        int movementId;
        await using (var writeScope = CreateScope())
        {
            var db = Db(writeScope);
            var user = await AddUserAsync(db);
            var item = await AddItemAsync(db, ItemType.RawMaterial);
            var movement = new Movement
            {
                Type = MovementType.Received,
                ItemId = item.Id,
                WarehouseChange = 1000m,
                OccurredAt = SampleTime,
                UserId = user.Id,
                ReceiptDate = new DateOnly(2026, 11, 11),
                Note = "Delivery note 4471",
            };
            db.Movements.Add(movement);
            await db.SaveChangesAsync(Cancel);
            movementId = movement.Id;
        }

        await using (var editScope = CreateScope())
        {
            var db = Db(editScope);
            var saved = await db.Movements.SingleAsync(m => m.Id == movementId, Cancel);

            // Properties are init-only, so a change has to go through EF Core's change tracker, which is what this simulates.
            db.Entry(saved).Property(m => m.WarehouseChange).CurrentValue = 5m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Cancel));

            db.Entry(saved).State = EntityState.Deleted;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Cancel));
        }

        await using var readScope = CreateScope();
        var reread = await Db(readScope).Movements.AsNoTracking().SingleAsync(m => m.Id == movementId, Cancel);
        Assert.Equal(1000m, reread.WarehouseChange);
        Assert.Null(reread.ProductionStorageChange);
        Assert.Equal(new DateOnly(2026, 11, 11), reread.ReceiptDate);
    }

    // ---- InWriteTransactionAsync --------------------------------------------------------------------------

    /// <summary>Architecture 5.2: successful work is saved and committed, and its result is returned.</summary>
    [Fact]
    public async Task Write_transaction_saves_and_returns_the_result()
    {
        var name = Seed.UniqueName("Bag");
        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            var result = await db.InWriteTransactionAsync(() =>
            {
                db.Units.Add(NewUnit(name));
                return Task.FromResult(42);
            }, Cancel);

            Assert.Equal(42, result);
            Assert.Null(db.Database.CurrentTransaction); // committed and closed
        }

        await using var readScope = CreateScope();
        Assert.True(await Db(readScope).Units.AnyAsync(u => u.Name == name, Cancel));
    }

    /// <summary>
    /// Architecture 5.2, the documented pattern for creating rows: SaveChangesAsync inside the work fills in the
    /// database-generated key, so the response can show it, and the row is committed once.
    /// </summary>
    [Fact]
    public async Task Work_that_saves_itself_can_return_a_generated_key()
    {
        var name = Seed.UniqueName("Keyed");
        int returnedId;
        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            returnedId = await db.InWriteTransactionAsync(async () =>
            {
                var unit = NewUnit(name);
                db.Units.Add(unit);
                Assert.Equal(0, unit.Id); // not generated yet
                await db.SaveChangesAsync(Cancel);
                return unit.Id;
            }, Cancel);
        }

        Assert.NotEqual(0, returnedId);
        await using var readScope = CreateScope();
        var saved = await Db(readScope).Units.AsNoTracking().SingleAsync(u => u.Name == name, Cancel);
        Assert.Equal(returnedId, saved.Id);
    }

    /// <summary>
    /// T-002, PRD section 5 ("completes fully or saves nothing"): an exception inside the work saves nothing,
    /// even rows the work had already flushed with SaveChangesAsync, and the exception reaches the caller unchanged.
    /// </summary>
    [Fact]
    public async Task Exception_inside_write_transaction_saves_nothing()
    {
        var flushedName = Seed.UniqueName("Flushed");
        var pendingName = Seed.UniqueName("Pending");
        var failure = new InvalidOperationException("Simulated rule failure");

        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.InWriteTransactionAsync(async () =>
                {
                    db.Units.Add(NewUnit(flushedName));
                    await db.SaveChangesAsync(Cancel); // sent to the database, inside the transaction
                    db.Units.Add(NewUnit(pendingName));
                    throw failure;
                }, Cancel));

            Assert.Same(failure, thrown);
            Assert.Empty(db.ChangeTracker.Entries()); // nothing left that a later save could write by accident
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using var readScope = CreateScope();
        var readDb = Db(readScope);
        Assert.False(await readDb.Units.AnyAsync(u => u.Name == flushedName || u.Name == pendingName, Cancel));
    }

    /// <summary>
    /// T-002, architecture 5.2: while one write transaction is open, a second one on another DbContext waits until
    /// the first commits, and then sees the committed data.
    /// </summary>
    [Fact]
    public async Task Second_write_transaction_waits_for_the_first_and_sees_its_data()
    {
        var name = Seed.UniqueName("Serialized");
        // TaskCompletionSource is a task the test completes by hand, used here as a signal between the two
        // transactions. RunContinuationsAsynchronously stops the waiting code from running inside SetResult.
        var firstHasBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondIsCalling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHasBegun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Task.Run puts each transaction on its own thread: SQLite waits for its lock by blocking the thread.
        var first = Task.Run(async () =>
        {
            await using var scope = CreateScope();
            var db = Db(scope);
            await db.InWriteTransactionAsync(async () =>
            {
                db.Units.Add(NewUnit(name));
                firstHasBegun.SetResult();
                await releaseFirst.Task; // hold the transaction open until the test says so
            }, Cancel);
        }, Cancel);

        try
        {
            await firstHasBegun.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            var second = Task.Run(async () =>
            {
                await using var scope = CreateScope();
                var db = Db(scope);
                secondIsCalling.SetResult(); // about to ask for the write lock
                return await db.InWriteTransactionAsync(async () =>
                {
                    secondHasBegun.SetResult();
                    return await db.Units.AnyAsync(u => u.Name == name, Cancel);
                }, Cancel);
            }, Cancel);

            // Start the observation window only once the second task is really calling InWriteTransactionAsync,
            // so a slow thread start cannot make the test pass without the lock being tested.
            await secondIsCalling.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            // The second transaction must not get past BEGIN while the first holds the write lock.
            var firstToFinish = await Task.WhenAny(secondHasBegun.Task, Task.Delay(TimeSpan.FromMilliseconds(750), Cancel));
            Assert.NotSame(secondHasBegun.Task, firstToFinish);

            releaseFirst.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
            var secondSawTheUnit = await second.WaitAsync(TimeSpan.FromSeconds(10), Cancel);

            Assert.True(secondHasBegun.Task.IsCompleted);
            Assert.True(secondSawTheUnit);
        }
        finally
        {
            // If an assertion above failed, the first transaction would otherwise keep the write lock, and later
            // tests on this database would each wait 30 seconds for it.
            releaseFirst.TrySetResult();
        }
    }

    /// <summary>Architecture 5.2: one write transaction per request; a nested call is refused instead of half-working.</summary>
    [Fact]
    public async Task Nested_write_transaction_is_refused()
    {
        var name = Seed.UniqueName("Nested");
        await using (var scope = CreateScope())
        {
            var db = Db(scope);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.InWriteTransactionAsync(async () =>
                {
                    db.Units.Add(NewUnit(name));
                    await db.InWriteTransactionAsync(() => Task.CompletedTask, Cancel);
                }, Cancel));
        }

        await using var readScope = CreateScope();
        Assert.False(await Db(readScope).Units.AnyAsync(u => u.Name == name, Cancel));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private AsyncServiceScope CreateScope() => factory.Services.CreateAsyncScope();

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    /// <summary>The model's entity types that belong to the domain (Identity's tables excluded).</summary>
    private List<IEntityType> DomainEntityTypes()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Model.GetEntityTypes().Where(entity => entity.ClrType.Namespace == typeof(Item).Namespace).ToList();
    }

    /// <summary>
    /// One property of an entity in <paramref name="model"/>. The snapshot's model knows entities by name only (it has
    /// no CLR types), so the lookup is by the type's full name.
    /// </summary>
    private static IReadOnlyProperty Property(IReadOnlyModel model, Type entity, string property) =>
        model.FindEntityType(entity.FullName!)!.FindProperty(property)!;

    /// <summary>A notification about <paramref name="orderNumber"/> with exactly <paramref name="message"/>.</summary>
    private static Notification NewNotification(int orderNumber, string message) => new()
    {
        Type = NotificationType.OutputVariance,
        ProductionOrderNumber = orderNumber,
        CreatedAt = SampleTime,
        Message = message,
        DataJson = "{}",
    };

    private static UnitOfMeasure NewUnit(string name) =>
        new() { Name = name, NameNormalized = NameNormalizer.Normalize(name) };

    private static AppUser NewUser()
    {
        var email = $"{Guid.NewGuid():N}@example.com";
        return new AppUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            CreatedAt = SampleTime,
        };
    }

    private static async Task<AppUser> AddUserAsync(AppDbContext db)
    {
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancel);
        return user;
    }

    private static async Task<Item> AddItemAsync(AppDbContext db, ItemType type, DateTime? createdAt = null)
    {
        var name = Seed.UniqueName("Item");
        var item = new Item
        {
            Name = name,
            NameNormalized = NameNormalizer.Normalize(name),
            Type = type,
            Unit = NewUnit(Seed.UniqueName("Unit")),
            CreatedAt = createdAt ?? SampleTime,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync(Cancel);
        return item;
    }

    private static async Task<ProductionOrder> AddOrderAsync(AppDbContext db, Item product, AppUser user, OrderStatus status)
    {
        var order = new ProductionOrder
        {
            FinishedProductId = product.Id,
            OrderedQuantity = 1000m,
            Status = status,
            CreatedAt = SampleTime,
            CreatedById = user.Id,
            StatusChangedAt = SampleTime,
        };
        if (status != OrderStatus.Created)
        {
            order.SentAt = SampleTime.AddMinutes(1);
            order.SentById = user.Id;
        }

        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync(Cancel);
        return order;
    }

    private static Customer NewCustomer(string name, AppUser user) => new()
    {
        Name = name.Trim(),
        NameNormalized = NameNormalizer.Normalize(name),
        CreatedAt = SampleTime,
        CreatedById = user.Id,
    };

    private static async Task<Customer> AddCustomerAsync(AppDbContext db, AppUser user)
    {
        var customer = NewCustomer(Seed.UniqueName("Customer"), user);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(Cancel);
        return customer;
    }

    /// <summary>
    /// Saves a sales order in <paramref name="status"/> with one line of 600 per item in <paramref name="itemIds"/>, in
    /// that order. The who-and-when pair of a Shipped or Cancelled status is filled in too (AC-05.3).
    /// </summary>
    private static async Task<SalesOrder> AddSalesOrderAsync(
        AppDbContext db, Customer customer, AppUser user, SalesOrderStatus status, params int[] itemIds)
    {
        var salesOrder = new SalesOrder
        {
            CustomerId = customer.Id,
            Status = status,
            CreatedAt = SampleTime,
            CreatedById = user.Id,
            StatusChangedAt = SampleTime,
            Lines = itemIds.Select((itemId, index) => new SalesOrderLine { LineNo = index + 1, ItemId = itemId, Quantity = 600m }).ToList(),
        };
        if (status == SalesOrderStatus.Shipped)
        {
            (salesOrder.ShippedAt, salesOrder.ShippedById) = (SampleTime, user.Id);
        }
        else if (status == SalesOrderStatus.Cancelled)
        {
            (salesOrder.CancelledAt, salesOrder.CancelledById, salesOrder.CancellationReason) = (SampleTime, user.Id, "Customer changed the order");
        }

        db.SalesOrders.Add(salesOrder);
        await db.SaveChangesAsync(Cancel);
        return salesOrder;
    }

    /// <summary>
    /// A Warehouse decrease of 1 that names <paramref name="salesOrderNumber"/>, for the database rules on the new column.
    /// Its type is Adjustment only because <c>ShippedToCustomer</c> arrives in T-026: the foreign key and the filtered
    /// index look at the sales order number and the item, never at the type. Added straight through the DbContext,
    /// not through <c>StockLedger</c>, to reach the database's own checks (tests only, decision #112).
    /// </summary>
    private static Movement NewSalesOrderMovement(int itemId, string userId, int? salesOrderNumber) => new()
    {
        Type = MovementType.Adjustment,
        ItemId = itemId,
        WarehouseChange = -1m,
        OccurredAt = SampleTime,
        UserId = userId,
        Reason = "Database rule test",
        SalesOrderNumber = salesOrderNumber,
    };
}
