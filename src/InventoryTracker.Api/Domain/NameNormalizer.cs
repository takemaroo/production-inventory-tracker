namespace InventoryTracker.Api.Domain;

/// <summary>
/// Produces the value stored in the <c>NameNormalized</c> columns of units and items (AC-06.2, AC-07.1).
/// </summary>
/// <remarks>
/// Names must be unique without regard to letter case. Rather than rely on a database collation (SQLite's
/// <c>NOCASE</c> is SQLite-only and ASCII-only), each table stores a second, normalized copy of the name with a
/// unique index on it (architecture 3.1, decision #30). Every place that sets a <c>Name</c> must set
/// <c>NameNormalized</c> from this method, so the comparison rule exists in one place.
/// </remarks>
public static class NameNormalizer
{
    /// <summary>Returns <paramref name="name"/> trimmed and upper-cased with the invariant culture.</summary>
    /// <remarks>
    /// The invariant culture gives the same result on every machine; with the current culture, a Turkish
    /// locale would upper-case "i" to "İ" and the same name could normalize differently.
    /// </remarks>
    public static string Normalize(string name) => name.Trim().ToUpperInvariant();
}
