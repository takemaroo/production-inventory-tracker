using System.Text;

namespace InventoryTracker.Api.Domain;

/// <summary>
/// Produces the value stored in the <c>NameNormalized</c> columns of units, items and customers (AC-06.2, AC-07.1,
/// AC-29.2).
/// </summary>
/// <remarks>
/// Names must be unique without regard to letter case. Rather than rely on a database collation (SQLite's
/// <c>NOCASE</c> is SQLite-only and ASCII-only), each table stores a second, normalized copy of the name with a
/// unique index on it (architecture 3.1, decision #30). Every place that sets a <c>Name</c> must set
/// <c>NameNormalized</c> from this method, so the comparison rule exists in one place. Request names reach it through
/// <c>Common.Validation.NameRules</c>, which also trims and checks them.
/// </remarks>
public static class NameNormalizer
{
    /// <summary>
    /// Returns <paramref name="name"/> in Unicode NFC, trimmed and upper-cased with the invariant culture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decision #170 (BUG-002): the text is put in NFC first, so "é" typed as one code point and "é" typed as "e" plus a
    /// combining accent give the same key (see <c>NameRules</c> for what NFC is). For text already in NFC, which is
    /// what <c>NameRules</c> passes in, this step changes nothing; the key is then exactly the one stored before
    /// BUG-002, so existing rows still compare correctly without a migration (decision #172).
    /// </para>
    /// <para>
    /// The invariant culture gives the same result on every machine; with the current culture, a Turkish
    /// locale would upper-case "i" to "İ" and the same name could normalize differently.
    /// </para>
    /// </remarks>
    public static string Normalize(string name) =>
        name.Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();
}
