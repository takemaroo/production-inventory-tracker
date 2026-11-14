using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// The one check of a request body's <c>lines</c> against the lines of a production order (decision #100): every order
/// line must appear exactly once, each with one quantity. Used by the send (T-010, "quantity") and the completion
/// (T-011, "leftover"), and meant for any later body that carries one figure per order line.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it checks, per submitted line, in body order</b> (design 2.2 step 4, design 7.2, decisions #74, #75, #96):
/// </para>
/// <list type="bullet">
/// <item>The item: given ("Item is required."), on this order ("Item {id} is not on order #{n}.", also for an id that
/// names no item at all: either way it is not one of this order's materials, so <c>400</c>, not <c>404</c>), and not
/// already listed ("{item} appears on more than one line.": the first line naming the material is kept and each later
/// one is reported, so the message points at the line to remove).</item>
/// <item>The quantity, in the order of architecture 3.1, one message per field: required ("{Label} is required."), not
/// negative ("{Label} cannot be negative."; zero is allowed), at most 3 decimal places (decision #95; trailing zeros do
/// not count, design 2.4), and at most 999,999,999.999 (decision #80).</item>
/// </list>
/// <para>
/// Then the order lines that no submitted line named are reported together under <c>lines</c>, in recipe line order:
/// "Every order line needs a quantity. Missing: Cheese, Potato starch." (design 7.2, "Missing line (send or complete)";
/// decision #96). A line that was present with a bad quantity is not missing; its own message says what is wrong.
/// </para>
/// <para>
/// The messages are added to the caller's <see cref="ValidationErrors"/>, so they are reported in one <c>400</c> together
/// with the caller's other field errors (for example the produced quantity). The caller throws.
/// </para>
/// </remarks>
public static class OrderLineMatcher
{
    /// <summary>
    /// Checks <paramref name="submitted"/> against <paramref name="order"/>'s lines, adding every problem to
    /// <paramref name="errors"/>, and returns the lines that passed.
    /// </summary>
    /// <param name="order">The order, with its <c>Lines</c> and each line's <c>RawMaterial</c> loaded (for the names).</param>
    /// <param name="submitted">The body's lines, in the order the client sent them; a <c>null</c> element is a line sent as <c>null</c>.</param>
    /// <param name="quantityField">The JSON name of the quantity, for the error path: <c>quantity</c> or <c>leftover</c>.</param>
    /// <param name="quantityLabel">The quantity's name in messages: <c>Quantity</c> or <c>Leftover</c> (design 7.2).</param>
    /// <param name="errors">The caller's collector.</param>
    /// <returns>The valid lines, in body order, each with its position in the request (for later error paths).</returns>
    public static List<MatchedOrderLine> Match(
        ProductionOrder order,
        IReadOnlyList<SubmittedOrderLine?> submitted,
        string quantityField,
        string quantityLabel,
        ValidationErrors errors)
    {
        var orderLines = order.Lines.ToDictionary(line => line.RawMaterialId);
        var matched = new List<MatchedOrderLine>(submitted.Count);
        var materialsSeen = new HashSet<int>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var line = submitted[index];
            var itemPath = $"lines[{index}].itemId";
            var quantityPath = $"lines[{index}].{quantityField}";

            // A line sent as null gets both "required" messages, as in recipes (decision #74).
            ProductionOrderLine? orderLine = null;
            if (line?.ItemId is not { } itemId)
            {
                errors.Add(itemPath, "Item is required.");
            }
            else if (!orderLines.TryGetValue(itemId, out orderLine))
            {
                errors.Add(itemPath, $"Item {itemId} is not on order #{order.Number}.");
            }
            else if (!materialsSeen.Add(itemId))
            {
                errors.Add(itemPath, $"{orderLine.RawMaterial.Name} appears on more than one line.");
                orderLine = null;
            }

            var quantityValid = false;
            if (line?.Quantity is not { } quantity)
            {
                errors.Add(quantityPath, $"{quantityLabel} is required.");
            }
            else if (quantity < 0m)
            {
                // AC-15.8 (send, D-7 "S ≥ 0") and AC-19.4 (completion, "a negative leftover"). Zero is allowed in both: a
                // send of 0 where leftover covers the need (AC-15.4), and nothing left at the end (AC-19.1).
                errors.Add(quantityPath, $"{quantityLabel} cannot be negative.");
            }
            else if (!Quantity.HasAtMostDecimals(quantity, Quantity.MaxQuantityDecimals))
            {
                // PRD 4.1; the message names the field (decision #95).
                errors.Add(quantityPath, $"{quantityLabel} can have at most {Quantity.MaxQuantityDecimals} decimal places.");
            }
            else if (!Quantity.IsWithinMax(quantity))
            {
                // Decision #80: a field error, reported before any range or stock check.
                errors.Add(quantityPath, Quantity.AboveMaxMessage(quantityLabel));
            }
            else
            {
                quantityValid = true;
            }

            if (orderLine is not null && quantityValid)
            {
                matched.Add(new MatchedOrderLine(index, orderLine, line!.Quantity!.Value));
            }
        }

        var missing = order.Lines
            .Where(line => !materialsSeen.Contains(line.RawMaterialId))
            .OrderBy(line => line.LineNo)
            .Select(line => line.RawMaterial.Name)
            .ToList();
        if (missing.Count > 0)
        {
            errors.Add("lines", $"Every order line needs a quantity. Missing: {string.Join(", ", missing)}.");
        }

        return matched;
    }
}

/// <summary>
/// One line of a request body as <see cref="OrderLineMatcher"/> reads it: the item and its one quantity (a send's
/// <c>quantity</c>, a completion's <c>leftover</c>). Both are nullable so a missing value gets the design's message.
/// </summary>
/// <param name="ItemId">The raw material the line is for.</param>
/// <param name="Quantity">The line's figure.</param>
public sealed record SubmittedOrderLine(int? ItemId, decimal? Quantity);

/// <summary>A submitted line that passed <see cref="OrderLineMatcher.Match"/>.</summary>
/// <param name="Index">Its position in the request body, for the JSON path of later errors (<c>lines[i]…</c>).</param>
/// <param name="Line">The order line it names.</param>
/// <param name="Quantity">Its valid quantity (a send) or leftover (a completion).</param>
public sealed record MatchedOrderLine(int Index, ProductionOrderLine Line, decimal Quantity);
