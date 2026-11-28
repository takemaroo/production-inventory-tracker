using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace InventoryTracker.Api.Common.Json;

/// <summary>
/// (v1.2) The one rule for how the API escapes text inside the JSON it writes (PRD A-5, AC-37.9; architecture 14,
/// decision #230). Both JSON settings of the app use <see cref="Encoder"/>: the controllers' (Program.cs) and the
/// minimal-API settings that every ProblemDetails error body is written with (<see cref="Errors.ErrorHandlingSetup"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What a <see cref="JavaScriptEncoder"/> is.</b> When System.Text.Json writes a string it asks its encoder which
/// characters may appear as they are and which must be written as an escape: a backslash, the letter <c>u</c> and four
/// hex digits. The escape is still the same character to every JSON reader, so after parsing the value is identical;
/// only the raw bytes on the wire differ. ASP.NET Core's own default for responses is the "relaxed" encoder
/// (<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>), which writes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and
/// <c>'</c> as they are. This class replaces it.
/// </para>
/// <para>
/// <b>What <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c> does.</b> <see cref="UnicodeRanges.All"/> is the whole
/// Basic Multilingual Plane (the first 65,536 code points: every Latin letter with accents, Greek, Cyrillic, Chinese and
/// so on). Passing it allows the plane's ordinary text through readable, so "Crème" stays "Crème" in the bytes. Not
/// every character of the plane, though: whatever the allowed ranges, an encoder made by <c>Create</c> always escapes
/// the characters that are dangerous in HTML or JavaScript (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>,
/// <c>"</c>, <c>+</c> and <c>`</c>), control characters, and a fixed list of its own. Checked on this app's .NET 10
/// runtime (T-038): that list includes the space characters other than the plain space (the no-break space U+00A0,
/// U+2007, U+3000), the line separator U+2028, the byte-order mark U+FEFF, private-use code points (U+E000),
/// unassigned ones (U+0378) and U+FFFF. Format characters such as U+202E (right-to-left override), U+200B, U+200D and
/// the soft hyphen U+00AD were written raw. Characters beyond the plane (emoji) are written as their two surrogate
/// escapes. For example the customer
/// name <c>&lt;/script&gt;</c> is sent with its <c>&lt;</c> as the six characters backslash, <c>u003C</c>, and its
/// <c>&gt;</c> as backslash, <c>u003E</c> (System.Text.Json writes the hex digits in upper case).
/// </para>
/// <para>
/// <b>Why, when the answer is already <c>application/json</c> and the front end shows text safely.</b> This is defence in
/// depth: a second lock that matters only if the first ones fail. Today a browser never treats this JSON as HTML: the
/// content type says JSON, <c>X-Content-Type-Options: nosniff</c> (A-2) forbids guessing otherwise, and the front end
/// puts every text into the page as text, never as HTML (AC-37.1, AC-37.2). But a stored name such as
/// <c>&lt;/script&gt;&lt;script&gt;alert(1)&lt;/script&gt;</c> is hostile in any HTML context, and a JSON answer can still
/// end up in one: pasted into a page by a future tool, saved to a file and opened in a browser, shown by an old or
/// misconfigured client, or copied into a <c>&lt;script&gt;</c> block. With the escapes, the raw bytes hold no
/// <c>&lt;</c> at all, so none of those mistakes can close a tag or open a new one. The cost is only that the raw text
/// is a little harder for a person to read; every program reads the same values (SEC-27 server hardening, Q108).
/// </para>
/// </remarks>
public static class JsonEscaping
{
    /// <summary>
    /// The encoder for every JSON body the API writes: BMP text readable, HTML-sensitive characters escaped (AC-37.9).
    /// One shared instance, because an encoder holds no per-request state and is safe to use from many threads.
    /// </summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
}
