using System.Globalization;
using System.Text;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// BUG-002, decisions #170 and #171: <see cref="NameRules.Validate"/>, the one name rule for customers, units and items
/// (AC-06.2, AC-07.1, AC-29.2). It folds a name to Unicode NFC before trimming, treats a name with nothing visible as
/// missing, checks the length on what is stored, and gives the comparison key. T-034 (SEC-25, decisions #176 and #181)
/// adds the characters that are not allowed; its tests are the section marked T-034.
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
    /// Persian need a zero width non-joiner between letters, and emoji sequences use the zero width joiner). Since T-034
    /// (decision #176) this holds between visible characters only; one at the start or end is refused (the T-034
    /// tests below).
    /// </summary>
    [Theory]
    [InlineData("Caf\u00E9\u200BSur")]                 // zero width space between words
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")] // Persian word with a zero width non-joiner
    [InlineData("\U0001F469\u200D\U0001F373 Kitchen")] // woman cook emoji: woman + ZWJ + cooking
    public void Invisible_characters_among_visible_ones_are_kept(string value)
    {
        var name = Valid(value, maxLength: 100);

        Assert.Equal(value, name.Name);
    }

    /// <summary>
    /// Decision #171: one visible character is enough, even a single letter or a symbol. (Before T-034 the cases also
    /// wrapped the letter in zero width spaces; decision #176 now refuses those at the start and end, see the T-034
    /// tests below.)
    /// </summary>
    [Theory]
    [InlineData("x", "x")]
    [InlineData(" \u00E9 ", "\u00E9")]
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
    /// Decision #174: the neighbours of U+FFFE are not caught by the NFC check. U+FFFD (the replacement character) is an
    /// ordinary character and is accepted. U+FFFF (another noncharacter) normalizes without complaint, so #174 lets it
    /// through; since T-034 (decision #176) the noncharacter rule refuses it instead. Before T-034 this test expected
    /// U+FFFF to be accepted.
    /// </summary>
    [Fact]
    public void Neighbours_of_U_FFFE_are_not_refused_by_the_NFC_check()
    {
        Assert.Equal("a\uFFFD", Valid("a\uFFFD", maxLength: 50).Name);

        Assert.Equal("a\uFFFF", "a\uFFFF".Normalize(NormalizationForm.FormC)); // no exception: not a #174 case
        AssertRefused("a\uFFFF", maxLength: 50, NameRules.NotAllowedCharacterMessage); // decision #176
    }

    // ---- T-034 (SEC-25 fix list, decisions #176 and #181): characters that are not allowed ---------------------------

    /// <summary>
    /// The invisible characters the SEC-25 fix list names, as code points (so no raw invisible character is in this
    /// file). Each one is a Default_Ignorable_Code_Point; the four Hangul fillers are in that list too.
    /// </summary>
    public static TheoryData<int> EdgeInvisibles =>
    [
        0x034F,                                 // COMBINING GRAPHEME JOINER (a SEC-25 "Deli Norte" twin)
        0x115F, 0x1160, 0x3164, 0xFFA0,         // the four Hangul fillers
        0x200B, 0x200C, 0x200D, 0x200E, 0x200F, // zero width space, ZWNJ, ZWJ, left-to-right and right-to-left marks
        0x2060, 0x2064, 0x2065, 0x2066, 0x206F, // word joiner up to nominal digit shapes (U+2060 to U+206F)
        0xFEFF,                                 // byte order mark (zero width no-break space)
        0xFE00, 0xFE0F,                         // variation selectors 1 and 16
        0x180B, 0x180C, 0x180D, 0x180E, 0x180F, // Mongolian free variation selectors and vowel separator
        0xE0100, 0xE01EF,                       // variation selectors 17 and 256, outside the Basic Multilingual Plane
        0x00AD, 0x202E,                         // soft hyphen, right-to-left override
    ];

    /// <summary>
    /// SEC-25 fix, decision #176: a name that ends with an invisible character is refused with the #174 message, so
    /// "Deli Norte" followed by one can no longer sit beside "Deli Norte" as a twin that looks the same.
    /// </summary>
    [Theory]
    [MemberData(nameof(EdgeInvisibles))]
    public void T034_invisible_character_at_the_end_is_not_allowed(int codePoint)
    {
        AssertRefused("Deli Norte" + char.ConvertFromUtf32(codePoint), maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>SEC-25 fix, decision #176: the same characters at the start of a name are refused too.</summary>
    [Theory]
    [MemberData(nameof(EdgeInvisibles))]
    public void T034_invisible_character_at_the_start_is_not_allowed(int codePoint)
    {
        AssertRefused(char.ConvertFromUtf32(codePoint) + "Deli Norte", maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #171 kept by #176, and #181: the same characters between visible ones are allowed and kept exactly as
    /// sent. SEC-25 asks only for the edges; #181 records why U+200B and U+202E stay allowed in the middle.
    /// </summary>
    [Theory]
    [MemberData(nameof(EdgeInvisibles))]
    public void T034_invisible_character_in_the_middle_is_kept(int codePoint)
    {
        var value = "Deli" + char.ConvertFromUtf32(codePoint) + "Norte";

        Assert.Equal(value, Valid(value, maxLength: 100).Name);
    }

    /// <summary>
    /// Decision #176: the edge rule is checked after NFC and trimming, on what would be stored. White space around the
    /// invisible character is removed first, so the character is then at the edge and is refused.
    /// </summary>
    [Theory]
    [InlineData("  \u200BDeli Norte  ")]       // spaces trimmed, then the zero width space is first
    [InlineData("Deli Norte\u200B\t\u3000")] // tab and ideographic space trimmed, then the zero width space is last
    [InlineData("\uFEFF Deli Norte")]          // U+FEFF is not white space, so trimming leaves it first
    [InlineData("Deli Norte\uFE0F ")]          // a variation selector before a trailing space
    public void T034_edge_rule_is_checked_after_trimming(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #171, kept by #176: ZWNJ and ZWJ between letters are allowed, as Persian words and emoji sequences need.
    /// </summary>
    [Theory]
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")]  // Persian word with a ZWNJ inside it
    [InlineData("\U0001F469\u200D\U0001F373")]                              // woman cook emoji alone: ZWJ between two pictures
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467 Bakery")]   // family emoji: two ZWJ inside
    public void T034_joiners_between_letters_are_kept(string value)
    {
        Assert.Equal(value, Valid(value, maxLength: 100).Name);
    }

    /// <summary>
    /// Decision #176: ZWNJ and ZWJ are default ignorable, so at the start or end of a name, where they join nothing,
    /// they are refused.
    /// </summary>
    [Theory]
    [InlineData("\u200CKitchen")]
    [InlineData("Kitchen\u200D")]
    [InlineData("\u0645\u06CC\u200C")] // Persian letters with a ZWNJ left over at the end
    public void T034_joiners_at_the_start_or_end_are_not_allowed(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #181 (check order): a name made only of invisible characters is "Name is required." (#171), not the
    /// not-allowed message, although its first character is also a default ignorable at the start. A lone U+200B is
    /// the BUG-002 case.
    /// </summary>
    [Theory]
    [InlineData("\u200B")]
    [InlineData(" \u200B ")]
    [InlineData("\u200D\u200C")]
    [InlineData("\u0000")]              // NUL alone: a control character, refused anywhere, but nothing is visible
    [InlineData("\t\u0000\u200B")]
    [InlineData("\u3164\u200B\uFEFF")] // a Hangul filler among format characters
    public void T034_name_of_only_invisible_characters_is_still_required(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.RequiredMessage);
    }

    /// <summary>
    /// SEC-25 fix, decision #176: the Hangul fillers and U+2800 BRAILLE PATTERN BLANK count as invisible for the
    /// required check, so a name made only of them (SEC-25's blank customers) is "Name is required.".
    /// </summary>
    [Theory]
    [InlineData("\u3164")]
    [InlineData("\u3164\u3164\u3164")]
    [InlineData("\u115F\u1160")]
    [InlineData("\uFFA0")]
    [InlineData("\u2800")]
    [InlineData(" \u2800 \u2800 ")]
    [InlineData("\u2800\u3164\u200B")]
    public void T034_Hangul_fillers_or_braille_blank_alone_are_required(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.RequiredMessage);
    }

    /// <summary>
    /// Decision #182: U+2800 between visible characters is kept exactly as sent, as the default ignorables are there
    /// (#181).
    /// </summary>
    [Theory]
    [InlineData("Deli\u2800Norte")]
    [InlineData("Deli \u2800 Norte")]
    public void T034_braille_blank_in_the_middle_is_kept(string value)
    {
        Assert.Equal(value, Valid(value, maxLength: 100).Name);
    }

    /// <summary>
    /// Decision #182, closing SEC-25's twin "Deli Norte" followed by U+2800: U+2800 at the start or end of a name that has
    /// visible characters is refused with the not-allowed message, checked after trimming. (Before #182 this test was
    /// <c>T034_braille_blank_beside_visible_characters_is_kept</c> and expected these names to be accepted.)
    /// </summary>
    [Theory]
    [InlineData("Deli Norte\u2800")]
    [InlineData("\u2800Deli Norte")]
    [InlineData(" \u2800Deli Norte ")]
    [InlineData("Deli Norte\u2800\u2800")]
    public void T034_braille_blank_at_the_start_or_end_is_not_allowed(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #182: a name whose first code point, after NFC and trimming, is a combining mark (category Mn or Me) is
    /// refused with the not-allowed message: a lone combining accent (listed in SEC-25 as accepted before), or a name that
    /// begins with one. A combining mark counts as visible under #173, so these get past "Name is required." first.
    /// </summary>
    [Theory]
    [InlineData("\u0301")]            // lone COMBINING ACUTE ACCENT (Mn)
    [InlineData("\u0301\u0301")]     // two of them
    [InlineData("\u0301Deli Norte")]  // a name beginning with one
    [InlineData("  \u0301Deli Norte")] // the same after trimming
    [InlineData("\u20DD")]            // lone COMBINING ENCLOSING CIRCLE (Me)
    [InlineData("\u20DDDeli")]
    [InlineData("\u0488Deli")]        // COMBINING CYRILLIC HUNDRED THOUSANDS SIGN (Me)
    [InlineData("\u093CDeli")]        // DEVANAGARI SIGN NUKTA (Mn) with no consonant before it
    [InlineData("\u0301\u200B")]     // a mark, then a zero width space
    public void T034_combining_mark_first_is_not_allowed(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #182: a combining mark after a base letter is unaffected, anywhere in the name, at the end included. NFC
    /// runs first, so a decomposed accented first letter has already become one code point and is not refused.
    /// </summary>
    [Theory]
    [InlineData("x\u0301 Sur", "x\u0301 Sur")]                     // no precomposed x-acute: the mark stays after its letter
    [InlineData("Cafe\u0301", "Caf\u00E9")]                         // composed by NFC
    [InlineData("E\u0301clair", "\u00C9clair")]                     // decomposed first letter: NFC makes it U+00C9
    [InlineData("Deli Norte x\u20DD", "Deli Norte x\u20DD")]       // enclosing mark at the end, after its letter
    [InlineData("\u0915\u0941\u0932", "\u0915\u0941\u0932")]    // Devanagari: a vowel sign (Mn) after its consonant
    public void T034_combining_mark_after_a_letter_is_kept(string value, string stored)
    {
        Assert.Equal(stored, Valid(value, maxLength: 100).Name);
    }

    /// <summary>
    /// Decision #181 (check order), decision #173: a lone variation selector or combining grapheme joiner (category Mn)
    /// does not count as invisible under #173, so it passes the required check and is then refused as a default
    /// ignorable at the start (SEC-25 listed "a lone variation selector" as accepted before).
    /// </summary>
    [Theory]
    [InlineData("\uFE0F")]
    [InlineData("\u034F")]
    [InlineData("\u180B")]
    [InlineData("\U000E0100")]
    public void T034_lone_variation_selector_or_grapheme_joiner_is_not_allowed(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// SEC-25 fix, decision #176: a control character (category Cc) anywhere in a name is refused, NUL, tab and line
    /// breaks included, with the #174 message.
    /// </summary>
    [Theory]
    [InlineData("Deli\u0000Norte")]  // NUL (the planned PostgreSQL port refuses it in text, SEC-09)
    [InlineData("Deli\tNorte")]      // tab
    [InlineData("Deli\nNorte")]      // line feed
    [InlineData("Deli\r\nNorte")]   // carriage return and line feed
    [InlineData("Deli\u0085Norte")]  // NEXT LINE, a C1 control that is also white space
    [InlineData("Deli\u001FNorte")]  // last C0 control
    [InlineData("Deli\u007FNorte")]  // DELETE
    [InlineData("Deli\u0080Norte")]  // first C1 control
    [InlineData("Deli\u009FNorte")]  // last C1 control
    [InlineData("\u0000Deli Norte")] // NUL first: not white space, so not trimmed
    [InlineData("Deli Norte\u0000")] // NUL last
    public void T034_control_character_anywhere_is_not_allowed(string value)
    {
        AssertRefused(value, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Design 2.6, unchanged by decision #176: a tab or line break at either end is white space and is trimmed, not
    /// refused; only one inside the name is a control character in the name.
    /// </summary>
    [Fact]
    public void T034_tab_or_line_break_at_the_ends_is_trimmed_not_refused()
    {
        Assert.Equal("Deli Norte", Valid("\tDeli Norte\r\n", maxLength: 100).Name);
    }

    /// <summary>
    /// SEC-25 fix, decision #176: a noncharacter (U+FDD0 to U+FDEF, or a code point ending in FFFE or FFFF) is refused
    /// anywhere in a name, and alone. A noncharacter is not invisible under #173 (it is category Cn), so a lone one gets
    /// the not-allowed message.
    /// </summary>
    [Theory]
    [InlineData(0xFDD0)]   // first of the U+FDD0 block
    [InlineData(0xFDEF)]   // last of the U+FDD0 block
    [InlineData(0xFFFF)]
    [InlineData(0x1FFFE)]
    [InlineData(0x1FFFF)]
    [InlineData(0xEFFFF)]
    [InlineData(0x10FFFE)]
    [InlineData(0x10FFFF)] // the last code point of all
    public void T034_noncharacter_anywhere_is_not_allowed(int codePoint)
    {
        var c = char.ConvertFromUtf32(codePoint);

        AssertRefused("Deli" + c + "Norte", maxLength: 100, NameRules.NotAllowedCharacterMessage);
        AssertRefused(c + "Deli Norte", maxLength: 100, NameRules.NotAllowedCharacterMessage);
        AssertRefused("Deli Norte" + c, maxLength: 100, NameRules.NotAllowedCharacterMessage);
        AssertRefused(c, maxLength: 100, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #176 boundary: the code points next to the noncharacters are allowed: U+FFFD (replacement character),
    /// U+FFFC (object replacement character), U+FDCF and U+FDF0 (Arabic ligatures on each side of U+FDD0 to U+FDEF), and
    /// U+1FFFD and U+10FFFD (just below a plane's last two code points).
    /// </summary>
    [Theory]
    [InlineData(0xFFFD)]
    [InlineData(0xFFFC)]
    [InlineData(0xFDCF)]
    [InlineData(0xFDF0)]
    [InlineData(0x1FFFD)]
    [InlineData(0x10FFFD)]
    public void T034_neighbours_of_noncharacters_are_allowed(int codePoint)
    {
        var value = "Deli" + char.ConvertFromUtf32(codePoint) + "Norte";

        Assert.Equal(value, Valid(value, maxLength: 100).Name);
    }

    /// <summary>
    /// Decision #181 (check order): the character rule comes before the length check, as #174 does, so an over-long
    /// name with a character that is not allowed gets the not-allowed message.
    /// </summary>
    [Theory]
    [InlineData("xxxx\u200B")]
    [InlineData("xx\u0000xx")]
    [InlineData("xx\uFDD0xx")]
    public void T034_not_allowed_character_is_reported_before_the_length(string value)
    {
        AssertRefused(value, maxLength: 2, NameRules.NotAllowedCharacterMessage);
    }

    /// <summary>
    /// Decision #181 with #172: a rename that sends the record's stored name back unchanged passes, even when the stored
    /// name breaks a T-034 rule. It is returned exactly as stored, with the key it was stored with, so the record can
    /// still be edited (an item's description, a customer's notes). White space around it is trimmed first, as always.
    /// </summary>
    [Theory]
    [InlineData("Deli Norte\u200B", "Deli Norte\u200B")]   // invisible character at the end
    [InlineData(" Deli Norte\u200B ", "Deli Norte\u200B")] // the same, once trimmed
    [InlineData("\u034FDeli Norte", "\u034FDeli Norte")]   // combining grapheme joiner at the start
    [InlineData("Deli\tNorte", "Deli\tNorte")]             // tab inside
    [InlineData("Deli\uFFFFNorte", "Deli\uFFFFNorte")]     // noncharacter inside
    [InlineData("\u3164", "\u3164")]                       // Hangul filler only: required under #176, not under #173
    [InlineData("\u2800", "\u2800")]                       // braille blank only, likewise
    [InlineData("Deli Norte\u2800", "Deli Norte\u2800")]   // braille blank at the end (#182)
    [InlineData("\u0301Deli Norte", "\u0301Deli Norte")]   // combining accent first (#182)
    [InlineData("\u0301", "\u0301")]                       // lone combining accent (#182)
    public void T034_unchanged_stored_name_is_not_held_to_the_new_rules(string sent, string stored)
    {
        var errors = new ValidationErrors();

        var name = NameRules.Validate(sent, maxLength: 100, errors, storedName: stored);

        Assert.False(errors.HasErrors);
        Assert.Equal(stored, name!.Name);
        Assert.Equal(NameNormalizer.Normalize(stored), name.Normalized);
    }

    /// <summary>
    /// Decision #181: any real change of the name follows every rule, a change of letter case included (the same
    /// ordinal test the customer log uses for "name changed", #179). Only the exact stored text is let through.
    /// </summary>
    [Theory]
    [InlineData("DELI NORTE\u200B", "Deli Norte\u200B", "Name contains a character that is not allowed.")] // case only
    [InlineData("Deli Norte 2\u200B", "Deli Norte\u200B", "Name contains a character that is not allowed.")]
    [InlineData("Deli Norte\u200B", "Deli Norte", "Name contains a character that is not allowed.")]       // newly added
    [InlineData("Deli\t\tNorte", "Deli\tNorte", "Name contains a character that is not allowed.")]
    [InlineData("Deli Norte 2\u2800", "Deli Norte\u2800", "Name contains a character that is not allowed.")] // #182
    [InlineData("\u0301DELI NORTE", "\u0301Deli Norte", "Name contains a character that is not allowed.")] // #182
    [InlineData("\u3164\u3164", "\u3164", "Name is required.")]
    public void T034_changed_name_follows_the_new_rules(string sent, string stored, string message)
    {
        var errors = new ValidationErrors();

        Assert.Null(NameRules.Validate(sent, maxLength: 100, errors, storedName: stored));

        Assert.Equal([message], errors.ToDictionary()["name"]);
    }

    /// <summary>
    /// Decision #181: the exemption covers only the T-034 rules. A stored name from before BUG-002 that breaks a BUG-002
    /// rule (a lone zero width space, U+FFFE) is refused as before, and the length limit still applies.
    /// </summary>
    [Theory]
    [InlineData("\u200B", 100, "Name is required.")]
    [InlineData("Caf\u00E9\uFFFE", 100, "Name contains a character that is not allowed.")]
    [InlineData("Deli Norte\u200B", 5, "Name must be 5 characters or fewer.")]
    public void T034_unchanged_stored_name_still_follows_the_older_rules(string stored, int maxLength, string message)
    {
        var errors = new ValidationErrors();

        Assert.Null(NameRules.Validate(stored, maxLength, errors, storedName: stored));

        Assert.Equal([message], errors.ToDictionary()["name"]);
    }

    /// <summary>
    /// Decisions #176 and #182: every code point is tried at the end of "x", at the start of "x" and between "x" and "y".
    /// <list type="bullet">
    /// <item>Refused at both edges but not in the middle: the default ignorables of the hand-copied table, 4174 code points
    /// (the total DerivedCoreProperties.txt 16.0.0 gives), all in the categories the file lists for them (Cf, Mn, Lo,
    /// Cn), plus U+2800 (#182).</item>
    /// <item>Refused at the start only: exactly the combining marks (Mn, Me) that are not default ignorable (#182).</item>
    /// <item>Refused in the middle: exactly the 65 control characters and the 66 noncharacters (#176; #181: nothing else
    /// is refused between visible characters).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void T034_every_code_point_is_judged_as_decisions_176_and_182_say()
    {
        var edges = new List<int>();
        var startOnly = new List<int>();
        var middle = new List<int>();

        for (var codePoint = 0; codePoint <= 0x10FFFF; codePoint++)
        {
            // A surrogate is half of a UTF-16 pair, not a character; the JSON reader refuses a lone one (BUG-002).
            if (codePoint is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var c = char.ConvertFromUtf32(codePoint);
            var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
            var isMark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
            var atEnd = IsNotAllowed("x" + c);
            var atStart = IsNotAllowed(c + "x");
            var inMiddle = IsNotAllowed("x" + c + "y");

            if (inMiddle)
            {
                middle.Add(codePoint);
                // A control that is also white space (tab, line feed, U+0085) is trimmed at an edge instead.
                Assert.True((atEnd && atStart) || char.IsWhiteSpace(c, 0), $"U+{codePoint:X4} is refused in the middle only.");
            }
            else if (atEnd)
            {
                edges.Add(codePoint);
                Assert.True(atStart, $"U+{codePoint:X4} is refused at the end but not at the start.");
            }
            else if (atStart)
            {
                startOnly.Add(codePoint);
                Assert.True(isMark, $"U+{codePoint:X4} is refused at the start only but is not a combining mark.");
            }
            else
            {
                Assert.False(isMark, $"U+{codePoint:X4} is a combining mark but is allowed at the start.");
            }
        }

        Assert.Equal(4174 + 1, edges.Count); // the file's default ignorables, plus U+2800 (#182)
        UnicodeCategory[] fileCategories =
            [UnicodeCategory.Format, UnicodeCategory.NonSpacingMark, UnicodeCategory.OtherLetter, UnicodeCategory.OtherNotAssigned];
        Assert.All(edges.Where(codePoint => codePoint != 0x2800),
            codePoint => Assert.Contains(CharUnicodeInfo.GetUnicodeCategory(codePoint), fileCategories));
        Assert.Contains(0x200B, edges);
        Assert.Contains(0x3164, edges);
        Assert.Contains(0xE0FFF, edges); // the last default ignorable
        Assert.Contains(0x2800, edges);

        Assert.Contains(0x0301, startOnly);
        Assert.Contains(0x20DD, startOnly);
        Assert.DoesNotContain(0xFE0F, startOnly); // a variation selector is Mn, but default ignorable, so refused at both edges

        Assert.Equal(65 + 66, middle.Count);
        Assert.All(middle, codePoint => Assert.True(
            CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.Control
            || (codePoint is >= 0xFDD0 and <= 0xFDEF)
            || (codePoint & 0xFFFE) == 0xFFFE,
            $"U+{codePoint:X4} is refused in the middle but is neither a control character nor a noncharacter."));
    }

    /// <summary>
    /// True when the rule refuses <paramref name="value"/> with the not-allowed message. Every value here has a visible
    /// "x", so "Name is required." would mean the rule is broken; the assertion says so.
    /// </summary>
    private static bool IsNotAllowed(string value)
    {
        var errors = new ValidationErrors();
        if (NameRules.Validate(value, maxLength: 100, errors) is not null)
        {
            return false;
        }

        Assert.Equal([NameRules.NotAllowedCharacterMessage], errors.ToDictionary()["name"]);
        return true;
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
