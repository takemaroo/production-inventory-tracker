using System.Globalization;
using System.Text;
using InventoryTracker.Api.Domain;

namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// The one place where a customer, unit or item name from a request is cleaned and checked before it is compared and
/// stored (decisions #170, #171; AC-06.2, AC-07.1, AC-29.2). Every create and rename of the three lists calls
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
/// <b>Steps, in this order.</b> (1) Convert to NFC; a name .NET cannot convert (U+FFFE) is refused (decision #174).
/// (2) Trim leading and trailing white space (design 2.6). (3) A name with no visible character left is missing
/// (decision #171). (4) Check the length. (5) Compute the comparison key with
/// <see cref="NameNormalizer"/>. The caller then runs its duplicate check on that key. NFC comes first so that the
/// trimmed text, the length and the key are all taken from the same text that is stored.
/// </para>
/// <para>
/// <b>Stored names are not rewritten</b> (decision #172): rows saved before this rule keep their name and key as they
/// are. Only names created or renamed from now on pass through here.
/// </para>
/// </remarks>
public static class NameRules
{
    /// <summary>Design 7.2: the message for a missing, blank or invisible-only name (AC-06.2, AC-07.2, AC-29.2).</summary>
    public const string RequiredMessage = "Name is required.";

    /// <summary>
    /// Decision #174: the message for a name that cannot be put in NFC, because it holds U+FFFE (or, in principle, a lone
    /// surrogate). Same <c>400</c> <c>validation</c> shape as the other name errors, under <c>name</c>.
    /// </summary>
    public const string NotAllowedCharacterMessage = "Name contains a character that is not allowed.";

    /// <summary>
    /// Cleans <paramref name="value"/> and checks it, adding any problem to <paramref name="errors"/> under the
    /// <c>name</c> field. Returns the name to store and its comparison key, or null when the name was refused.
    /// </summary>
    /// <param name="value">The name exactly as the client sent it, or null when it was missing.</param>
    /// <param name="maxLength">The column limit for this list (<c>ColumnRules</c>), in Unicode code points.</param>
    /// <param name="errors">The request's collector, so a name problem is reported with the other fields' problems.</param>
    public static CleanName? Validate(string? value, int maxLength, ValidationErrors errors)
    {
        // Decision #174: a name .NET cannot normalize is refused before anything else, rather than becoming a 500.
        if (!TryToNfc(value, out var nfc))
        {
            errors.Add("name", NotAllowedCharacterMessage);
            return null;
        }

        // Decision #170: NFC first, then trim. Text.Clean trims and turns an empty result into null (design 2.6).
        var name = Text.Clean(nfc);

        // Decision #171: "\u200B" or " \uFEFF " has nothing a person can see, so it counts as missing, not as a name.
        if (name is null || !HasVisibleCharacter(name))
        {
            errors.Add("name", RequiredMessage);
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
    /// one <c>Normalize</c> call and only <see cref="ArgumentException"/>, so no other failure is hidden.
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
    /// Decision #173 (the exact rule for decision #171): true when <paramref name="name"/> has at least
    /// one code point that is not white space, not a control character and not a format character.
    /// </summary>
    /// <remarks>
    /// <para>The three invisible classes, checked per code point (<see cref="Rune"/>, so emoji count as one):</para>
    /// <list type="bullet">
    /// <item><b>White space</b>: <see cref="Rune.IsWhiteSpace"/>, the Unicode White_Space property (space, tab, line
    /// breaks, no-break space U+00A0, ideographic space U+3000 and the other space characters).</item>
    /// <item><b>Control</b>: category Cc (U+0000–U+001F, U+007F–U+009F), for example NUL.</item>
    /// <item><b>Format</b>: category Cf, invisible characters that only steer how text is shown: zero width space
    /// U+200B, zero width non-joiner U+200C, zero width joiner U+200D, word joiner U+2060, byte order mark U+FEFF,
    /// soft hyphen U+00AD, the left-to-right and right-to-left marks, and others.</item>
    /// </list>
    /// <para>
    /// The check only decides "is there anything to see". It never removes these characters from a name that also has
    /// visible ones: some scripts need a joiner (U+200C, U+200D) between letters, and emoji sequences use U+200D, so
    /// "Café\u200DSur" is kept exactly as sent (decision #171).
    /// </para>
    /// </remarks>
    private static bool HasVisibleCharacter(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var invisible = Rune.IsWhiteSpace(rune)
                || category == UnicodeCategory.Control
                || category == UnicodeCategory.Format;
            if (!invisible)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A name that passed <see cref="NameRules.Validate"/>: <see cref="Name"/> is what is stored and shown (NFC, trimmed),
/// <see cref="Normalized"/> the value for the <c>NameNormalized</c> column and the duplicate check.
/// </summary>
public sealed record CleanName(string Name, string Normalized);
