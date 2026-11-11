using System.Text.Json;
using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Domain;
using InventoryTracker.Api.Domain.Calculations;

namespace InventoryTracker.Api.Features.Notifications;

/// <summary>
/// Builds the five kinds of notification (US-23, AC-23.2): the message text from design 7.3 and the structured
/// <c>data</c> object from design 5.10. One method per <see cref="NotificationType"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every method is a pure function: it reads only its arguments, touches no database or clock, and returns a new
/// <see cref="Notification"/> that has not been saved. The services that raise notifications (completion T-011,
/// complementary request T-017, cancellation T-016) add it to the DbContext inside their own write transaction,
/// so a notification exists exactly when its event does (architecture 5.2). Keeping the wording here, in one
/// place, means every notification of a type reads the same and the texts can be unit-tested without a database.
/// </para>
/// <para>
/// The methods decide <em>what a notification says</em>, not <em>whether</em> one is raised. The caller makes
/// that decision (AC-19.7, AC-20.2, AC-20.4, AC-21.3). A call that could only produce a wrong notification, such
/// as a usage variance within tolerance or a leftover list where nothing is above zero, throws
/// <see cref="ArgumentException"/>, because it is a programming error and must not be saved quietly.
/// </para>
/// </remarks>
public static class NotificationComposer
{
    /// <summary>
    /// How the <c>data</c> object is written to <see cref="Notification.DataJson"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializerDefaults.Web"/> gives camelCase field names (design 2.3), so the C# property
    /// <c>RequiredForActualOutput</c> is written as <c>requiredForActualOutput</c>. Null values are written, not
    /// left out, because every response field is always present (design 2.3). Decimals are written as JSON numbers
    /// with their exact digits; no floating point is involved. The default encoder escapes characters such as
    /// quotes and accented letters (<c>é</c> becomes the six characters <c>\u00E9</c>); the result is still the same JSON value.
    /// </remarks>
    private static readonly JsonSerializerOptions DataJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A UsageVariance notification (AC-20.4): one material of a completed order whose usage variance is above
    /// the tolerance, in either direction (decision #12).
    /// </summary>
    /// <param name="orderNumber">The completed production order.</param>
    /// <param name="material">The raw material of the line.</param>
    /// <param name="completion">The line's D-9 figures from <see cref="ProductionCalculations.CompleteLine"/>.</param>
    /// <param name="createdAt">The UTC time of the completion, from <c>Clock</c>.</param>
    /// <exception cref="ArgumentException">The line is not above the tolerance, so no notification may be raised.</exception>
    public static Notification UsageVariance(
        int orderNumber, NotificationItem material, LineCompletion completion, DateTime createdAt)
    {
        // AC-20.4: only lines above the tolerance notify. The flag was decided on the unrounded percentage
        // (decision #47), so it is trusted here rather than re-judged from a rounded number.
        if (!completion.AboveTolerance)
        {
            throw new ArgumentException(
                $"Order #{orderNumber}, item {material.ItemId}: usage is within tolerance, so no UsageVariance notification is raised.",
                nameof(completion));
        }

        // PRD 4.1, design 2.4: computed quantities are shown rounded to 3 places. The data uses the same rounded
        // figures as the message, so the two can never disagree.
        var required = Quantity.Round3(completion.RequiredForActualOutput);
        var actualUsage = Quantity.Round3(completion.ActualUsage);
        var difference = Quantity.Round3(completion.UsageVariance);
        var percent = RoundPercentForDisplay(completion.UsageVariancePercent);

        // Decision #49 and #57: when the required amount rounds to 0 the percentage has no value (it would divide
        // by zero). The message then says so in words, and the data's "percent" is null.
        var percentText = percent is null ? "percentage not applicable" : MessageFormat.SignedPercent(percent.Value);

        var message =
            $"Order #{orderNumber}, {material.ItemName}: used {MessageFormat.Number(actualUsage)} {material.Unit}, " +
            $"required {MessageFormat.Number(required)} {material.Unit} for actual output. " +
            $"Difference {MessageFormat.SignedNumber(difference)} {material.Unit} ({percentText}).";

        var data = new UsageVarianceData(
            material.ItemId, material.ItemName, material.Unit, required, actualUsage, difference, percent);

        return Create(NotificationType.UsageVariance, orderNumber, complementaryOrderNumber: null, message, data, createdAt);
    }

    /// <summary>
    /// An OutputVariance notification (AC-20.2): the produced quantity differs from the ordered quantity.
    /// </summary>
    /// <remarks>
    /// The variance is worked out here with <see cref="ProductionCalculations.OutputVariance"/> from the two
    /// quantities, rather than passed in, so the difference shown can never disagree with ordered and produced.
    /// </remarks>
    /// <param name="orderNumber">The completed production order.</param>
    /// <param name="finishedProduct">The order's finished product.</param>
    /// <param name="orderedQuantity">The quantity the order asked for.</param>
    /// <param name="producedQuantity">The quantity entered at completion.</param>
    /// <param name="createdAt">The UTC time of the completion, from <c>Clock</c>.</param>
    /// <exception cref="ArgumentException">Produced equals ordered, so no notification may be raised.</exception>
    public static Notification OutputVariance(
        int orderNumber, NotificationItem finishedProduct, decimal orderedQuantity, decimal producedQuantity,
        DateTime createdAt)
    {
        var variance = ProductionCalculations.OutputVariance(orderedQuantity, producedQuantity);

        // AC-20.2: any difference at all notifies (output has no tolerance); a zero difference creates none.
        if (!variance.RaisesNotification)
        {
            throw new ArgumentException(
                $"Order #{orderNumber}: produced equals ordered, so no OutputVariance notification is raised.",
                nameof(producedQuantity));
        }

        // Same rounded figures in the message and the data (PRD 4.1, design 2.4).
        var ordered = Quantity.Round3(orderedQuantity);
        var produced = Quantity.Round3(producedQuantity);
        var difference = Quantity.Round3(variance.Variance);

        // The ordered quantity is never 0 (validated at order creation), so this percentage always has a value.
        var percent = RoundPercentForDisplay(variance.Percent);

        var message =
            $"Order #{orderNumber}: produced {MessageFormat.Number(produced)} {finishedProduct.Unit}, " +
            $"ordered {MessageFormat.Number(ordered)} {finishedProduct.Unit}. " +
            $"Difference {MessageFormat.SignedNumber(difference)} {finishedProduct.Unit} ({MessageFormat.SignedPercent(percent)}).";

        var data = new OutputVarianceData(
            finishedProduct.ItemId, finishedProduct.ItemName, finishedProduct.Unit, ordered, produced, difference, percent);

        return Create(NotificationType.OutputVariance, orderNumber, complementaryOrderNumber: null, message, data, createdAt);
    }

    /// <summary>
    /// A LeftoverAtCompletion notification (AC-19.7): an order was completed with leftover in production storage.
    /// </summary>
    /// <param name="orderNumber">The completed production order.</param>
    /// <param name="leftovers">
    /// The leftover entered for each of the order's lines. Lines with 0 are left out (AC-19.7 lists only materials
    /// with leftover above zero), so the caller may pass every line.
    /// </param>
    /// <param name="reason">The completion reason, quoted as entered (design 7.3).</param>
    /// <param name="createdAt">The UTC time of the completion, from <c>Clock</c>.</param>
    /// <exception cref="ArgumentException">No material has leftover above zero, so no notification may be raised.</exception>
    public static Notification LeftoverAtCompletion(
        int orderNumber, IEnumerable<NotificationMaterial> leftovers, string reason, DateTime createdAt)
    {
        var materials = AboveZeroInLineOrder(leftovers, orderNumber, NotificationType.LeftoverAtCompletion, nameof(leftovers));

        var message =
            $"Order #{orderNumber} was completed with leftover in production storage: {MaterialList(materials)}. " +
            $"Reason: \"{reason}\"";

        var data = new LeftoverAtCompletionData(
            materials.Select(m => new LeftoverLine(m.ItemId, m.ItemName, m.Unit, m.Quantity)).ToList(), reason);

        return Create(NotificationType.LeftoverAtCompletion, orderNumber, complementaryOrderNumber: null, message, data, createdAt);
    }

    /// <summary>
    /// A ComplementaryAwaitingApproval notification (AC-16.1): a complementary order was requested and waits for a
    /// Manager to approve or reject it.
    /// </summary>
    /// <param name="orderNumber">The parent production order.</param>
    /// <param name="complementaryOrderNumber">The new complementary order; also stored on the notification itself.</param>
    /// <param name="requested">
    /// The requested amount of each material. <see cref="NotificationMaterial.LineNo"/> must be the material's
    /// position on the <em>parent order</em>, so the list follows recipe line order (design 7.3).
    /// </param>
    /// <param name="reason">The request's reason, quoted as entered.</param>
    /// <param name="requestedBy">The user who raised the request.</param>
    /// <param name="createdAt">The UTC time of the request, from <c>Clock</c>.</param>
    /// <exception cref="ArgumentException">No material was requested.</exception>
    public static Notification ComplementaryAwaitingApproval(
        int orderNumber, int complementaryOrderNumber, IEnumerable<NotificationMaterial> requested, string reason,
        NotificationUser requestedBy, DateTime createdAt)
    {
        // AC-16.1 already requires every requested amount to be above zero, so this only ever drops nothing; it
        // keeps the "list only real quantities" rule the same for all three list types.
        var materials = AboveZeroInLineOrder(requested, orderNumber, NotificationType.ComplementaryAwaitingApproval, nameof(requested));

        var message =
            $"Complementary order #{complementaryOrderNumber} for order #{orderNumber} is waiting for approval. " +
            $"{requestedBy.Email} requested {MaterialList(materials)}. Reason: \"{reason}\"";

        var data = new ComplementaryAwaitingApprovalData(
            materials.Select(m => new RequestedLine(m.ItemId, m.ItemName, m.Unit, m.Quantity)).ToList(),
            reason,
            new RequestedByData(requestedBy.Id, requestedBy.Email));

        return Create(NotificationType.ComplementaryAwaitingApproval, orderNumber, complementaryOrderNumber, message, data, createdAt);
    }

    /// <summary>
    /// An OrderCancelledWithMaterial notification (AC-21.3): an InProduction order was cancelled while its
    /// materials were still in production storage.
    /// </summary>
    /// <param name="orderNumber">The cancelled production order.</param>
    /// <param name="productionStorage">
    /// The production storage stock of each of the order's materials. Materials with 0 are left out (AC-21.3 lists
    /// only materials with stock above zero), so the caller may pass every line.
    /// </param>
    /// <param name="reason">The cancellation reason, quoted as entered.</param>
    /// <param name="createdAt">The UTC time of the cancellation, from <c>Clock</c>.</param>
    /// <exception cref="ArgumentException">No material has stock above zero, so no notification may be raised.</exception>
    public static Notification OrderCancelledWithMaterial(
        int orderNumber, IEnumerable<NotificationMaterial> productionStorage, string reason, DateTime createdAt)
    {
        var materials = AboveZeroInLineOrder(productionStorage, orderNumber, NotificationType.OrderCancelledWithMaterial, nameof(productionStorage));

        var message =
            $"Order #{orderNumber} was cancelled with material still in production storage: {MaterialList(materials)}. " +
            $"Reason: \"{reason}\"";

        var data = new OrderCancelledWithMaterialData(
            materials.Select(m => new ProductionStorageLine(m.ItemId, m.ItemName, m.Unit, m.Quantity)).ToList(), reason);

        return Create(NotificationType.OrderCancelledWithMaterial, orderNumber, complementaryOrderNumber: null, message, data, createdAt);
    }

    /// <summary>Fills in the entity. Serializing the data here means no caller can store it with the wrong field names.</summary>
    private static Notification Create(
        NotificationType type, int orderNumber, int? complementaryOrderNumber, string message, object data, DateTime createdAt)
        => new()
        {
            Type = type,
            ProductionOrderNumber = orderNumber,
            ComplementaryOrderNumber = complementaryOrderNumber,
            CreatedAt = createdAt,
            Message = message,
            // data.GetType() makes the serializer write the record's own fields; with "object" it would write none.
            DataJson = JsonSerializer.Serialize(data, data.GetType(), DataJsonOptions),
        };

    /// <summary>
    /// PRD 4.1, design 2.4: percentages are shown rounded to 2 places, half away from zero. Null stays null
    /// (decision #49).
    /// </summary>
    private static decimal? RoundPercentForDisplay(decimal? percent)
        => percent is null ? null : RoundPercentForDisplay(percent.Value);

    /// <summary>
    /// Decision #58: a displayed percentage is the <em>stored</em> 6-place value rounded to 2 places, both steps
    /// half away from zero.
    /// </summary>
    /// <remarks>
    /// The order line keeps the percentage rounded to 6 places (architecture 3.1), and later order responses can
    /// only show that stored value rounded to 2. Rounding the unrounded value straight to 2 places here could
    /// then disagree by 0.01: 2.0049996 is stored as 2.005000, which an order response shows as 2.01, while a
    /// direct rounding would give 2.00. Taking the same two steps here means a notification and the order it is
    /// about always show the same percentage. The above-tolerance decision never uses this value (AC-20.4).
    /// </remarks>
    private static decimal RoundPercentForDisplay(decimal percent)
    {
        var stored = Math.Round(percent, 6, MidpointRounding.AwayFromZero);
        return Math.Round(stored, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Keeps only materials with a quantity above zero, rounded to 3 places, in recipe line order (design 7.3).
    /// Throws when nothing is left, because a notification listing no material would be wrong.
    /// </summary>
    /// <param name="materials">The list the public method received.</param>
    /// <param name="orderNumber">The order, for the error text.</param>
    /// <param name="type">The notification type, for the error text.</param>
    /// <param name="parameterName">
    /// The public method's parameter name (for example <c>leftovers</c>), so the exception names the argument the
    /// caller actually passed.
    /// </param>
    private static List<NotificationMaterial> AboveZeroInLineOrder(
        IEnumerable<NotificationMaterial> materials, int orderNumber, NotificationType type, string parameterName)
    {
        var kept = materials
            // "m with { ... }" copies the record with one property changed; the caller's record is left as it was.
            .Select(m => m with { Quantity = Quantity.Round3(m.Quantity) })
            .Where(m => m.Quantity > 0m)
            .OrderBy(m => m.LineNo)
            .ToList();

        if (kept.Count == 0)
        {
            throw new ArgumentException(
                $"Order #{orderNumber}: a {type} notification needs at least one material with a quantity above zero.",
                parameterName);
        }

        return kept;
    }

    /// <summary>Design 7.3: "{item} {quantity} {unit}" for each material, separated by commas: "Cheese 50 kg, Potato starch 0.75 kg".</summary>
    private static string MaterialList(IEnumerable<NotificationMaterial> materials)
        => string.Join(", ", materials.Select(m => $"{m.ItemName} {MessageFormat.Number(m.Quantity)} {m.Unit}"));

    // ---- The design 5.10 "data" objects -------------------------------------------------------------------
    // One record per type. A record's properties are serialized in the order they are declared, which is the
    // order of the design 5.10 table. They are private: other code reads DataJson as JSON, not as these types.

    private sealed record UsageVarianceData(
        int ItemId, string ItemName, string Unit, decimal RequiredForActualOutput, decimal ActualUsage,
        decimal Difference, decimal? Percent);

    private sealed record OutputVarianceData(
        int FinishedProductId, string FinishedProductName, string Unit, decimal Ordered, decimal Produced,
        decimal Difference, decimal Percent);

    private sealed record LeftoverAtCompletionData(IReadOnlyList<LeftoverLine> Materials, string Reason);

    private sealed record LeftoverLine(int ItemId, string ItemName, string Unit, decimal Leftover);

    private sealed record ComplementaryAwaitingApprovalData(
        IReadOnlyList<RequestedLine> Materials, string Reason, RequestedByData RequestedBy);

    private sealed record RequestedLine(int ItemId, string ItemName, string Unit, decimal RequestedAmount);

    private sealed record RequestedByData(string Id, string Email);

    private sealed record OrderCancelledWithMaterialData(IReadOnlyList<ProductionStorageLine> Materials, string Reason);

    private sealed record ProductionStorageLine(int ItemId, string ItemName, string Unit, decimal ProductionStorageQuantity);
}

/// <summary>
/// An item as a notification names it: id, name and unit name (design 2.4: the unit travels with the quantity).
/// Input to <see cref="NotificationComposer"/>.
/// </summary>
/// <param name="ItemId">The item's id.</param>
/// <param name="ItemName">The item's name, as shown in messages ("Cheese").</param>
/// <param name="Unit">The name of the item's unit ("kg", "Bag"), never pluralised (design 6.1).</param>
public sealed record NotificationItem(int ItemId, string ItemName, string Unit);

/// <summary>
/// One material and a quantity of it, for the notifications that list materials. Input to
/// <see cref="NotificationComposer"/>.
/// </summary>
/// <param name="LineNo">
/// The material's position on the production order, which is copied from the recipe. Lists are sorted by it, so
/// they follow recipe line order (design 7.3) whatever order the caller passes them in.
/// </param>
/// <param name="ItemId">The raw material's id.</param>
/// <param name="ItemName">The raw material's name.</param>
/// <param name="Unit">The name of the raw material's unit.</param>
/// <param name="Quantity">The leftover, requested amount or production storage stock, depending on the notification.</param>
public sealed record NotificationMaterial(int LineNo, int ItemId, string ItemName, string Unit, decimal Quantity);

/// <summary>
/// A user as notifications show them: id and email (design 2.3, "People"). Input to
/// <see cref="NotificationComposer.ComplementaryAwaitingApproval"/>.
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="Email">The user's email address.</param>
public sealed record NotificationUser(string Id, string Email);
