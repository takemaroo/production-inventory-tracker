using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// (v1.1) The steps of a sales order's life that many integration tests need as set-up (architecture 7, "Shared helpers";
/// decision #101): a customer to sell to, a sales order created, read (T-025) and shipped (T-027) through the API, and a
/// sales order written straight to the database in a status no endpoint produces in one step. One shared copy, so the
/// customer, sales order, shipment and report tests (T-024 to T-031) all set up their records the same way, and a fix
/// here reaches every one of them. T-028 adds the cancel call.
/// </summary>
/// <remarks>
/// <para>
/// Test classes import it with <c>using static InventoryTracker.Api.Tests.Infrastructure.SalesFlow;</c>, like
/// <see cref="OrderFlow"/>, so a test can write <c>CreateCustomerAsync(...)</c> without the class name.
/// </para>
/// <para>
/// HTTP helpers check the status they expect, so a broken set-up fails at the line that caused it (like
/// <see cref="Seed"/>). The database helper runs inside <c>InWriteTransactionAsync</c>, as a real service must
/// (architecture 5.2), through <see cref="OrderFlow.InTransactionAsync"/>.
/// </para>
/// </remarks>
public static class SalesFlow
{
    /// <summary>The base path of the customer endpoints (design 4).</summary>
    public const string CustomersPath = "/api/v1/customers";

    /// <summary>(T-025) The base path of the sales order endpoints (design 4).</summary>
    public const string SalesOrdersPath = "/api/v1/sales-orders";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates a customer through <c>POST /api/v1/customers</c> (T-024) and checks it got <c>201</c>.
    /// <paramref name="client"/> must hold the Manager or Seller role. Without a <paramref name="name"/>, the customer
    /// gets a unique one; pass an exact name (such as "Deli Norte") when a test checks a message that quotes it, and it is
    /// checked to come back exactly as typed.
    /// </summary>
    public static async Task<CreatedCustomer> CreateCustomerAsync(HttpClient client, string? name = null, string? notes = null)
    {
        var response = await client.PostAsJsonAsync(CustomersPath, new { name = name ?? Seed.UniqueName("Customer"), notes }, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiJson.ReadAsync(response);
        if (name is not null)
        {
            // Tests build expected messages from the name; a change the API made to it must fail here, not later.
            Assert.Equal(name, body.GetProperty("name").GetString());
        }

        return new CreatedCustomer(body.GetProperty("id").GetInt32(), body.GetProperty("name").GetString()!);
    }

    /// <summary>
    /// Sends <c>POST /api/v1/sales-orders</c> (T-025) for <paramref name="customerId"/> with <paramref name="lines"/> in the
    /// order given, and returns the response unchecked, for tests that check a refusal or race two requests.
    /// <paramref name="client"/> needs the Manager or Seller role to succeed.
    /// </summary>
    public static Task<HttpResponseMessage> PostSalesOrderAsync(
        HttpClient client, int customerId, params (int ItemId, decimal Quantity)[] lines) =>
        client.PostAsJsonAsync(SalesOrdersPath,
            new { customerId, lines = lines.Select(line => new { itemId = line.ItemId, quantity = line.Quantity }) }, Cancel);

    /// <summary>
    /// Creates a sales order through <c>POST /api/v1/sales-orders</c> (T-025) for <paramref name="customerId"/> with
    /// <paramref name="lines"/>, checks it got <c>201</c>, and returns its number. <paramref name="client"/> must hold the
    /// Manager or Seller role, and the customer must be active (AC-24.4).
    /// </summary>
    public static async Task<int> CreateSalesOrderAsync(
        HttpClient client, int customerId, params (int ItemId, decimal Quantity)[] lines)
    {
        var response = await PostSalesOrderAsync(client, customerId, lines);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Creating a sales order gave {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Cancel)}");
        return (await ApiJson.ReadAsync(response)).GetProperty("number").GetInt32();
    }

    /// <summary>
    /// Reads a sales order's detail through <c>GET /api/v1/sales-orders/{number}</c> (T-025) and checks it got
    /// <c>200</c>. <paramref name="client"/> must hold the Warehouse, Manager or Seller role.
    /// </summary>
    public static async Task<JsonElement> ReadSalesOrderAsync(HttpClient client, int number)
    {
        var response = await client.GetAsync($"{SalesOrdersPath}/{number}", Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiJson.ReadAsync(response);
    }

    /// <summary>
    /// Ships a sales order through <c>POST /api/v1/sales-orders/{number}/ship</c> (T-027) with <c>{ "note": ... }</c>, and
    /// returns the response unchecked, because the ship is often the request under test or one side of a race.
    /// <paramref name="client"/> needs the Warehouse or Manager role to succeed (AC-26.9). A test of the other body forms
    /// (no body, <c>{}</c>) sends its own request.
    /// </summary>
    public static Task<HttpResponseMessage> ShipAsync(HttpClient client, int number, string? note = null) =>
        client.PostAsJsonAsync($"{SalesOrdersPath}/{number}/ship", new { note }, Cancel);

    /// <summary>
    /// Inserts a sales order for <paramref name="customerId"/> with one line (<paramref name="quantity"/> of
    /// <paramref name="itemId"/>), straight in <paramref name="status"/>, created now by <paramref name="userId"/>, in a
    /// write transaction of its own, and returns its number.
    /// </summary>
    /// <remarks>
    /// For states the API cannot produce yet, or not in one step: T-024 needs "a customer with a Cancelled sales order"
    /// before the sales order endpoints exist (AC-29.5), and T-027 a Cancelled one before the cancel endpoint exists
    /// (T-028). A Shipped or Cancelled one gets the who-and-when pair of that step (AC-05.3) and, for Cancelled, a reason,
    /// so the row looks like one the endpoints would leave. A Shipped one gets <b>no</b> shipment movement: use
    /// <see cref="ShipAsync"/> when stock must move. <c>StatusChangedAt</c> is the time of the latest step, as
    /// architecture 5.1 sets it.
    /// </remarks>
    public static async Task<int> InsertSalesOrderAsync(
        TestApiFactory app, int customerId, int itemId, decimal quantity, SalesOrderStatus status, string userId)
    {
        var number = 0;
        await OrderFlow.InTransactionAsync(app, async (db, _, now) =>
        {
            var salesOrder = new SalesOrder
            {
                CustomerId = customerId,
                Status = status,
                CreatedAt = now,
                CreatedById = userId,
                StatusChangedAt = now,
                Lines = [new SalesOrderLine { LineNo = 1, ItemId = itemId, Quantity = quantity }],
            };
            switch (status)
            {
                case SalesOrderStatus.Shipped:
                    (salesOrder.ShippedAt, salesOrder.ShippedById) = (now, userId);
                    break;
                case SalesOrderStatus.Cancelled:
                    (salesOrder.CancelledAt, salesOrder.CancelledById, salesOrder.CancellationReason) =
                        (now, userId, "Customer changed the order");
                    break;
            }

            db.SalesOrders.Add(salesOrder);

            // The number is generated by the database; save (still inside the transaction) to read it.
            await db.SaveChangesAsync(Cancel);
            number = salesOrder.Number;
        });
        return number;
    }
}

/// <summary>A customer created through the API by <see cref="SalesFlow.CreateCustomerAsync"/>: its id and stored name.</summary>
public sealed record CreatedCustomer(int Id, string Name);
