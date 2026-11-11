using System.Net;
using System.Text;
using System.Text.Json;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-001: every error the API can produce has the design section 6 body (status, fixed <c>title</c>,
/// <c>code</c>, <c>detail</c>, <c>traceId</c>, and <c>errors</c> for validation) with
/// <c>Content-Type: application/problem+json</c>. The errors are raised by the test-only
/// <see cref="ErrorProbeController"/>, so these tests go through the real middleware pipeline.
/// </summary>
/// <remarks>
/// The probe actions are <c>[AllowAnonymous]</c>, but an unknown path or a wrong method matches no action, so the
/// fallback "signed-in" policy applies there (T-003). Those tests send a valid token, because design 2.2 checks the
/// token (401) before anything else.
/// </remarks>
public class ErrorHandlingTests(ProbeApiFactory factory) : IClassFixture<ProbeApiFactory>
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>T-001 AC 1: a NotFoundException gives 404 "Not found" with its code and detail, and no errors field.</summary>
    [Fact]
    public async Task NotFoundException_gives_404_problem_details()
    {
        var (response, body) = await GetAsync("/test-probe/not-found");

        AssertProblem(response, body, HttpStatusCode.NotFound, "Not found", "not-found", "Order #99 not found.");
        Assert.False(body.TryGetProperty("errors", out _));
    }

    /// <summary>T-001 AC 1: a ConflictException gives 409 "Conflict with current state" with the PRD message unchanged.</summary>
    [Fact]
    public async Task ConflictException_gives_409_problem_details()
    {
        var (response, body) = await GetAsync("/test-probe/conflict");

        AssertProblem(response, body, HttpStatusCode.Conflict, "Conflict with current state", "another-order-in-production",
            "Order #11 is in production. Only one order can be in production at a time.");
    }

    /// <summary>
    /// T-001 AC 1 and design 6: a ValidationException gives 400 "Invalid request", code validation, every message in
    /// <c>errors</c> under its JSON path, and <c>detail</c> holding all messages joined with a space.
    /// </summary>
    [Fact]
    public async Task ValidationException_gives_400_with_errors_per_path()
    {
        var (response, body) = await GetAsync("/test-probe/validation");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "Quantity must be greater than 0. Item 42 does not exist.");
        var errors = body.GetProperty("errors");
        Assert.Equal(new[] { "Quantity must be greater than 0." }, Messages(errors, "quantity"));
        Assert.Equal(new[] { "Item 42 does not exist." }, Messages(errors, "lines[1].itemId"));
    }

    /// <summary>Architecture 6: a BadRequestException gives 400 with its own code and no errors field.</summary>
    [Fact]
    public async Task BadRequestException_gives_400_with_its_own_code()
    {
        var (response, body) = await GetAsync("/test-probe/bad-request");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "receipt-not-raw-material",
            "Receipts are for raw materials only.");
        Assert.False(body.TryGetProperty("errors", out _));
    }

    /// <summary>
    /// T-001 AC 2: an unhandled exception gives 500 internal-error "An unexpected error occurred.", the body holds no
    /// exception text or stack trace, and the exception is logged at Error level with the exception attached.
    /// </summary>
    [Fact]
    public async Task Unhandled_exception_gives_500_without_internals_and_is_logged()
    {
        var (response, body) = await GetAsync("/test-probe/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("internal-error", body.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));

        var raw = body.GetRawText();
        Assert.DoesNotContain("INTERNAL-DETAIL-MARKER", raw);
        Assert.DoesNotContain(nameof(InvalidOperationException), raw);
        Assert.DoesNotContain("ErrorProbeController", raw);

        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Exception is InvalidOperationException { Message: ErrorProbeController.SecretMessage });
    }

    /// <summary>Architecture 6, Logging: an expected refusal (ApiException) is not logged as an application error.</summary>
    [Fact]
    public async Task Expected_refusals_are_not_logged_as_errors()
    {
        await GetAsync("/test-probe/not-found");

        Assert.DoesNotContain(factory.Logs.Entries, entry =>
            entry.Level >= LogLevel.Error && entry.Exception is ApiException);
    }

    /// <summary>
    /// Architecture 4.1 item 11, section 6 and design 7.2: a DbUpdateException from a unique index or primary key
    /// gives 409 concurrent-change with the design message, logged as a Warning, with no SQL in the body.
    /// </summary>
    [Theory]
    [InlineData("unique", 2067)]
    [InlineData("primary-key", 1555)]
    public async Task Uniqueness_violation_gives_409_concurrent_change(string kind, int extendedCode)
    {
        var (response, body) = await GetAsync($"/test-probe/database/{kind}");

        AssertProblem(response, body, HttpStatusCode.Conflict, "Conflict with current state", "concurrent-change",
            "The data changed while your request was being processed. Read it again and retry.");
        Assert.DoesNotContain("UNIQUE", body.GetRawText());
        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Category == typeof(ApiExceptionHandler).FullName &&
            entry.Exception is DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: var code } } &&
            code == extendedCode);
    }

    /// <summary>
    /// Architecture section 6 (only a unique index means concurrent-change): NOT NULL and FOREIGN KEY failures and a
    /// busy database are bugs or outages, so they give 500 internal-error and are logged at Error, not Warning.
    /// </summary>
    [Theory]
    [InlineData("not-null", 1299)]
    [InlineData("foreign-key", 787)]
    [InlineData("busy", 5)]
    public async Task Other_database_errors_give_500(string kind, int extendedCode)
    {
        var (response, body) = await GetAsync($"/test-probe/database/{kind}");

        AssertProblem(response, body, HttpStatusCode.InternalServerError, null, "internal-error", "An unexpected error occurred.");
        Assert.DoesNotContain("constraint", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Level == LogLevel.Error &&
            entry.Exception is DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: var code } } &&
            code == extendedCode);
    }

    /// <summary>Architecture section 6: a DbUpdateException with no database error inside gives 500 internal-error, logged at Error.</summary>
    [Fact]
    public async Task DbUpdateException_without_a_constraint_error_gives_500()
    {
        var (response, body) = await GetAsync("/test-probe/database/none");

        AssertProblem(response, body, HttpStatusCode.InternalServerError, null, "internal-error", "An unexpected error occurred.");
        Assert.Contains(factory.Logs.Entries, entry =>
            entry.Level == LogLevel.Error && entry.Exception is DbUpdateException { InnerException: null });
    }

    /// <summary>
    /// T-001 AC 3 and architecture 4.1 item 8: for a signed-in user, an unknown route gives 404 not-found
    /// "No endpoint matches this path.".
    /// </summary>
    [Fact]
    public async Task Unknown_route_gives_404_not_found()
    {
        var client = await SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/no-such-resource", Cancel);
        var body = await ReadJsonAsync(response);

        AssertProblem(response, body, HttpStatusCode.NotFound, "Not found", "not-found", "No endpoint matches this path.");
    }

    /// <summary>
    /// AC-01.5 and design 2.2 (401 is checked first): without a token, an unknown route or a wrong method gives the
    /// 401 not-authenticated ProblemDetails, not 404 or 405, so an anonymous caller cannot probe which paths exist.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/no-such-resource")]
    [InlineData("DELETE", "/test-probe/not-found")]
    public async Task Unauthenticated_unknown_route_or_method_gives_401(string method, string path)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        var response = await client.SendAsync(request, Cancel);
        var body = await ReadJsonAsync(response);

        AssertProblem(response, body, HttpStatusCode.Unauthorized, "Not signed in", "not-authenticated",
            "Your sign-in is missing or has expired. Sign in again.");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>
    /// T-001 AC 3 and architecture 4.1 item 8: a known path with the wrong method gives 405 method-not-allowed
    /// "This method is not allowed on this path.".
    /// </summary>
    [Fact]
    public async Task Wrong_method_gives_405_method_not_allowed()
    {
        var client = await SignedInClientAsync();

        var response = await client.DeleteAsync("/test-probe/not-found", Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("method-not-allowed", body.GetProperty("code").GetString());
        Assert.Equal("This method is not allowed on this path.", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    /// <summary>T-001 AC 3 and architecture 4.1 item 2: malformed JSON gives 400 validation "The request body is not valid JSON.".</summary>
    [Theory]
    [InlineData("{ \"quantity\": 12.5, }")]   // trailing comma
    [InlineData("{ \"quantity\": 12.5")]      // cut off
    [InlineData("quantity=12.5")]             // not JSON at all
    [InlineData("   ")]                       // only spaces
    public async Task Malformed_json_gives_400_not_valid_json(string json)
    {
        var (response, body) = await PostJsonAsync(json);

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "The request body is not valid JSON.");
    }

    /// <summary>
    /// T-001 AC 3, design 2.4 and architecture 4.1 item 2: a number sent as a string gives 400 validation
    /// "{field} has the wrong type.", with the field in errors.
    /// </summary>
    [Fact]
    public async Task Number_sent_as_string_gives_400_wrong_type()
    {
        var (response, body) = await PostJsonAsync("""{ "quantity": "12.5", "name": "x" }""");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation", "quantity has the wrong type.");
        Assert.Equal(new[] { "quantity has the wrong type." }, Messages(body.GetProperty("errors"), "quantity"));
        Assert.DoesNotContain("System.Decimal", body.GetRawText());
    }

    /// <summary>
    /// Design 6: body error paths are camelCase whatever spelling the client used. Property names are matched ignoring
    /// case, so {"Quantity": "abc"} is read, and its error is keyed quantity, the same as for query values.
    /// </summary>
    [Theory]
    [InlineData("""{ "Quantity": "abc" }""", "quantity")]
    [InlineData("""{ "quantity": 1, "Lines": [ { "Quantity": "x" } ] }""", "lines[0].quantity")]
    public async Task Body_type_error_paths_are_camel_case(string json, string path)
    {
        var (response, body) = await PostJsonAsync(json);

        var message = $"{path} has the wrong type.";
        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation", message);
        Assert.Equal(new[] { message }, Messages(body.GetProperty("errors"), path));
    }

    /// <summary>Design 6: a wrong type inside a list is keyed by its full JSON path, for example lines[1].quantity.</summary>
    [Fact]
    public async Task Wrong_type_in_a_nested_line_names_the_line()
    {
        var (response, body) = await PostJsonAsync("""{ "quantity": 1, "lines": [ { "quantity": 2 }, { "quantity": true } ] }""");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "lines[1].quantity has the wrong type.");
        Assert.Equal(new[] { "lines[1].quantity has the wrong type." }, Messages(body.GetProperty("errors"), "lines[1].quantity"));
    }

    /// <summary>Architecture 4.1 item 2: a body of the wrong kind altogether (an array for an object) is refused as a wrong type.</summary>
    [Fact]
    public async Task Body_of_the_wrong_type_gives_400()
    {
        var (response, body) = await PostJsonAsync("[1, 2]");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "The request body has the wrong type.");
    }

    /// <summary>Architecture 4.1 item 2: an empty JSON body where one is required gives 400 "A request body is required.".</summary>
    [Fact]
    public async Task Missing_body_gives_400_body_required()
    {
        var (response, body) = await PostJsonAsync("");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation", "A request body is required.");
    }

    /// <summary>
    /// Architecture 6, Validation: MVC's automatic 400 is only for unreadable bodies. A readable body with a field
    /// missing reaches the action, so services run their own checks in the design's order.
    /// </summary>
    [Fact]
    public async Task Readable_body_with_missing_fields_reaches_the_action()
    {
        var (response, _) = await PostJsonAsync("""{ "quantity": 12.5000 }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A body that is not sent as JSON gives 415 unsupported-media-type in the same ProblemDetails shape.</summary>
    [Fact]
    public async Task Non_json_content_type_gives_415()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync("/test-probe/body",
            new StringContent("quantity=1", Encoding.UTF8, "text/plain"), Cancel);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unsupported-media-type", body.GetProperty("code").GetString());
    }

    /// <summary>
    /// Design 6: even a client whose Accept header does not mention JSON gets the full ProblemDetails body
    /// (type, fixed title, code, traceId), not an empty or text/plain response. Covers a thrown ApiException and the
    /// bodiless 404 and 405 that UseStatusCodePages fills in. The client is signed in, so the 404 and 405 are reached
    /// (without a token they would be 401, see <see cref="Unauthenticated_unknown_route_or_method_gives_401"/>).
    /// </summary>
    [Theory]
    [InlineData("GET", "/test-probe/conflict", HttpStatusCode.Conflict, "Conflict with current state", "another-order-in-production",
        "Order #11 is in production. Only one order can be in production at a time.")]
    [InlineData("GET", "/api/v1/no-such-resource", HttpStatusCode.NotFound, "Not found", "not-found",
        "No endpoint matches this path.")]
    [InlineData("DELETE", "/test-probe/not-found", HttpStatusCode.MethodNotAllowed, null, "method-not-allowed",
        "This method is not allowed on this path.")]
    public async Task Client_not_asking_for_json_still_gets_problem_details(
        string method, string path, HttpStatusCode status, string? title, string code, string detail)
    {
        var client = await SignedInClientAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Accept.ParseAdd("text/html");

        var response = await client.SendAsync(request, Cancel);
        var body = await ReadJsonAsync(response);

        AssertProblem(response, body, status, title, code, detail);
    }

    /// <summary>
    /// Design 6: errors are keyed by camelCase path even when MVC names the value after the C# properties, in every
    /// segment (ItemId becomes itemId, Range.From becomes range.from).
    /// </summary>
    [Fact]
    public async Task Query_value_errors_are_keyed_in_camel_case()
    {
        var (response, body) = await GetAsync("/test-probe/filter?itemId=abc&range.from=xyz");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", body.GetProperty("code").GetString());
        var keys = body.GetProperty("errors").EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(["itemId", "range.from"], keys);
    }

    /// <summary>T-001 AC 6 and design 2.5: without parameters a list uses page 1 and page size 50, in the envelope { items, page, pageSize, totalCount }.</summary>
    [Fact]
    public async Task Paging_defaults_and_envelope()
    {
        var (response, body) = await GetAsync("/test-probe/paging");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, body.EnumerateObject().Count());
    }

    /// <summary>T-001 AC 6: page below 1 or pageSize outside 1–100 gives 400 with the design 7.2 messages.</summary>
    [Theory]
    [InlineData("page=0", "page", "page must be 1 or greater.")]
    [InlineData("page=-3", "page", "page must be 1 or greater.")]
    [InlineData("pageSize=0", "pageSize", "pageSize must be between 1 and 100.")]
    [InlineData("pageSize=101", "pageSize", "pageSize must be between 1 and 100.")]
    public async Task Paging_out_of_range_gives_400(string query, string field, string message)
    {
        var (response, body) = await GetAsync($"/test-probe/paging?{query}");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation", message);
        Assert.Equal(new[] { message }, Messages(body.GetProperty("errors"), field));
    }

    /// <summary>T-001 AC 6 and design 6: both paging errors are reported together, joined with a space in detail.</summary>
    [Fact]
    public async Task Paging_reports_both_errors_at_once()
    {
        var (response, body) = await GetAsync("/test-probe/paging?page=0&pageSize=500");

        AssertProblem(response, body, HttpStatusCode.BadRequest, "Invalid request", "validation",
            "page must be 1 or greater. pageSize must be between 1 and 100.");
    }

    /// <summary>T-001 AC 6: the limits themselves (page 1, pageSize 1 and 100) are accepted.</summary>
    [Theory]
    [InlineData("page=1&pageSize=1", 1, 1)]
    [InlineData("page=3&pageSize=100", 3, 100)]
    public async Task Paging_limits_are_accepted(string query, int page, int pageSize)
    {
        var (response, body) = await GetAsync($"/test-probe/paging?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(page, body.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, body.GetProperty("pageSize").GetInt32());
    }

    /// <summary>Design 2.5: a paging value that is not a number is 400 validation, keyed by the parameter name.</summary>
    [Fact]
    public async Task Paging_value_that_is_not_a_number_gives_400()
    {
        var (response, body) = await GetAsync("/test-probe/paging?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("page", out _));
    }

    /// <summary>A client with a valid token; any role will do, because these paths have no role rule.</summary>
    private Task<HttpClient> SignedInClientAsync() => factory.CreateClientAsAsync(Roles.Warehouse);

    private async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(string path)
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync(path, Cancel);
        return (response, await ReadJsonAsync(response));
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> PostJsonAsync(string json)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsync("/test-probe/body",
            new StringContent(json, Encoding.UTF8, "application/json"), Cancel);
        return (response, await ReadJsonAsync(response));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Cancel);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Checks the fields every design 6 error body has.</summary>
    /// <param name="title">The design 6 fixed title, or null for statuses that keep the framework's default title.</param>
    private static void AssertProblem(HttpResponseMessage response, JsonElement body,
        HttpStatusCode status, string? title, string code, string detail)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("type").GetString()));
        if (title is not null)
        {
            Assert.Equal(title, body.GetProperty("title").GetString());
        }

        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(detail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    private static string[] Messages(JsonElement errors, string path) =>
        errors.GetProperty(path).EnumerateArray().Select(message => message.GetString()!).ToArray();
}
