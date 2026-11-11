namespace InventoryTracker.Api.Domain.Calculations;

/// <summary>
/// The quantities a warehouse user may send for one line: from <see cref="Min"/> to <see cref="Max"/>,
/// both limits included, with <see cref="Nominal"/> as the amount to aim for.
/// </summary>
/// <remarks>
/// <para>
/// Used for an order send (D-7) and for a complementary send (AC-18.1). The limits are already rounded to
/// 3 decimal places, and a quantity is accepted when <c>Min ≤ quantity ≤ Max</c> using exactly these limits,
/// so the API accepts precisely the range it shows (architecture 4.1 item 4, decision #24).
/// </para>
/// <para>
/// A <c>record</c> is a class whose equality compares the values inside it, which makes it a natural fit for
/// a small result like this and lets tests write <c>Assert.Equal(new AcceptedRange(975, 1015, 995), range)</c>.
/// </para>
/// </remarks>
/// <param name="Min">Lowest accepted quantity, never below 0.</param>
/// <param name="Max">Highest accepted quantity.</param>
/// <param name="Nominal">
/// The amount to aim for: planned − leftover applied for an order send (D-7), the requested amount for a
/// complementary send.
/// </param>
public sealed record AcceptedRange(decimal Min, decimal Max, decimal Nominal);
