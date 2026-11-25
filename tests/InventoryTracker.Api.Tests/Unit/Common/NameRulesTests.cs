using System.Text;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// BUG-002, decisions #170 and #171: <see cref="NameRules.Validate"/>, the one name rule for customers, units and items
/// (AC-06.2, AC-07.1, AC-29.2). It folds a name to Unicode NFC before trimming, treats a name with nothing visible as
/// missing, checks the length on what is stored, and gives the comparison key.
/// </summary>
/// <remarks>
/// Every special character is written as an escape (<c>\u0301</c>, <c>\u200B</c>), so an editor or a copy-paste cannot
/// silently turn one Unicode form into the other and make a test pass for the wrong reason.
/// </remarks>
public class NameRulesTests
{
    private const string Precomposed = "Caf\u00E9 Sur"; // "\u00E9" as one code point, as a Windows keyboard types it
    private const string Decomposed = "Cafe\u0301 Sur"; // "e" + COMBINING ACUTE ACCENT, as pasted from macOS or a PDF

    /// <summary>
    /// Decision #170: the decomposed spelling is stored in NFC, so it is the very same text as the precomposed one, and
    /// both give the same comparison key. Without the NFC step the two keys differ (the BUG-002 duplicate).
    /// </summary>
    [Fact]
    public void Decomposed_name_is_folded_to_NFC_and_gets_the_same_key_as_the_precomposed_one()
    {
        Assert.NotEqual(Precomposed, Decomposed); // the inputs really are different code points

        var fromDecomposed = Valid(Decomposed, maxLength: 100);
        var fromPrecomposed = Valid(Precomposed, maxLength: 100);

        Assert.Equal(Precomposed, fromDecomposed.Name);
        Assert.True(fromDecomposed.Name.IsNormalized(NormalizationForm.FormC));
        Assert.Equal(fromPrecomposed.Normalized, fromDecomposed.Normalized);
        Assert.Equal("CAF\u00C9 SUR", fromDecomposed.Normalized);
    }

    /// <summary>
    /// Decision #170, design 2.6: NFC comes before trimming, and the key is the trimmed, upper-cased NFC text, the same
    /// format as before the fix (decision #172), so a name already stored in NFC still matches.
    /// </summary>
    [Fact]
    public void Name_is_trimmed_after_NFC_and_the_key_keeps_its_old_format()
    {
        var name = Valid("  " + Decomposed + "\t ", maxLength: 100);

        Assert.Equal(Precomposed, name.Name);
        Assert.Equal(Precomposed.Trim().ToUpperInvariant(), name.Normalized);
        Assert.Equal(NameNormalizer.Normalize(Precomposed), name.Normalized);
    }

    /// <summary>
    /// Decision #172: <see cref="NameNormalizer.Normalize"/> gives a name already in NFC exactly the key it had before
    /// BUG-002 (trimmed, upper-cased), so stored rows compare correctly without a migration; a decomposed name gets the
    /// key of its NFC form.
    /// </summary>
    [Theory]
    [InlineData("Deli Norte", "DELI NORTE")]
    [InlineData(" kg ", "KG")]
    [InlineData("Caf\u00E9 Sur", "CAF\u00C9 SUR")]
    [InlineData("Cafe\u0301 Sur", "CAF\u00C9 SUR")]
    public void Key_of_a_name_is_its_NFC_form_trimmed_and_upper_cased(string name, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(name));
    }

    /// <summary>
    /// Decision #171: a name with no visible character (only white space, control and format characters) is "Name is
    /// required.", as an empty name is. Covers the zero width space from BUG-002's notes, white space mixed with it,
    /// the other common format characters, and the tab and NUL v1 QA noted.
    /// </summary>
    [Theory]
    [InlineData("\u200B")]          // zero width space (BUG-002 notes)
    [InlineData("  \u200B  ")]      // white space around a zero width space
    [InlineData("\u200B \u200B\t")] // zero width spaces and white space mixed
    [InlineData("\u200C\u200D")]    // zero width non-joiner and joiner
    [InlineData("\u2060")]          // word joiner
    [InlineData("\uFEFF")]          // byte order mark (zero width no-break space)
    [InlineData("\u00AD")]          // soft hyphen
    [InlineData("\u200E\u200F")]    // left-to-right and right-to-left marks
    [InlineData("\u00A0\u3000")]    // no-break space and ideographic space (white space .Trim() also removes)
    [InlineData("\t\u0000")]        // tab and NUL: control characters
    [InlineData("\u0007\u007F")]    // bell and delete: control characters
    public void Name_with_no_visible_character_is_required(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.RequiredMessage);
    }

    /// <summary>Design 2.6, AC-06.2, AC-29.2: missing, empty and spaces-only names are still "Name is required.".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_name_is_required(string? value)
    {
        AssertRefused(value, maxLength: 100, NameRules.RequiredMessage);
    }

    /// <summary>
    /// Decision #171: invisible characters among visible ones are kept exactly as sent, never stripped (scripts such as
    /// Persian need a zero width non-joiner between letters, and emoji sequences use the zero width joiner).
    /// </summary>
    [Theory]
    [InlineData("Caf\u00E9\u200BSur")]                 // zero width space between words
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")] // Persian word with a zero width non-joiner
    [InlineData("\U0001F469\u200D\U0001F373 Kitchen")] // woman cook emoji: woman + ZWJ + cooking
    [InlineData("\u200BKitchen")]                     // leading format character: not white space, so not trimmed
    public void Invisible_characters_among_visible_ones_are_kept(string value)
    {
        var name = Valid(value, maxLength: 100);

        Assert.Equal(value, name.Name);
    }

    /// <summary>
    /// Decision #171: one visible character is enough, even a single letter or a symbol among invisible ones.
    /// </summary>
    [Theory]
    [InlineData("\u200Bx\u200B", "\u200Bx\u200B")]
    [InlineData(" \u200B\u00E9 ", "\u200B\u00E9")]
    [InlineData("\U0001F9C0", "\U0001F9C0")]
    public void One_visible_character_makes_a_name(string value, string stored)
    {
        Assert.Equal(stored, Valid(value, maxLength: 50).Name);
    }

    /// <summary>
    /// Design 2.6, decision #116 and this fix: the length limit is checked on what is stored, after NFC and trimming. 50
    /// letters with every "é" typed decomposed are 50 code points after NFC (the column limit for units), although the
    /// text as sent has more; 51 after NFC is refused with the length message.
    /// </summary>
    [Fact]
    public void Length_is_counted_after_NFC_and_trimming()
    {
        var fifty = string.Concat(Enumerable.Repeat("e\u0301", 50)); // 100 code points as sent, 50 in NFC
        Assert.Equal(100, Text.Length(fifty));

        var accepted = Valid($"  {fifty}  ", maxLength: 50);
        Assert.Equal(50, Text.Length(accepted.Name));
        Assert.Equal(new string('\u00E9', 50), accepted.Name);

        AssertRefused(fifty + "e\u0301", maxLength: 50, "Name must be 50 characters or fewer.");
    }

    /// <summary>
    /// Design 2.6: a character with no precomposed form stays decomposed after NFC and still counts as two code points,
    /// so the limit can be reached with it (the documented cost of counting code points, decision #116).
    /// </summary>
    [Fact]
    public void Combining_mark_with_no_precomposed_form_still_counts_as_two()
    {
        // "x" + combining acute: Unicode has no precomposed x-acute, so NFC leaves the two code points alone.
        var twentyFive = string.Concat(Enumerable.Repeat("x\u0301", 25));

        Assert.Equal(twentyFive, Valid(twentyFive, maxLength: 50).Name);
        AssertRefused(twentyFive + "x", maxLength: 50, "Name must be 50 characters or fewer.");
    }

    /// <summary>
    /// Decision #174 (BUG-002 review): U+FFFE is valid UTF-16 and valid JSON, but <c>string.Normalize</c> throws on it.
    /// A name holding it is refused with "Name contains a character that is not allowed." instead of an exception
    /// (which became a <c>500</c>). The check comes first, so an otherwise empty or over-long name gets this message too.
    /// </summary>
    [Theory]
    [InlineData("\uFFFE", 100)]           // the noncharacter alone
    [InlineData("Caf\u00E9\uFFFE Sur", 100)] // among visible letters
    [InlineData("  \uFFFE  ", 100)]       // with white space around it
    [InlineData("xx\uFFFE", 2)]           // over the limit as well: the character message wins
    public void Name_that_cannot_be_normalized_is_refused_with_400_not_an_exception(string value, int maxLength)
    {
        Assert.Throws<ArgumentException>(() => value.Normalize(NormalizationForm.FormC)); // the .NET behaviour guarded against

        AssertRefused(value, maxLength, NameRules.NotAllowedCharacterMessage);
        Assert.Equal("Name contains a character that is not allowed.", NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #174: the neighbours of U+FFFE are not refused. U+FFFD (the replacement character) and U+FFFF (another
    /// noncharacter, which .NET normalizes without complaint) are ordinary visible-or-not code points for the rule.
    /// </summary>
    [Fact]
    public void Neighbours_of_U_FFFE_are_not_refused_as_not_allowed()
    {
        Assert.Equal("a\uFFFD", Valid("a\uFFFD", maxLength: 50).Name);
        Assert.Equal("a\uFFFF", Valid("a\uFFFF", maxLength: 50).Name);
    }

    /// <summary>A name the rule accepts, checking that it added no error.</summary>
    private static CleanName Valid(string value, int maxLength)
    {
        var errors = new ValidationErrors();
        var name = NameRules.Validate(value, maxLength, errors);

        Assert.False(errors.HasErrors);
        return Assert.IsType<CleanName>(name);
    }

    /// <summary>Checks that the rule refuses <paramref name="value"/> with exactly <paramref name="message"/> under <c>name</c>.</summary>
    private static void AssertRefused(string? value, int maxLength, string message)
    {
        var errors = new ValidationErrors();

        Assert.Null(NameRules.Validate(value, maxLength, errors));

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny());
        Assert.Equal(message, exception.Detail);
        var field = Assert.Single(exception.Errors!);
        Assert.Equal("name", field.Key);
        Assert.Equal([message], field.Value);
    }
}
