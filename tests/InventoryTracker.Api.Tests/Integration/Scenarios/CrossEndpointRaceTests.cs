using System.Net;
using System.Net.Http.Json;
using InventoryTracker.Api.Tests.Infrastructure;
using static InventoryTracker.Api.Tests.Infrastructure.OrderFlow;
using static InventoryTracker.Api.Tests.Integration.Scenarios.ScenarioApi;

namespace InventoryTracker.Api.Tests.Integration.Scenarios;

/// <summary>
/// T-020, PRD section 5 ("two movements that together would take stock below zero: at most one succeeds and stock never
/// goes below zero"), architecture 5.2: races between two different endpoints that take the same Warehouse stock. A
/// send for an order (AC-15.7) or for an approved complementary order (AC-18.4) and a negative Warehouse adjustment
/// (AC-12.3) that together need more than the Warehouse holds: exactly one succeeds, the other gets <c>409</c>
/// <c>insufficient-stock</c> naming what is left, and stock never goes below zero. (v1.1, T-031) The same for a sales
/// order shipment (AC-26.1) against a write-off, a production send and a complementary send (AC-26.10, fourth and fifth
/// bullets).
/// </summary>
/// <remarks>
/// <para>
/// The races of one endpoint against itself (two sends, two adjustments, two receives, a send against a cancellation
/// and so on) are already proven in each feature's tests (T-010, T-015 to T-019); these are the cross-endpoint ones the
/// task board names for T-020.
/// </para>
/// <para>
/// Each pair is run three ways. Two tests force the order with <see cref="ForcedOrder"/>: the first request takes
/// SQLite's single write lock (<c>BEGIN IMMEDIATE</c>), and when it first reads the <c>Movements</c> table (both
/// endpoints sum the Warehouse stock from it) the second request is started and shown to be waiting. So each branch is
/// proven to run, with exact figures. One more test fires both at the same moment for several rounds and checks only
/// what must hold whichever wins. (v1.1) The three shipment pairs are each forced both ways (a theory with
/// <c>shipFirst</c> true and false); the simultaneous rounds are run for the ship against the write-off, the pair
/// AC-26.10's fourth bullet names.
/// </para>
/// <para>
/// The set-up is the shared <see cref="OrderFlow.RaceSceneAsync"/> of the earlier race tests (decision #101), which
/// creates its users through Identity directly; unlike the worked examples, these tests are about the race, not about
/// starting from an empty database through HTTP only. The race itself and every check after it go through the API.
/// </para>
/// </remarks>
public class CrossEndpointRaceTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // ---- Order send against a Warehouse write-off ---------------------------------------------------------------------

    /// <summary>
    /// PRD section 5, AC-15.1, AC-12.3: 1,000 in the Warehouse; the send of 1,000 for the order holds the lock first, and
    /// a −100 Warehouse adjustment waits behind it. The send succeeds (InProduction, Warehouse 0); the adjustment then
    /// sees 0 and gets <c>409</c> "Not enough stock: {item} in Warehouse has 0 {unit}.", and no Adjustment movement exists.
    /// </summary>
    [Fact]
    public async Task Write_off_waiting_behind_a_send_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await RaceSceneAsync(race.App, send: false);

        var (send, writeOff) = await race.RunAsync(
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m)),
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, 100m));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("InProduction", (await ApiJson.ReadAsync(send)).GetProperty("status").GetString());
        await ApiAssert.ProblemAsync(writeOff, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Warehouse has 0 {scene.Cheese.UnitName}.");
        await AssertWarehouseAsync(scene, expected: 0m, adjustments: 0);
    }

    /// <summary>
    /// PRD section 5, AC-12.3, AC-15.7: the −100 Warehouse adjustment holds the lock first and the send of 1,000 waits.
    /// The adjustment succeeds (Warehouse 900); the send then sees 900 and gets <c>409</c> "Not enough stock: {item} in
    /// Warehouse has 900 {unit}.", the order stays Created with nothing sent, and no SentToProduction movement exists.
    /// </summary>
    [Fact]
    public async Task Send_waiting_behind_a_write_off_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await RaceSceneAsync(race.App, send: false);

        var (writeOff, send) = await race.RunAsync(
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, 100m),
            () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m)));

        Assert.Equal(HttpStatusCode.Created, writeOff.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Warehouse has 900 {scene.Cheese.UnitName}.");
        var order = await ReadOrderAsync(scene.Manager, scene.Number);
        Assert.Equal("Created", order.GetProperty("status").GetString());
        Assert.Empty(await OrderMovementsAsync(scene.Manager, scene.Number));
        await AssertWarehouseAsync(scene, expected: 900m, adjustments: 1);
    }

    /// <summary>
    /// PRD section 5: the send of 1,000 and the −100 Warehouse adjustment arrive at the same moment, five rounds, each on
    /// its own material and order. Exactly one succeeds; the loser gets <c>409</c> <c>insufficient-stock</c> naming the
    /// stock the winner left (0 or 900); the Warehouse never goes below zero and its movements add up to it (AC-11.4).
    /// A successful send's order is cancelled afterwards, so the next round can send (AC-15.10).
    /// </summary>
    [Fact]
    public async Task Simultaneous_send_and_write_off_never_both_succeed()
    {
        await using var app = new TestApiFactory();

        for (var round = 0; round < 5; round++)
        {
            var scene = await RaceSceneAsync(app, send: false);

            var sendTask = SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m));
            var writeOffTask = WriteOffAsync(scene.Manager, scene.Cheese.Id, 100m);
            await Task.WhenAll(sendTask, writeOffTask);
            var (send, writeOff) = (await sendTask, await writeOffTask);

            var sent = send.StatusCode == HttpStatusCode.OK;
            Assert.True(sent != (writeOff.StatusCode == HttpStatusCode.Created),
                $"Round {round}: send {(int)send.StatusCode}, write-off {(int)writeOff.StatusCode}; exactly one must succeed.");
            var left = sent ? 0m : 900m;
            await ApiAssert.ProblemAsync(sent ? writeOff : send, HttpStatusCode.Conflict, "insufficient-stock",
                $"Not enough stock: {scene.Cheese.Name} in Warehouse has {left:0} {scene.Cheese.UnitName}.");
            await AssertWarehouseAsync(scene, expected: left, adjustments: sent ? 0 : 1);
            if (sent)
            {
                Assert.Equal(HttpStatusCode.OK, (await CancelAsync(scene.Manager, scene.Number, "Race round finished")).StatusCode);
            }
        }

        Assert.True(await AssertMovementsAddUpToStockAsync(await StartUpManagerAsync(app)) > 0);
    }

    // ---- Complementary send against a Warehouse write-off -------------------------------------------------------------

    /// <summary>
    /// PRD section 5, AC-18.1, AC-12.3: the order is InProduction and 100 more is in the Warehouse; an approved
    /// complementary order of 20 is sent first, and a −90 Warehouse adjustment waits behind it. The send succeeds
    /// (Sent, Warehouse 80); the adjustment then gets <c>409</c> "Not enough stock: {item} in Warehouse has 80 {unit}.".
    /// </summary>
    [Fact]
    public async Task Write_off_waiting_behind_a_complementary_send_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var (scene, complementary) = await ApprovedComplementarySceneAsync(race.App);

        var (send, writeOff) = await race.RunAsync(
            () => SendComplementaryAsync(scene.Warehouse, complementary, (scene.Cheese.Id, 20m)),
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, 90m));

        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal("Sent", (await ApiJson.ReadAsync(send)).GetProperty("status").GetString());
        await ApiAssert.ProblemAsync(writeOff, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Warehouse has 80 {scene.Cheese.UnitName}.");
        await AssertWarehouseAsync(scene, expected: 80m, adjustments: 0);
    }

    /// <summary>
    /// PRD section 5, AC-12.3, AC-18.4: the −90 Warehouse adjustment goes first and the complementary send of 20 waits.
    /// The adjustment succeeds (Warehouse 10); the send then gets <c>409</c> "Not enough stock: {item} in Warehouse has 10
    /// {unit}.", the complementary order stays Approved, and the parent line's complementary sends stay 0.
    /// </summary>
    [Fact]
    public async Task Complementary_send_waiting_behind_a_write_off_is_refused()
    {
        await using var race = new ForcedOrder("Movements");
        var (scene, complementary) = await ApprovedComplementarySceneAsync(race.App);

        var (writeOff, send) = await race.RunAsync(
            () => WriteOffAsync(scene.Manager, scene.Cheese.Id, 90m),
            () => SendComplementaryAsync(scene.Warehouse, complementary, (scene.Cheese.Id, 20m)));

        Assert.Equal(HttpStatusCode.Created, writeOff.StatusCode);
        await ApiAssert.ProblemAsync(send, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Warehouse has 10 {scene.Cheese.UnitName}.");
        Assert.Equal("Approved", (await ReadComplementaryAsync(scene.Manager, complementary)).GetProperty("status").GetString());
        Assert.Equal(0m, Line(await ReadOrderAsync(scene.Manager, scene.Number), scene.Cheese.Id).GetProperty("complementarySends").GetDecimal());
        await AssertWarehouseAsync(scene, expected: 10m, adjustments: 1);
    }

    // ---- (v1.1) T-031: a shipment against a write-off, a production send and a complementary send ---------------------
    //
    // AC-26.10, fourth and fifth bullets, architecture 5.2: the ship checks and takes Warehouse stock through the ledger
    // in its own serialized transaction, like the three v1 writes it races here. Each pair together needs more than the
    // Warehouse holds. Both orderings are forced with ForcedOrder("Movements"): every one of these requests sums the
    // material's movements inside its write transaction, so the first one holds the write lock when the second starts.
    // The sales orders are created by the scene's Manager (who may create them, AC-24.1), for a customer of their own.

    /// <summary>
    /// AC-26.10 fourth bullet, AC-26.1, AC-12.3: 1,000 in the Warehouse; a ship of a 600 sales order and a −500
    /// Warehouse adjustment of the same item. Ship first: <c>200</c>, Warehouse 400, and the adjustment gets <c>409</c>
    /// "... has 400 ...". Adjustment first: <c>201</c>, Warehouse 500, and the ship gets <c>409</c> "... has 500 ...;
    /// sales order #n needs 600 ...", with no shipment movement and the sales order still Created. The Warehouse never
    /// goes below zero.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ship_and_write_off_of_the_same_item_never_both_succeed(bool shipFirst)
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await RaceSceneAsync(race.App, send: false);
        var number = await SalesOrderAsync(scene, 600m);
        Func<Task<HttpResponseMessage>> ship = () => SalesFlow.ShipAsync(scene.Warehouse, number);
        Func<Task<HttpResponseMessage>> writeOff = () => WriteOffAsync(scene.Manager, scene.Cheese.Id, 500m);

        var (shipped, wroteOff) = await RunInOrderAsync(race, shipFirst, ship, writeOff);

        if (shipFirst)
        {
            Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
            await ApiAssert.ProblemAsync(wroteOff, HttpStatusCode.Conflict, "insufficient-stock",
                $"Not enough stock: {scene.Cheese.Name} in Warehouse has 400 {scene.Cheese.UnitName}.");
            await AssertWarehouseAsync(scene, expected: 400m, adjustments: 0);
            Assert.Single(await SalesFlow.ShipmentMovementsAsync(scene.Manager, number));
        }
        else
        {
            Assert.Equal(HttpStatusCode.Created, wroteOff.StatusCode);
            await AssertShipRefusedAsync(scene, shipped, number, available: 500m, needs: 600m);
            await AssertWarehouseAsync(scene, expected: 500m, adjustments: 1);
        }
    }

    /// <summary>
    /// AC-26.10 fifth bullet, AC-26.6, AC-15.7: 1,000 of a raw material in the Warehouse; a ship of a 100 sales order of
    /// it and the production send of 1,000 for the Created order that uses it. Ship first: <c>200</c>, Warehouse 900, and
    /// the send gets <c>409</c> "... has 900 ...", the order stays Created with no movement (AC-26.12's shape). Send
    /// first: InProduction, Warehouse 0, and the ship gets <c>409</c> "... has 0 ...; sales order #n needs 100 ...".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ship_and_production_send_of_the_same_material_never_both_succeed(bool shipFirst)
    {
        await using var race = new ForcedOrder("Movements");
        var scene = await RaceSceneAsync(race.App, send: false);
        var number = await SalesOrderAsync(scene, 100m);
        Func<Task<HttpResponseMessage>> ship = () => SalesFlow.ShipAsync(scene.Manager, number);
        Func<Task<HttpResponseMessage>> send = () => SendAsync(scene.Warehouse, scene.Number, (scene.Cheese.Id, 1000m));

        var (shipped, sent) = await RunInOrderAsync(race, shipFirst, ship, send);

        if (shipFirst)
        {
            Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
            await ApiAssert.ProblemAsync(sent, HttpStatusCode.Conflict, "insufficient-stock",
                $"Not enough stock: {scene.Cheese.Name} in Warehouse has 900 {scene.Cheese.UnitName}.");
            Assert.Equal("Created", (await ReadOrderAsync(scene.Manager, scene.Number)).GetProperty("status").GetString());
            Assert.Empty(await OrderMovementsAsync(scene.Manager, scene.Number));
            await AssertWarehouseAsync(scene, expected: 900m, adjustments: 0);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal("InProduction", (await ApiJson.ReadAsync(sent)).GetProperty("status").GetString());
            await AssertShipRefusedAsync(scene, shipped, number, available: 0m, needs: 100m);
            await AssertWarehouseAsync(scene, expected: 0m, adjustments: 0);
        }
    }

    /// <summary>
    /// AC-26.10 fifth bullet, AC-26.6, AC-18.4: the order is InProduction and 100 more of its material is in the
    /// Warehouse; a ship of a 90 sales order of the material and the send of an approved complementary order of 20.
    /// Shipping while the order is InProduction is allowed (AC-26.6). Ship first: <c>200</c>, Warehouse 10, and the
    /// complementary send gets <c>409</c> "... has 10 ...", stays Approved, and the parent line's complementary sends
    /// stay 0. Complementary send first: Sent, Warehouse 80, and the ship gets <c>409</c> "... has 80 ...; sales order #n
    /// needs 90 ...".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ship_and_complementary_send_of_the_same_material_never_both_succeed(bool shipFirst)
    {
        await using var race = new ForcedOrder("Movements");
        var (scene, complementary) = await ApprovedComplementarySceneAsync(race.App);
        var number = await SalesOrderAsync(scene, 90m);
        Func<Task<HttpResponseMessage>> ship = () => SalesFlow.ShipAsync(scene.Manager, number);
        Func<Task<HttpResponseMessage>> send = () => SendComplementaryAsync(scene.Warehouse, complementary, (scene.Cheese.Id, 20m));

        var (shipped, sent) = await RunInOrderAsync(race, shipFirst, ship, send);

        if (shipFirst)
        {
            Assert.Equal(HttpStatusCode.OK, shipped.StatusCode);
            await ApiAssert.ProblemAsync(sent, HttpStatusCode.Conflict, "insufficient-stock",
                $"Not enough stock: {scene.Cheese.Name} in Warehouse has 10 {scene.Cheese.UnitName}.");
            Assert.Equal("Approved", (await ReadComplementaryAsync(scene.Manager, complementary)).GetProperty("status").GetString());
            Assert.Equal(0m, Line(await ReadOrderAsync(scene.Manager, scene.Number), scene.Cheese.Id).GetProperty("complementarySends").GetDecimal());
            await AssertWarehouseAsync(scene, expected: 10m, adjustments: 0);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal("Sent", (await ApiJson.ReadAsync(sent)).GetProperty("status").GetString());
            await AssertShipRefusedAsync(scene, shipped, number, available: 80m, needs: 90m);
            await AssertWarehouseAsync(scene, expected: 80m, adjustments: 0);
        }
    }

    /// <summary>
    /// AC-26.10 fourth bullet, PRD section 5: the ship of 600 and the −500 Warehouse adjustment really arrive at the same
    /// moment, three rounds, each on its own material. Exactly one succeeds; the loser gets <c>409</c>
    /// <c>insufficient-stock</c> naming what the winner left (400 or 500); the Warehouse never goes below zero and its
    /// movements add up to it (AC-11.4, AC-28.3).
    /// </summary>
    [Fact]
    public async Task Simultaneous_ship_and_write_off_never_both_succeed()
    {
        await using var app = new TestApiFactory();

        for (var round = 0; round < 3; round++)
        {
            var scene = await RaceSceneAsync(app, send: false);
            var number = await SalesOrderAsync(scene, 600m);

            var shipTask = SalesFlow.ShipAsync(scene.Warehouse, number);
            var writeOffTask = WriteOffAsync(scene.Manager, scene.Cheese.Id, 500m);
            await Task.WhenAll(shipTask, writeOffTask);
            var (ship, writeOff) = (await shipTask, await writeOffTask);

            var shipped = ship.StatusCode == HttpStatusCode.OK;
            Assert.True(shipped != (writeOff.StatusCode == HttpStatusCode.Created),
                $"Round {round}: ship {(int)ship.StatusCode}, write-off {(int)writeOff.StatusCode}; exactly one must succeed.");
            if (shipped)
            {
                await ApiAssert.ProblemAsync(writeOff, HttpStatusCode.Conflict, "insufficient-stock",
                    $"Not enough stock: {scene.Cheese.Name} in Warehouse has 400 {scene.Cheese.UnitName}.");
            }
            else
            {
                await AssertShipRefusedAsync(scene, ship, number, available: 500m, needs: 600m);
            }

            await AssertWarehouseAsync(scene, expected: shipped ? 400m : 500m, adjustments: shipped ? 0 : 1);
        }

        Assert.True(await AssertMovementsAddUpToStockAsync(await StartUpManagerAsync(app)) > 0);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// (v1.1) A Created sales order of <paramref name="quantity"/> of the scene's material, for a new customer, both made
    /// by the scene's Manager through the API (T-024, T-025). Returns its number.
    /// </summary>
    private static async Task<int> SalesOrderAsync(RaceScene scene, decimal quantity)
    {
        var customer = await SalesFlow.CreateCustomerAsync(scene.Manager);
        return await SalesFlow.CreateSalesOrderAsync(scene.Manager, customer.Id, (scene.Cheese.Id, quantity));
    }

    /// <summary>
    /// (v1.1) Runs the pair through <see cref="ForcedOrder.RunAsync"/> with <paramref name="ship"/> first or second, and
    /// returns the two responses as (ship, other) whatever the order, so the test reads the same either way.
    /// </summary>
    private static async Task<(HttpResponseMessage Ship, HttpResponseMessage Other)> RunInOrderAsync(
        ForcedOrder race, bool shipFirst, Func<Task<HttpResponseMessage>> ship, Func<Task<HttpResponseMessage>> other)
    {
        if (shipFirst)
        {
            return await race.RunAsync(ship, other);
        }

        var (first, second) = await race.RunAsync(other, ship);
        return (second, first);
    }

    /// <summary>
    /// (v1.1) AC-26.3, design 7.2: the ship lost: <c>409</c> <c>insufficient-stock</c> "Not enough stock: {item} in
    /// Warehouse has {available} {unit}; sales order #{number} needs {needs} {unit}.", no shipment movement, and the
    /// sales order still Created.
    /// </summary>
    private static async Task AssertShipRefusedAsync(
        RaceScene scene, HttpResponseMessage ship, int number, decimal available, decimal needs)
    {
        await ApiAssert.ProblemAsync(ship, HttpStatusCode.Conflict, "insufficient-stock",
            $"Not enough stock: {scene.Cheese.Name} in Warehouse has {available:0} {scene.Cheese.UnitName}; "
            + $"sales order #{number} needs {needs:0} {scene.Cheese.UnitName}.");
        Assert.Empty(await SalesFlow.ShipmentMovementsAsync(scene.Manager, number));
        Assert.Equal("Created", (await SalesFlow.ReadSalesOrderAsync(scene.Manager, number)).GetProperty("status").GetString());
    }

    /// <summary>
    /// <see cref="OrderFlow.RaceSceneAsync"/> with the order sent (1,000, so the Warehouse is empty), then 100 more
    /// received, and a complementary order of 20 requested by Production and approved by the Manager, ready to send.
    /// </summary>
    private static async Task<(RaceScene Scene, int Complementary)> ApprovedComplementarySceneAsync(TestApiFactory app)
    {
        var scene = await RaceSceneAsync(app, send: true);
        await ReceiveAsync(scene.Manager, scene.Cheese.Id, 100m);
        var complementary = await ApprovedComplementaryAsync(
            scene.Production, scene.Manager, scene.Number, "Hopper spill at start of run", (scene.Cheese.Id, 20m));
        return (scene, complementary);
    }

    /// <summary>A Warehouse write-off of <paramref name="quantity"/> through <c>POST /adjustments</c>, unchecked.</summary>
    private static Task<HttpResponseMessage> WriteOffAsync(HttpClient manager, int itemId, decimal quantity) =>
        manager.PostAsJsonAsync($"{ApiPath}/adjustments",
            new { itemId, location = "Warehouse", quantity = -quantity, reason = "Damaged in the warehouse" }, Cancel);

    /// <summary>
    /// The Warehouse stock of the scene's material is <paramref name="expected"/> (never below zero), equals the sum of
    /// its movements' Warehouse changes (AC-11.4), and exactly <paramref name="adjustments"/> Adjustment movements exist.
    /// </summary>
    private static async Task AssertWarehouseAsync(RaceScene scene, decimal expected, int adjustments)
    {
        var stock = (await StockAsync(scene.Manager, scene.Cheese.Id)).GetProperty("warehouse").GetDecimal();
        var movements = await AllRowsAsync(scene.Manager, $"{ApiPath}/movements?itemId={scene.Cheese.Id}");

        Assert.Equal(expected, stock);
        Assert.True(stock >= 0m);
        Assert.Equal(stock, movements.Sum(movement => NullableDecimal(movement.GetProperty("warehouseChange")) ?? 0m));
        Assert.Equal(adjustments, movements.Count(movement => movement.GetProperty("type").GetString() == "Adjustment"));
    }
}
