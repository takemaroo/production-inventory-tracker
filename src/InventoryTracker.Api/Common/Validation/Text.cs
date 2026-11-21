namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// Small helpers for text inputs such as names, reasons and notes.
/// </summary>
public static class Text
{
    /// <summary>
    /// Design 2.6: leading and trailing spaces are removed before a value is checked, and a value that is empty
    /// after trimming counts as missing. Returns the trimmed text, or null when nothing is left.
    /// </summary>
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Decision #116 (BUG-001): the number of characters in <paramref name="value"/>, counted as Unicode code points.
    /// Every text length limit (names, descriptions, reasons, notes, email) is checked with this method and never with
    /// <see cref="string.Length"/>. No text (null) has no characters, so null gives 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three ways to count "characters".</b> Take the text "a🧀é" (a, cheese emoji U+1F9C0, precomposed é U+00E9).
    /// It is 4 UTF-16 code units (a 1, 🧀 2, é 1), 3 code points and 3 grapheme clusters. Each count is explained below.
    /// </para>
    /// <list type="bullet">
    /// <item><b>UTF-16 code units</b> are what <see cref="string.Length"/> counts. A C# <c>string</c> is a sequence of
    /// 16-bit <c>char</c> values. Most characters fit in one, but a character outside the Basic Multilingual Plane
    /// (emoji such as 🧀 U+1F9C0, some CJK and historic scripts) needs two, called a <em>surrogate pair</em>. So
    /// "🧀".Length is 2, and an emoji used to count double (BUG-001).</item>
    /// <item><b>Unicode code points</b> are the numbered characters of the Unicode standard: 🧀 is one (U+1F9C0), and
    /// so is the precomposed é (U+00E9). In C# one code point is a <see cref="System.Text.Rune"/>, and
    /// <see cref="string.EnumerateRunes"/> walks the string one code point at a time, joining each surrogate pair.
    /// This is what we count.</item>
    /// <item><b>Grapheme clusters</b> are characters as a person sees them. "é" can also be typed as e followed by a
    /// combining accent (U+0065 U+0301): two code points that show as one letter. Counting those as one needs
    /// <c>StringInfo.LengthInTextElements</c>.</item>
    /// </list>
    /// <para>
    /// <b>Why code points.</b> The column limits must agree with the database. PostgreSQL (the planned port, PRD
    /// "Later" list) counts the length of a <c>varchar(n)</c> in characters of the database encoding, which for UTF-8
    /// means code points. Counting the same way guarantees that any text the API accepts also fits the column. Grapheme
    /// clusters would not: one visible letter can be several code points, so a text of 500 visible letters could be
    /// longer than 500 for the database and be refused with a 500 error. SQLite does not enforce column lengths at all.
    /// The cost is small: a letter typed with a separate combining accent uses two of the allowance.
    /// </para>
    /// <para>
    /// A lone surrogate (half a pair, which is not valid Unicode) is counted as one character, because
    /// <see cref="string.EnumerateRunes"/> turns it into the replacement character U+FFFD.
    /// </para>
    /// </remarks>
    public static int Length(string? value) => value is null ? 0 : value.EnumerateRunes().Count();
}
