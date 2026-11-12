using System.Text;
using System.Text.Json;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Shared helpers for the JSON that integration tests send to the API and read back: a raw JSON request body, a
/// response body as a <see cref="JsonElement"/>, and the two parts of a response tests look into most often, the ids
/// in a page of results and the messages in a validation error.
/// </summary>
/// <remarks>
/// A <c>static class</c> cannot be instantiated; it is just a home for helper methods, called as
/// <c>ApiJson.ReadAsync(response)</c>. Tests read responses as <see cref="JsonElement"/> rather than into C# classes so
/// that they check the exact JSON a client receives: field names, nulls and the number of fields.
/// </remarks>
public static class ApiJson
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>
    /// A request body holding exactly <paramref name="json"/>, sent as <c>application/json</c>. Use it for bodies an
    /// anonymous object cannot express: invalid JSON, a value of the wrong type, the literal <c>null</c>, or no body
    /// at all (an empty string).
    /// </summary>
    public static StringContent Content(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Reads the response body as JSON. Fails the test (by throwing) when the body is empty or is not JSON, because
    /// every response a test reads this way is expected to have a JSON body.
    /// </summary>
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);

        // A JsonDocument rents pooled memory and must be disposed. Clone() copies the root element out of it, so the
        // returned value stays readable after the document is gone.
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The <c>id</c> of every row in a page of results, in order (design 2.5: a page is
    /// <c>{ items, page, pageSize, totalCount }</c>).
    /// </summary>
    public static IEnumerable<int> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()).ToList();

    /// <summary>
    /// The messages a validation error body lists for one field (design 6: <c>errors</c>, keyed by the field's JSON
    /// path in camelCase, such as <c>name</c> or <c>lines[1].quantity</c>). Fails the test if the field is not listed.
    /// </summary>
    public static IEnumerable<string?> Messages(JsonElement body, string path) =>
        body.GetProperty("errors").GetProperty(path).EnumerateArray().Select(message => message.GetString()).ToList();
}
