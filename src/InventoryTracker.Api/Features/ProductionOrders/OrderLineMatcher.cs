using InventoryTracker.Api.Common.Quantities;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Features.ProductionOrders;

/// <summary>
/// The one check of a request body's <c>lines</c> against the lines of a production order (decision #100): each line
/// names one of the order's materials, at most once, with one quantity. Used by the send (T-010, "quantity") and the
/// completion (T-011, "leftover"), where every order line must appear and zero is allowed, and by the complementary
/// order request (T-017, "requestedAmount"), which names only some of the materials, each above zero
/// (<see cref="OrderLineRules"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it checks, per submitted line, in body order</b> (design 2.2 step 4, design 7.2, decisions #74, #75, #96):
/// </para>
/// <list type="bullet">
/// <item>The item: given ("Item is required."), on this order ("Item {id} is not on order #{n}.", also for an id that
/// names no item at all: either way it is not one of this order's materials, so <c>400</c>, not <c>404</c>; the
/// complementary request words it with the item's name, <see cref="OrderLineRules.NotOnOrderMessage"/>), and not
/// already listed ("{item} appears on more than one line.": the first line naming the material is kept and each later
/// one is reported, so the message points at the line to remove).</item>
/// <item>The quantity, in the order of architecture 3.1, one message per field: required ("{Label} is required."), the
/// sign rule (send and completion: "{Label} cannot be negative.", zero allowed; complementary request: "{Label} must be
/// greater than 0."), at most 3 decimal places (decision #95; trailing zeros do not count, design 2.4), and at most
/// 999,999,999.999 (decision #80).</item>
/// </list>
/// <para>
/// Then, for the send and the completion, the order lines that no submitted line named are reported together under
/// <c>lines</c>, in recipe line order: "Every order line needs a quantity. Missing: Cheese, Potato starch." (design 7.2,
/// "Missing line (send or complete)"; decision #96). A line that was present with a bad quantity is not missing; its own
/// message says what is wrong. A complementary request skips this step, because it asks only for the materials that ran
/// short (US-16).
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
    /// <param name="quantityField">
    /// The JSON name of the quantity, for the error path: <c>quantity</c>, <c>leftover</c> or <c>requestedAmount</c>.
    /// </param>
    /// <param name="quantityLabel">
    /// The quantity's name in messages: <c>Quantity</c>, <c>Leftover</c> or <c>Requested amount</c> (design 7.2).
    /// </param>
    /// <param name="errors">The caller's collector.</param>
    /// <param name="rules">
    /// Which variant of the rules applies; <c>null</c> means <see cref="OrderLineRules.EveryLineZeroOrMore"/>, the send's
    /// and the completion's.
    /// </param>
    /// <returns>The valid lines, in body order, each with its position in the request (for later error paths).</returns>
    public static List<MatchedOrderLine> Match(
        ProductionOrder order,
        IReadOnlyList<SubmittedOrderLine?> submitted,
        string quantityField,
        string quantityLabel,
        ValidationErrors errors,
        OrderLineRules? rules = null)
    {
        rules ??= OrderLineRules.EveryLineZeroOrMore;
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
                errors.Add(itemPath,
                    rules.NotOnOrderMessage?.Invoke(itemId) ?? $"Item {itemId} is not on order #{order.Number}.");
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
            else if (rules.ZeroAllowed && quantity < 0m)
            {
                // AC-15.8 (send, D-7 "S ≥ 0") and AC-19.4 (completion, "a negative leftover"). Zero is allowed in both: a
                // send of 0 where leftover covers the need (AC-15.4), and nothing left at the end (AC-19.1).
                errors.Add(quantityPath, $"{quantityLabel} cannot be negative.");
            }
            else if (!rules.ZeroAllowed && quantity <= 0m)
            {
                // AC-16.3 (complementary request, "an amount of zero or less"): a request for nothing extra means nothing.
                errors.Add(quantityPath, $"{quantityLabel} must be greater than 0.");
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

        if (!rules.EveryLineRequired)
        {
            return matched;
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
/// The variant of <see cref="OrderLineMatcher"/>'s rules a request body follows. The checks themselves (item given, on
/// the order, not repeated; quantity given, decimal places, maximum) are the same for every body; only these three
/// points differ between "one figure for every order line" (send, completion) and "extra material for some of the lines"
/// (complementary request).
/// </summary>
/// <remarks>
/// A <c>record</c> with <c>init</c> properties: a caller writes <c>new OrderLineRules { ZeroAllowed = false }</c>, and
/// every property it does not set keeps the default shown here, which is the send's and the completion's rule.
/// </remarks>
public sealed record OrderLineRules
{
    /// <summary>The send's and the completion's rules (AC-15.8, AC-19.4): every order line once, zero or more.</summary>
    public static OrderLineRules EveryLineZeroOrMore { get; } = new();

    /// <summary>
    /// True: every order line must appear, and the ones missing are reported together (design 7.2, "Missing line (send or
    /// complete)"). False: a body may name only some of the order's materials (US-16).
    /// </summary>
    public bool EveryLineRequired { get; init; } = true;

    /// <summary>
    /// True: zero is allowed and only a negative figure is refused ("{Label} cannot be negative."). False: the figure must
    /// be above zero ("{Label} must be greater than 0.", AC-16.3).
    /// </summary>
    public bool ZeroAllowed { get; init; } = true;

    /// <summary>
    /// The message for an item id that is not one of the order's materials, or <c>null</c> for the send's "Item {id} is
    /// not on order #{n}." (decision #96). The complementary request names the item where it exists (design 7.2,
    /// "{item} is not on order #{number}.").
    /// </summary>
    public Func<int, string>? NotOnOrderMessage { get; init; }
}

/// <summary>
/// One line of a request body as <see cref="OrderLineMatcher"/> reads it: the item and its one quantity (a send's
/// <c>quantity</c>, a completion's <c>leftover</c>, a complementary request's <c>requestedAmount</c>). Both are nullable so a missing value gets the design's message.
/// </summary>
/// <param name="ItemId">The raw material the line is for.</param>
/// <param name="Quantity">The line's figure.</param>
public sealed record SubmittedOrderLine(int? ItemId, decimal? Quantity);

/// <summary>A submitted line that passed <see cref="OrderLineMatcher.Match"/>.</summary>
/// <param name="Index">Its position in the request body, for the JSON path of later errors (<c>lines[i]…</c>).</param>
/// <param name="Line">The order line it names.</param>
/// <param name="Quantity">Its valid quantity (a send), leftover (a completion) or requested amount (a complementary request).</param>
public sealed record MatchedOrderLine(int Index, ProductionOrderLine Line, decimal Quantity);
