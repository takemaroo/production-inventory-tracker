using System.Collections.Concurrent;
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
/// T-002: the database schema, the domain migration and the write transaction (architecture 3 and 5.2).
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

    /// <summary>T-002: on an empty database, start-up applies InitialIdentity and then the single DomainModel migration.</summary>
    [Fact]
    public async Task Startup_on_an_empty_database_applies_the_domain_migration()
    {
        await using var scope = CreateScope();
        var db = Db(scope);

        var applied = db.Database.GetAppliedMigrations().ToList();
        Assert.Equal(2, applied.Count);
        Assert.EndsWith("_InitialIdentity", applied[0]);
        Assert.EndsWith("_DomainModel", applied[1]);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Cancel));

        var tables = await db.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(Cancel);
        string[] domainTables =
        [
            "Units", "Items", "Recipes", "RecipeLines", "Movements", "ProductionOrders", "ProductionOrderLines",
            "ComplementaryOrders", "ComplementaryOrderLines", "Returns", "Notifications", "NotificationReads",
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
        Assert.Contains(logs.Entries, entry => entry.EventId == 20402);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == 10632);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("IEntityTypeConfiguration"));
    }

    /// <summary>Architecture 2 and T-002: every domain entity has its own IEntityTypeConfiguration class.</summary>
    [Fact]
    public void Every_domain_entity_has_its_own_configuration_class()
    {
        var configuredTypes = typeof(AppDbContext).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToHashSet();

        Assert.Equal(12, DomainEntityTypes().Count);
        Assert.All(DomainEntityTypes(), entity => Assert.Contains(entity.ClrType, configuredTypes));
    }

    // ---- Enums, decimals, foreign keys --------------------------------------------------------------------

    /// <summary>T-002: enum names match design 2.3 exactly (these are the strings stored in the tables).</summary>
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
        Assert.Equal(6, enumProperties.Count); // Item, Movement, ProductionOrder, ComplementaryOrder, Return, Notification
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
    }

    /// <summary>Architecture 3.1: quantities are decimal(18,3); recipe amounts and stored percentages are decimal(18,6).</summary>
    [Fact]
    public void Decimals_have_the_architecture_precision()
    {
        var decimals = DomainEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(decimal))
            .ToList();

        string[] sixPlaces =
        [
            "RecipeLine.AmountPerUnit", "ProductionOrderLine.AmountPerUnit", "ProductionOrderLine.UsageVariancePercent",
            "ProductionOrder.OutputVariancePercent", "ProductionOrder.ToleranceAtCompletionPercent",
        ];
        Assert.All(decimals, property =>
        {
            var name = $"{property.DeclaringType.ClrType.Name}.{property.Name}";
            Assert.Equal(18, property.GetPrecision());
            Assert.Equal(sixPlaces.Contains(name) ? 6 : 3, property.GetScale());
        });
        Assert.Equal(sixPlaces.Length, decimals.Count(property => property.GetScale() == 6));
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
        var name = UniqueName("Bag");
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
        var name = UniqueName("Keyed");
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
        var flushedName = UniqueName("Flushed");
        var pendingName = UniqueName("Pending");
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
        var name = UniqueName("Serialized");
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
        var name = UniqueName("Nested");
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

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

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
        var name = UniqueName("Item");
        var item = new Item
        {
            Name = name,
            NameNormalized = NameNormalizer.Normalize(name),
            Type = type,
            Unit = NewUnit(UniqueName("Unit")),
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

    /// <summary>A logger provider that keeps every log entry in memory so a test can inspect them.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
