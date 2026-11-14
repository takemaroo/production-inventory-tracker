using System.Text.Json;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.Notifications;

/// <summary>
/// A notification as the API returns it (design 5.10, AC-23.1): the row of <c>GET /notifications</c> and the body of
/// <c>GET /notifications/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The fields are declared in the order of the design 5.10 example, and the JSON is written in that order.
/// </para>
/// <para>
/// <see cref="Data"/> is a <see cref="JsonElement"/>: a read-only piece of already-parsed JSON. Its shape differs per
/// <see cref="Type"/> (design 5.10 table), so no single C# record could describe it. The serializer writes a
/// <see cref="JsonElement"/> back out as the JSON it holds, field names and number digits unchanged, so the client gets
/// a real JSON object (not a string that contains JSON) with exactly the values that were stored when the
/// notification was raised (<c>Notification.DataJson</c>, written by <see cref="NotificationComposer"/>).
/// </para>
/// </remarks>
/// <param name="Id">The notification's id; its URL is <c>/api/v1/notifications/{id}</c>.</param>
/// <param name="Type">Which of the five kinds it is (AC-23.2), written as its name.</param>
/// <param name="OrderNumber">The production order it is about. Every type relates to an order.</param>
/// <param name="ComplementaryOrderNumber">The complementary order, for ComplementaryAwaitingApproval; otherwise null.</param>
/// <param name="CreatedAt">When it was raised, UTC, whole seconds.</param>
/// <param name="Message">The finished text of design 7.3.</param>
/// <param name="IsRead">AC-23.3: whether the <em>calling</em> Manager has marked it read. Other Managers may see a different value.</param>
/// <param name="Data">The structured values of design 5.10 for this type, as a JSON object.</param>
public sealed record NotificationResponse(
    int Id,
    NotificationType Type,
    int OrderNumber,
    int? ComplementaryOrderNumber,
    DateTime CreatedAt,
    string Message,
    bool IsRead,
    JsonElement Data);
