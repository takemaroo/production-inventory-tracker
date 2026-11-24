namespace InventoryTracker.Api.Common.Contracts;

/// <summary>
/// (v1.1) A customer as it appears inside another record, such as a sales order or a report row (design 2.3,
/// "Customers inside other records"): <c>{ "id": 1, "name": "Deli Norte", "isActive": true }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is read, never copied</b> (AC-29.3, architecture 4.1 item 17). A sales order stores only its customer's id.
/// Every response builds this summary from the <c>Customers</c> row at the moment of the request, by a join. So after a
/// customer is renamed, every sales order, old ones included, shows the new name, and after a deactivation every one
/// shows <c>isActive: false</c> (AC-29.4). No old name is kept anywhere.
/// </para>
/// <para>
/// It lives in <c>Common/Contracts</c>, beside <see cref="PersonResponse"/>, because the sales orders (T-025) and the
/// shipped-totals reports (T-030) both show it, and one shape keeps them alike (decision #161).
/// </para>
/// </remarks>
/// <param name="Id">The customer's id.</param>
/// <param name="Name">The customer's current name, as stored (AC-29.3).</param>
/// <param name="IsActive">False once the customer is deactivated (AC-29.4).</param>
public sealed record CustomerSummary(int Id, string Name, bool IsActive);
