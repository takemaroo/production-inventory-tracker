using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// Decision #116 (BUG-001): <see cref="Text.Length"/> counts Unicode code points, not UTF-16 code units, so every text
/// limit in design 2.6 (AC-06.1, AC-12.1 and the other "characters" limits) counts an emoji as one character.
/// </summary>
/// <remarks>
/// Each case also checks <see cref="string.Length"/> where the two differ, so the test shows the difference the helper
/// exists for and would fail if the helper went back to counting code units.
/// </remarks>
public class TextLengthTests
{
    /// <summary>Decision #116: plain ASCII text is one code point per letter, the same as <see cref="string.Length"/>.</summary>
    [Theory]
    [InlineData("kg", 2)]
    [InlineData("Mozzarella cheese", 17)]
    [InlineData(" ", 1)]
    public void Ascii_text_counts_one_per_letter(string value, int expected)
    {
        Assert.Equal(expected, Text.Length(value));
    }

    /// <summary>Decision #116: no text has no characters. Null gives 0, so optional fields need no separate null check.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_missing_text_has_length_zero(string? value)
    {
        Assert.Equal(0, Text.Length(value));
    }

    /// <summary>Decision #116: a precomposed accented letter (é, U+00E9) is one code point, and one UTF-16 code unit.</summary>
    [Fact]
    public void Precomposed_accented_letter_is_one_character()
    {
        // Written with escapes, so an editor cannot silently turn one form of the accented e into the other.
        const string precomposed = "caf\u00E9";

        Assert.Equal(4, Text.Length(precomposed));
        Assert.Equal(4, precomposed.Length);
    }

    /// <summary>
    /// Decision #116: e followed by a combining acute accent (U+0065 U+0301) shows as one "é" but is two code points, and
    /// counts as two. This is the documented cost of counting code points rather than grapheme clusters.
    /// </summary>
    [Fact]
    public void Letter_with_combining_accent_is_two_characters()
    {
        const string decomposed = "cafe\u0301";

        Assert.Equal(5, Text.Length(decomposed));
    }

    /// <summary>
    /// BUG-001, AC-06.1: an emoji (cheese, U+1F9C0) is one character, although C# stores it as two UTF-16 code units
    /// (a surrogate pair) and <see cref="string.Length"/> says 2.
    /// </summary>
    [Fact]
    public void Emoji_is_one_character()
    {
        const string cheese = "\U0001F9C0";

        Assert.Equal(1, Text.Length(cheese));
        Assert.Equal(2, cheese.Length);
    }

    /// <summary>
    /// BUG-001: every surrogate pair is one character, whether emoji or a letter outside the Basic Multilingual Plane
    /// (Deseret 𐐀 U+10400, the CJK ideograph 𠀀 U+20000), and mixed with ordinary letters.
    /// </summary>
    [Theory]
    [InlineData("\U0001F9C0\U0001F9C0\U0001F9C0", 3, 6)]
    [InlineData("\U00010400", 1, 2)]
    [InlineData("\U00020000x", 2, 3)]
    [InlineData("a\U0001F9C0\u00E9", 3, 4)]
    public void Surrogate_pairs_count_as_one_character_each(string value, int codePoints, int codeUnits)
    {
        Assert.Equal(codePoints, Text.Length(value));
        Assert.Equal(codeUnits, value.Length);
    }

    /// <summary>
    /// Decision #116: half a surrogate pair is not valid Unicode; it counts as one character (the replacement
    /// character), so a malformed string can never slip past a limit by counting as zero.
    /// </summary>
    [Fact]
    public void Lone_surrogate_counts_as_one_character()
    {
        Assert.Equal(2, Text.Length("a\uD83E"));
    }

    /// <summary>
    /// AC-06.1 at the limit: 50 emoji is exactly 50 characters (100 code units), so it passes a 50-character limit;
    /// 51 does not.
    /// </summary>
    [Fact]
    public void Fifty_emoji_are_fifty_characters()
    {
        var fifty = string.Concat(Enumerable.Repeat("\U0001F9C0", 50));

        Assert.Equal(50, Text.Length(fifty));
        Assert.Equal(51, Text.Length(fifty + "\U0001F9C0"));
    }

    /// <summary>
    /// Decision #116, architecture 3.1: the <c>NameNormalized</c> column has the same limit as the name, so upper-casing
    /// must not change the count. Deseret lower-case letters (U+10428) have upper-case forms that are also surrogate
    /// pairs; the letters really change, and the count stays the same.
    /// </summary>
    [Fact]
    public void Normalizing_a_name_keeps_its_character_count()
    {
        const string name = "\U00010428\U00010429 caf\u00E9 \U0001F9C0";

        var normalized = NameNormalizer.Normalize(name);

        Assert.NotEqual(name, normalized);
        Assert.Equal(Text.Length(name), Text.Length(normalized));
    }
}
