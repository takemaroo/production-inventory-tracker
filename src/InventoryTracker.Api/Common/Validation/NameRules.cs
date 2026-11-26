using System.Globalization;
using System.Text;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// The one place where a customer, unit or item name from a request is cleaned and checked before it is compared and
/// stored (decisions #170, #171, #176; AC-06.2, AC-07.1, AC-29.2). Every create and rename of the three lists calls
/// <see cref="Validate"/>, so all three follow the same rule.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Unicode normalization (BUG-002).</b> Unicode can write some letters in more than one way. "é" can be one
/// code point, U+00E9 (the <em>precomposed</em> form a Windows keyboard types), or two: "e" followed by U+0301
/// COMBINING ACUTE ACCENT (the <em>decomposed</em> form text pasted from macOS, a PDF or some web pages often has). Both
/// look the same on screen, but a plain string comparison sees different characters, so "Café Sur" could be created
/// twice. <see cref="string.Normalize(NormalizationForm)"/> rewrites text into one agreed form. We use
/// <b>NFC</b> (Normalization Form C, "canonical composition"): every letter-plus-accent that has a precomposed code point
/// becomes that one code point. NFC is the form most software produces and expects, and the form the owner's Windows
/// keyboard already types, so names typed on that machine are stored exactly as before.
/// </para>
/// <para>
/// <b>Steps, in this order</b> (decision #181 records why).
/// (1) Convert to NFC; a name .NET cannot convert (U+FFFE) is refused as "not allowed" (decision #174).
/// (2) Trim leading and trailing white space (design 2.6).
/// (3) A name with no visible character left is missing, "Name is required." (decisions #171, #173, and #176 for the
/// Hangul fillers and U+2800).
/// (4) T-034, SEC-25, decisions #176 and #182: refuse a control character or a noncharacter anywhere, an invisible
/// ("default ignorable") character or U+2800 at the start or the end, and a combining mark at the start, as "not
/// allowed".
/// (5) Check the length.
/// (6) Compute the comparison key with <see cref="NameNormalizer"/>. The caller then runs its duplicate check on that key.
/// </para>
/// <para>
/// Step 3 comes before step 4 on purpose: a name made only of invisible characters, such as a lone zero width space
/// (U+200B), is reported as missing, because that is what the user sees, even though U+200B is also an invisible
/// character "at the start". Step 4 comes before the length, as the #174 check does, because a name with a forbidden
/// character must be retyped whatever its length.
/// </para>
/// <para>
/// <b>Stored names are not rewritten</b> (decision #172): rows saved before a rule existed keep their name and key as
/// they are. Only names created or renamed from now on pass through here. A rename that sends the record's own stored
/// name back unchanged is not held to the T-034 rules (see <see cref="Validate"/>), so an old record can still be edited.
/// </para>
/// </remarks>
public static class NameRules
{
    /// <summary>Design 7.2: the message for a missing, blank or invisible-only name (AC-06.2, AC-07.2, AC-29.2).</summary>
    public const string RequiredMessage = "Name is required.";

    /// <summary>
    /// Decision #174: the message for a name holding a character that is not allowed: one .NET cannot put in NFC (U+FFFE),
    /// and, from T-034, a control character or noncharacter anywhere, an invisible character at the start or end (decision
    /// #176), U+2800 BRAILLE PATTERN BLANK at the start or end, or a combining mark as the first character (decision #182).
    /// Same <c>400</c> <c>validation</c> shape as the other name errors, under <c>name</c>.
    /// </summary>
    public const string NotAllowedCharacterMessage = "Name contains a character that is not allowed.";

    /// <summary>
    /// Cleans <paramref name="value"/> and checks it, adding any problem to <paramref name="errors"/> under the
    /// <c>name</c> field. Returns the name to store and its comparison key, or null when the name was refused.
    /// </summary>
    /// <param name="value">The name exactly as the client sent it, or null when it was missing.</param>
    /// <param name="maxLength">The column limit for this list (<c>ColumnRules</c>), in Unicode code points.</param>
    /// <param name="errors">The request's collector, so a name problem is reported with the other fields' problems.</param>
    /// <param name="storedName">
    /// On a rename, the record's name as it is stored now; null on a create. When the cleaned name is exactly this text
    /// (ordinal comparison, the same test the customer log uses for "name changed", decision #179), the T-034 rules are
    /// skipped (decision #181): the stored name was accepted under the rules of its day and is not rewritten (#172), so
    /// a <c>PUT</c> that only edits an item's description or a customer's notes must not fail because of it. Any real
    /// change of the name, a change of letter case included, follows every rule.
    /// </param>
    public static CleanName? Validate(string? value, int maxLength, ValidationErrors errors, string? storedName = null)
    {
        // Decision #174: a name .NET cannot normalize is refused before anything else, rather than becoming a 500.
        if (!TryToNfc(value, out var nfc))
        {
            errors.Add("name", NotAllowedCharacterMessage);
            return null;
        }

        // Decision #170: NFC first, then trim. Text.Clean trims and turns an empty result into null (design 2.6).
        var name = Text.Clean(nfc);

        // Decision #181: the record's own stored name, sent back unchanged, is checked only by the rules it was stored
        // under (BUG-002), not by the T-034 rules below.
        var applyT034Rules = storedName is null || !string.Equals(name, storedName, StringComparison.Ordinal);

        // Decisions #171, #173: a lone U+200B, or spaces around U+FEFF, has nothing a person can see, so it counts as
        // missing, not as a name. Decision #176 adds the Hangul fillers and U+2800 to what counts as invisible here.
        if (name is null || !HasVisibleCharacter(name, countBlankLookingAsInvisible: applyT034Rules))
        {
            errors.Add("name", RequiredMessage);
            return null;
        }

        // T-034, SEC-25, decision #176: controls and noncharacters anywhere, invisible characters at either end.
        if (applyT034Rules && HasCharacterNotAllowed(name))
        {
            errors.Add("name", NotAllowedCharacterMessage);
            return null;
        }

        // Design 2.6, decision #116: the limit is counted in code points on the cleaned text, after NFC and trimming,
        // because that is what is stored. A decomposed "é" (2 code points) becomes 1 here, so it counts as 1.
        if (Text.Length(name) > maxLength)
        {
            errors.Add("name", $"Name must be {maxLength} characters or fewer.");
            return null;
        }

        return new CleanName(name, NameNormalizer.Normalize(name));
    }

    /// <summary>
    /// Decision #174: <paramref name="value"/> in NFC, or false when .NET cannot normalize it. A null value (missing
    /// name) is fine and stays null, so the caller reports it as "Name is required.".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="string.Normalize(NormalizationForm)"/> throws <see cref="ArgumentException"/> when the text holds a
    /// code point it treats as invalid. A scan of every code point (code review, BUG-002) found exactly one that is
    /// valid UTF-16 and valid JSON and still throws: U+FFFE, a "noncharacter" that Unicode reserves and never assigns.
    /// System.Text.Json accepts it, so without this check it became a <c>500</c>. A lone surrogate (half of a UTF-16
    /// pair) would throw too, but the JSON reader already refuses one with <c>400</c> before the service runs.
    /// </para>
    /// <para>
    /// Why catch instead of pre-checking for U+FFFE: the exception is .NET's own definition of "cannot be normalized",
    /// so the rule stays right if a later .NET version changes which code points it refuses. The catch covers only the
    /// one <c>Normalize</c> call and only <see cref="ArgumentException"/>, so no other failure is hidden. The other
    /// noncharacters do not throw; T-034 refuses them later, in <see cref="HasCharacterNotAllowed"/>.
    /// </para>
    /// </remarks>
    private static bool TryToNfc(string? value, out string? nfc)
    {
        try
        {
            nfc = value?.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            nfc = null;
            return false;
        }
    }

    /// <summary>
    /// Decision #173 (the exact rule for decision #171), widened by decision #176: true when <paramref name="name"/> has
    /// at least one code point that is not white space, not a control character, not a format character and (when
    /// <paramref name="countBlankLookingAsInvisible"/> is true) not a Hangul filler or the braille blank.
    /// </summary>
    /// <remarks>
    /// <para>The invisible classes, checked per code point (<see cref="Rune"/>, so emoji count as one):</para>
    /// <list type="bullet">
    /// <item><b>White space</b>: <see cref="Rune.IsWhiteSpace"/>, the Unicode White_Space property (space, tab, line
    /// breaks, no-break space U+00A0, ideographic space U+3000 and the other space characters).</item>
    /// <item><b>Control</b>: category Cc (U+0000 to U+001F, U+007F to U+009F), for example NUL.</item>
    /// <item><b>Format</b>: category Cf, invisible characters that only steer how text is shown: zero width space
    /// U+200B, zero width non-joiner U+200C, zero width joiner U+200D, word joiner U+2060, byte order mark U+FEFF,
    /// soft hyphen U+00AD, the left-to-right and right-to-left marks, and others.</item>
    /// <item><b>Blank-looking letters and symbols</b> (decision #176): the four Hangul fillers and U+2800 BRAILLE
    /// PATTERN BLANK. Unicode files them as a letter (Lo) and a symbol (So), so the category test above misses them,
    /// but they show as empty space. See <see cref="IsBlankLooking"/>.</item>
    /// </list>
    /// <para>
    /// The check only decides "is there anything to see". It never removes these characters from a name that also has
    /// visible ones: some scripts need a joiner (U+200C, U+200D) between letters, and emoji sequences use U+200D, so
    /// "Café", U+200D, "Sur" is kept exactly as sent (decision #171).
    /// </para>
    /// <para>
    /// <paramref name="countBlankLookingAsInvisible"/> is false only for a stored name sent back unchanged (decision
    /// #181), which is judged by the BUG-002 rule it was stored under.
    /// </para>
    /// </remarks>
    private static bool HasVisibleCharacter(string name, bool countBlankLookingAsInvisible)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var invisible = Rune.IsWhiteSpace(rune)
                || category == UnicodeCategory.Control
                || category == UnicodeCategory.Format
                || (countBlankLookingAsInvisible && IsBlankLooking(rune.Value));
            if (!invisible)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decision #176: the code points that Unicode classes as a letter or symbol but that show as blank space, so that a
    /// name made only of them is "Name is required.".
    /// </summary>
    /// <remarks>
    /// The Hangul fillers are placeholders in Korean text encoding (they stand for "no consonant" or "no vowel" in a
    /// syllable); on their own they draw nothing. U+2800 is the braille cell with no dots raised. All five showed as
    /// blank customers in the security review (SEC-25). At the start or end of a name with visible characters, all five
    /// are refused as "not allowed" (<see cref="IsBlankAtAnEdge"/>).
    /// </remarks>
    private static bool IsBlankLooking(int codePoint) => codePoint is
        0x115F     // HANGUL CHOSEONG FILLER
        or 0x1160  // HANGUL JUNGSEONG FILLER
        or 0x3164  // HANGUL FILLER
        or 0xFFA0  // HALFWIDTH HANGUL FILLER
        or 0x2800; // BRAILLE PATTERN BLANK

    /// <summary>
    /// T-034, SEC-25, decisions #176 and #182: true when <paramref name="name"/> (already in NFC, trimmed and known to
    /// have a visible character) holds a control character or a noncharacter anywhere, starts or ends with a default
    /// ignorable code point or U+2800, or starts with a combining mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Anywhere.</b> A control character (category Cc: NUL, tab, line feed, carriage return, U+0085 NEXT LINE and the
    /// rest of U+0000 to U+001F and U+007F to U+009F) has no place in a name: it does not print, it can split a line in a
    /// list or a log, and the planned PostgreSQL port refuses NUL in text (SEC-09). A tab or line break at either end
    /// never gets here, because trimming already removed it (design 2.6); only one inside the name is refused.
    /// </para>
    /// <para>
    /// <b>At the start or the end only.</b> A default ignorable character, such as U+200B or a variation selector, draws
    /// nothing at the edge of a name, so "Deli Norte" followed by one looks exactly like "Deli Norte" but is a different
    /// name (SEC-25's twin customers). Between letters, the same characters can be needed: Persian writes a zero width
    /// non-joiner (U+200C) inside words, and emoji sequences join pictures with U+200D, so decision #171 keeps them
    /// allowed there. Other invisible characters in the middle (U+200B, the bidirectional controls such as U+202E) also
    /// stay allowed, as decision #181 records: refusing them would not stop look-alike names (a Cyrillic letter that looks
    /// like a Latin one makes one just as well, and cannot be refused), and the front end is to show ids beside names and
    /// make bidirectional controls visible (SEC-25, SEC-27). Decision #182 adds U+2800 to this edge rule
    /// (<see cref="IsBlankAtAnEdge"/>).
    /// </para>
    /// <para>
    /// <b>At the start only.</b> Decision #182: a combining mark has no letter to sit on there (<see cref="IsCombiningMark"/>).
    /// </para>
    /// <para>
    /// Only the first and the last code point are checked for the edge rules. A name such as U+200B U+200B "x" is refused
    /// for its first code point, which is enough.
    /// </para>
    /// </remarks>
    private static bool HasCharacterNotAllowed(string name)
    {
        int? first = null;
        var last = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control || IsNoncharacter(rune.Value))
            {
                return true;
            }

            first ??= rune.Value;
            last = rune.Value;
        }

        // The caller passes a name with at least one visible code point, so first always has a value here.
        return IsBlankAtAnEdge(first!.Value) || IsBlankAtAnEdge(last) || IsCombiningMark(first.Value);
    }

    /// <summary>
    /// Decisions #176 and #182: true for a code point that is refused as the first or last character of a name, because
    /// there it draws nothing and makes a twin of the name without it: every default ignorable code point, and U+2800
    /// BRAILLE PATTERN BLANK.
    /// </summary>
    /// <remarks>
    /// U+2800 is not default ignorable (Unicode files it as a symbol, So), so <see cref="DefaultIgnorableRanges"/> does
    /// not hold it. It still shows as an empty cell, and "Deli Norte" followed by it was one of SEC-25's twins, so
    /// decision #182 adds it to the edge rule by name. Between visible characters it stays allowed, like the default
    /// ignorables.
    /// </remarks>
    private static bool IsBlankAtAnEdge(int codePoint) => IsDefaultIgnorable(codePoint) || codePoint == 0x2800;

    /// <summary>
    /// Decision #182: true for a combining mark (category Mn, non-spacing, such as the combining acute accent U+0301, or
    /// Me, enclosing, such as the combining enclosing circle U+20DD), refused as the <em>first</em> code point of a name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A combining mark is drawn on the character before it: "e" followed by U+0301 shows as "é". At the start of a name
    /// there is nothing before it, so it floats over empty space or over whatever the screen showed just before the name
    /// (a quote, a list bullet), and a lone one looks blank. SEC-25 listed "a lone combining accent" among the names that
    /// were accepted and showed as blank.
    /// </para>
    /// <para>
    /// Only the first code point is checked. A mark after a letter is how decomposed text, and scripts with no
    /// precomposed letters, are written, so it stays allowed everywhere else, at the end included. The check runs after
    /// NFC, so a decomposed "é" at the start has already become the one code point U+00E9 and is not affected. Spacing
    /// marks (category Mc, such as Devanagari vowel signs) are not included: decision #182 covers Mn and Me only.
    /// </para>
    /// </remarks>
    private static bool IsCombiningMark(int codePoint)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    }

    /// <summary>
    /// T-034, decision #176: true for a Unicode <em>noncharacter</em>: U+FDD0 to U+FDEF, and the last two code points of
    /// every plane (U+FFFE, U+FFFF, U+1FFFE, U+1FFFF and so on up to U+10FFFF).
    /// </summary>
    /// <remarks>
    /// Unicode sets these 66 code points aside for a program's own internal use and promises never to give them a meaning,
    /// so they never belong in text exchanged between people. A code point "ends in FFFE or FFFF" when its lowest 16 bits
    /// are 0xFFFE or 0xFFFF; masking with 0xFFFE clears the lowest bit, so one comparison covers both. U+FFFD, the
    /// replacement character, is a normal character and is not one of them.
    /// </remarks>
    private static bool IsNoncharacter(int codePoint) =>
        (codePoint is >= 0xFDD0 and <= 0xFDEF) || (codePoint & 0xFFFE) == 0xFFFE;

    /// <summary>
    /// T-034, decision #176: true when <paramref name="codePoint"/> has the Unicode property
    /// <c>Default_Ignorable_Code_Point</c>.
    /// </summary>
    /// <remarks>
    /// "Default ignorable" is Unicode's own list of code points that software should draw as nothing at all when it does
    /// not support them: zero width characters, joiners, bidirectional controls, variation selectors, the Hangul fillers,
    /// tag characters, and ranges reserved for more of the same. .NET exposes the general category of a character but not
    /// this property, so the ranges are copied in <see cref="DefaultIgnorableRanges"/>. The list holds only 27
    /// ranges, so a plain scan is fast enough; it runs for two code points per name.
    /// </remarks>
    private static bool IsDefaultIgnorable(int codePoint)
    {
        foreach (var (first, last) in DefaultIgnorableRanges)
        {
            if (codePoint >= first && codePoint <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every range of the <c>Default_Ignorable_Code_Point</c> property, copied line for line from the Unicode Character
    /// Database file DerivedCoreProperties.txt, version 16.0.0 (2024-05-31,
    /// https://www.unicode.org/Public/16.0.0/ucd/DerivedCoreProperties.txt). Version 17.0.0 (2025-07-30) has the same
    /// ranges. The total is 4174 code points, the count the file gives. If a future Unicode version changes the list,
    /// copy it again from that file.
    /// </summary>
    /// <remarks>
    /// The four Hangul fillers (U+115F, U+1160, U+3164, U+FFA0) are in this list already, so "starts or ends with a
    /// default ignorable or a Hangul filler" (SEC-25) needs no separate check. U+2800 (braille blank) and U+00A0
    /// (no-break space) are not default ignorable: U+2800 is added to the edge rule by name (<see cref="IsBlankAtAnEdge"/>,
    /// decision #182) and counts as invisible for "Name is required." (<see cref="IsBlankLooking"/>), and white space at
    /// the edges is trimmed before this check.
    /// </remarks>
    private static readonly (int First, int Last)[] DefaultIgnorableRanges =
    [
        (0x00AD, 0x00AD),   // Cf  SOFT HYPHEN
        (0x034F, 0x034F),   // Mn  COMBINING GRAPHEME JOINER
        (0x061C, 0x061C),   // Cf  ARABIC LETTER MARK
        (0x115F, 0x1160),   // Lo  HANGUL CHOSEONG FILLER..HANGUL JUNGSEONG FILLER
        (0x17B4, 0x17B5),   // Mn  KHMER VOWEL INHERENT AQ..KHMER VOWEL INHERENT AA
        (0x180B, 0x180D),   // Mn  MONGOLIAN FREE VARIATION SELECTOR ONE..THREE
        (0x180E, 0x180E),   // Cf  MONGOLIAN VOWEL SEPARATOR
        (0x180F, 0x180F),   // Mn  MONGOLIAN FREE VARIATION SELECTOR FOUR
        (0x200B, 0x200F),   // Cf  ZERO WIDTH SPACE..RIGHT-TO-LEFT MARK (includes ZWNJ U+200C and ZWJ U+200D)
        (0x202A, 0x202E),   // Cf  LEFT-TO-RIGHT EMBEDDING..RIGHT-TO-LEFT OVERRIDE
        (0x2060, 0x2064),   // Cf  WORD JOINER..INVISIBLE PLUS
        (0x2065, 0x2065),   // Cn  reserved
        (0x2066, 0x206F),   // Cf  LEFT-TO-RIGHT ISOLATE..NOMINAL DIGIT SHAPES
        (0x3164, 0x3164),   // Lo  HANGUL FILLER
        (0xFE00, 0xFE0F),   // Mn  VARIATION SELECTOR-1..VARIATION SELECTOR-16
        (0xFEFF, 0xFEFF),   // Cf  ZERO WIDTH NO-BREAK SPACE (byte order mark)
        (0xFFA0, 0xFFA0),   // Lo  HALFWIDTH HANGUL FILLER
        (0xFFF0, 0xFFF8),   // Cn  reserved
        (0x1BCA0, 0x1BCA3), // Cf  SHORTHAND FORMAT LETTER OVERLAP..SHORTHAND FORMAT UP STEP
        (0x1D173, 0x1D17A), // Cf  MUSICAL SYMBOL BEGIN BEAM..MUSICAL SYMBOL END PHRASE
        (0xE0000, 0xE0000), // Cn  reserved
        (0xE0001, 0xE0001), // Cf  LANGUAGE TAG
        (0xE0002, 0xE001F), // Cn  reserved
        (0xE0020, 0xE007F), // Cf  TAG SPACE..CANCEL TAG
        (0xE0080, 0xE00FF), // Cn  reserved
        (0xE0100, 0xE01EF), // Mn  VARIATION SELECTOR-17..VARIATION SELECTOR-256
        (0xE01F0, 0xE0FFF), // Cn  reserved
    ];
}

/// <summary>
/// A name that passed <see cref="NameRules.Validate"/>: <see cref="Name"/> is what is stored and shown (NFC, trimmed),
/// <see cref="Normalized"/> the value for the <c>NameNormalized</c> column and the duplicate check.
/// </summary>
public sealed record CleanName(string Name, string Normalized);
