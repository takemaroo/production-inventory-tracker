using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;
using InventoryTracker.Api.Tests.Integration.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// T-021: the OpenAPI description (PRD section 5: "publishes an OpenAPI description listing every endpoint, at least in
/// the development environment") and the sample requests file. The endpoints are compared with the T-020 role matrix,
/// <see cref="RoleMatrixTests.Endpoints"/>: design section 4 row for row, with each endpoint's success status and roles.
/// </summary>
/// <remarks>
/// The document is fetched over HTTP from the running app in the Development environment (<see cref="DevelopmentApp"/>),
/// exactly as a client would fetch it, and read as plain JSON with <see cref="JsonDocument"/>.
/// </remarks>
public class OpenApiTests(OpenApiTests.DevelopmentApp app) : IClassFixture<OpenApiTests.DevelopmentApp>
{
    private const string DocumentPath = "/openapi/v1.json";
    private const string ProblemReference = "#/components/schemas/ProblemDetails";

    /// <summary>
    /// The endpoints that can answer <c>409</c> because of a business rule (PRD acceptance criteria, design 7.2): duplicates,
    /// records in use, last Manager, order and complementary order and return status, another order in production, pending
    /// returns, stock. Every other endpoint must not document a <c>409</c>.
    /// </summary>
    private static readonly HashSet<string> ConflictEndpoints =
    [
        "POST /users", "PUT /users/{id}/roles", "POST /users/{id}/deactivate",
        "POST /units", "PUT /units/{id}", "DELETE /units/{id}",
        "POST /items", "PUT /items/{id}", "DELETE /items/{id}",
        "POST /adjustments",
        "POST /production-orders/{number}/send", "POST /production-orders/{number}/complete",
        "POST /production-orders/{number}/cancel", "POST /production-orders/{number}/complementary-orders",
        "POST /complementary-orders/{number}/approve", "POST /complementary-orders/{number}/reject",
        "POST /complementary-orders/{number}/send",
        "POST /returns", "POST /returns/{number}/receive", "POST /returns/{number}/cancel",

        // (v1.1) T-024: a duplicate customer name (AC-29.2) and deleting a customer with sales orders (AC-29.5).
        // Deactivate and reactivate can fail only with 404 (architecture 5.1), so they document no 409.
        "POST /customers", "PUT /customers/{id}", "DELETE /customers/{id}",

        // (v1.1) T-025: a sales order for a deactivated customer (AC-24.4). The two reads cannot conflict.
        "POST /sales-orders",

        // (v1.1) T-027: shipping a sales order that is Shipped or Cancelled (AC-26.7), or without the stock (AC-26.3).
        "POST /sales-orders/{number}/ship",
    ];

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>One case per endpoint of design section 4, named <c>METHOD /path</c>.</summary>
    public static TheoryData<string> EndpointCases => new(RoleMatrixTests.Endpoints.Select(row => row.Key));

    // ---- Every endpoint is listed ------------------------------------------------------------------------------------

    /// <summary>
    /// PRD section 5, T-021 AC 1: in Development, <c>/openapi/v1.json</c> lists exactly the method and path pairs of
    /// design section 4 that exist so far (<see cref="RoleMatrixTests.DesignEndpointCount"/>), no more and no fewer.
    /// </summary>
    [Fact]
    public async Task Document_lists_exactly_the_endpoints_of_design_section_4()
    {
        using var document = await FetchDocumentAsync();

        var documented = Operations(document).Select(operation => operation.Key).Order(StringComparer.Ordinal).ToList();
        var expected = RoleMatrixTests.Endpoints.Select(row => row.Key).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(RoleMatrixTests.DesignEndpointCount, expected.Count);
        Assert.Equal(expected, documented);
    }

    /// <summary>
    /// T-021 AC 2: each endpoint's summary comes from its <c>///</c> comment, and design 4's success status is the one
    /// success response documented (<c>201</c> for creates, <c>204</c> for change-password, deletes and mark-read).
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task Endpoint_has_a_summary_and_its_success_status(string endpoint)
    {
        var row = RoleMatrixTests.Endpoints.Single(row => row.Key == endpoint);
        using var document = await FetchDocumentAsync();
        var operation = Operations(document)[endpoint];

        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()), $"{endpoint} has no summary.");
        var success = Responses(operation).Where(status => status.StartsWith('2')).ToList();
        Assert.Equal([((int)row.Success).ToString()], success);
    }

    /// <summary>
    /// T-021 AC 2: the summary is the text of the action's <c>///</c> comment, word for word (sign-in's, in
    /// <c>AuthController</c>), so the description really is generated from the code's comments.
    /// </summary>
    [Fact]
    public async Task Summary_is_the_text_of_the_actions_xml_comment()
    {
        using var document = await FetchDocumentAsync();

        var summary = Operations(document)["POST /auth/sign-in"].GetProperty("summary").GetString();

        Assert.StartsWith("Signs in with email and password and returns a bearer token valid for 8 hours (AC-01.1, AC-01.2).", summary);
    }

    // ---- Bearer security -------------------------------------------------------------------------------------------

    /// <summary>
    /// T-021 AC 1, US-01: the document declares the Bearer scheme (HTTP bearer, JWT), so clients know to send
    /// <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    [Fact]
    public async Task Document_declares_the_bearer_scheme()
    {
        using var document = await FetchDocumentAsync();

        var scheme = document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    /// <summary>
    /// AC-01.5 as documented: every endpoint except sign-in requires the Bearer scheme and documents its <c>401</c>; sign-in
    /// requires nothing and documents its own <c>401</c> (refused credentials, AC-01.3).
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task Endpoint_requires_the_bearer_token_unless_it_is_sign_in(string endpoint)
    {
        var row = RoleMatrixTests.Endpoints.Single(row => row.Key == endpoint);
        using var document = await FetchDocumentAsync();
        var operation = Operations(document)[endpoint];

        var schemes = operation.TryGetProperty("security", out var security)
            ? security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject().Select(scheme => scheme.Name)).ToList()
            : [];
        Assert.Equal(row.NeedsToken ? ["Bearer"] : [], schemes);
        Assert.Contains("401", Responses(operation));
    }

    // ---- Error responses -------------------------------------------------------------------------------------------

    /// <summary>
    /// Design 2.2 and 6 as documented, for each endpoint: <c>403</c> exactly when PRD 4.2 limits it to some roles;
    /// <c>404</c> exactly when the path names a record; <c>400</c> exactly when it reads a body or a query string;
    /// <c>413</c> <c>request-too-large</c> exactly when it reads a body (SEC-01, SEC-02);
    /// <c>409</c> exactly for the endpoints a business rule can refuse with a conflict. Every 4xx response is the design 6
    /// ProblemDetails body, as <c>application/problem+json</c> only.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndpointCases))]
    public async Task Endpoint_documents_its_error_responses_as_problem_details(string endpoint)
    {
        var row = RoleMatrixTests.Endpoints.Single(row => row.Key == endpoint);
        using var document = await FetchDocumentAsync();
        var operation = Operations(document)[endpoint];
        var statuses = Responses(operation);

        var hasQuery = operation.TryGetProperty("parameters", out var parameters)
            && parameters.EnumerateArray().Any(parameter => parameter.GetProperty("in").GetString() == "query");
        var hasBody = operation.TryGetProperty("requestBody", out _);

        Assert.Equal(row.Action.Allowed.Length < Roles.All.Count, statuses.Contains("403"));
        Assert.Equal(row.Template.Contains('{'), statuses.Contains("404"));
        Assert.Equal(hasQuery || hasBody, statuses.Contains("400"));
        Assert.Equal(hasBody, statuses.Contains("413"));
        Assert.Equal(ConflictEndpoints.Contains(endpoint), statuses.Contains("409"));

        foreach (var status in statuses.Where(status => status.StartsWith('4')))
        {
            var response = operation.GetProperty("responses").GetProperty(status);
            Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("description").GetString()));
            var content = response.GetProperty("content").EnumerateObject().ToList();
            Assert.Equal(["application/problem+json"], content.Select(media => media.Name));
            Assert.Equal(ProblemReference, content[0].Value.GetProperty("schema").GetProperty("$ref").GetString());
        }
    }

    /// <summary>
    /// Design 6: the shared ProblemDetails schema describes this API's error body, with <c>code</c> and <c>traceId</c>
    /// always present and <c>errors</c> (field path to messages) when fields are to blame.
    /// </summary>
    [Fact]
    public async Task Problem_details_schema_is_the_design_6_error_body()
    {
        using var document = await FetchDocumentAsync();

        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails");

        var properties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order().ToList();
        Assert.Equal(["code", "detail", "errors", "status", "title", "traceId", "type"], properties);
        var required = schema.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Order().ToList();
        Assert.Equal(["code", "detail", "status", "title", "traceId"], required);
    }

    // ---- Accurate shapes (follow-ups routed to T-021) ----------------------------------------------------------------

    /// <summary>
    /// Follow-up from T-013: no list advertises a <c>Skip</c> query parameter (the computed <c>PageQuery.Skip</c>);
    /// every list has exactly <c>page</c> and <c>pageSize</c> as integers, plus its own filters.
    /// </summary>
    [Fact]
    public async Task No_endpoint_advertises_a_skip_parameter()
    {
        using var document = await FetchDocumentAsync();

        foreach (var (key, operation) in Operations(document))
        {
            var names = operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!).ToList()
                : [];
            Assert.DoesNotContain(names, name => name.Equals("skip", StringComparison.OrdinalIgnoreCase));

            if (names.Contains("page"))
            {
                Assert.Contains("pageSize", names);
                foreach (var paging in parameters.EnumerateArray().Where(parameter => parameter.GetProperty("name").GetString() is "page" or "pageSize"))
                {
                    Assert.True(paging.GetProperty("schema").GetProperty("type").GetString() == "integer", $"{key}: paging is not an integer.");
                }
            }
        }
    }

    /// <summary>
    /// Design 2.4 (numbers sent as strings are refused) and 2.3 (enums are names): nothing in the document describes a
    /// number as "integer or string"; quantities are exact decimals; enums are lists of names.
    /// </summary>
    [Fact]
    public async Task Numbers_are_never_described_as_strings_and_enums_are_names()
    {
        using var document = await FetchDocumentAsync();

        var mixed = new List<string>();
        FindNumbersAllowedAsStrings(document.RootElement, "$", mixed);
        Assert.Empty(mixed);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var receiptQuantity = schemas.GetProperty("ReceiptRequest").GetProperty("properties").GetProperty("quantity");
        Assert.Equal("decimal", receiptQuantity.GetProperty("format").GetString());
        var itemTypes = schemas.GetProperty("ItemType").GetProperty("enum").EnumerateArray().Select(value => value.GetString());
        Assert.Equal(["RawMaterial", "FinishedProduct"], itemTypes);
    }

    /// <summary>
    /// Decision #110, design 5 and 7.2: a request schema lists as "required" exactly the fields the API refuses to go
    /// without, and leaves out the optional ones: an item's <c>description</c>, a receipt's <c>note</c>, a return's
    /// <c>orderNumber</c>, a completion's <c>reason</c> (needed only when some leftover is above zero), a rejection's
    /// <c>note</c> and (v1.1) a sales order's <c>customerReference</c> and a shipment's <c>note</c>. The optional fields are
    /// still described as properties.
    /// </summary>
    [Theory]
    [InlineData("ItemRequest", "name,type,unitId", "description")]
    [InlineData("ReceiptRequest", "itemId,quantity,receiptDate", "note")]
    [InlineData("ReturnRequest", "itemId,quantity,reason", "orderNumber")]
    [InlineData("CompleteOrderRequest", "producedQuantity,lines", "reason")]
    [InlineData("RejectComplementaryOrderRequest", "", "note")]
    [InlineData("AdjustmentRequest", "itemId,location,quantity,reason", "")]
    [InlineData("SendOrderLineRequest", "itemId,quantity", "")]
    [InlineData("SignInRequest", "email,password", "")]
    [InlineData("CreateSalesOrderRequest", "customerId,lines", "customerReference")]
    [InlineData("SalesOrderLineRequest", "itemId,quantity", "")]
    [InlineData("ShipSalesOrderRequest", "", "note")]
    public async Task Request_schema_requires_exactly_the_fields_the_api_needs(string schemaName, string required, string optional)
    {
        using var document = await FetchDocumentAsync();
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);

        var listed = schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal).ToList()
            : [];
        Assert.Equal(Split(required).Order(StringComparer.Ordinal), listed);

        var properties = schema.GetProperty("properties").EnumerateObject().Select(property => property.Name);
        Assert.Equal(Split(required).Concat(Split(optional)).Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));

        static string[] Split(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Design 2.3: response schemas list every field as required, because every response field is always present.</summary>
    [Fact]
    public async Task Response_schemas_require_every_field()
    {
        using var document = await FetchDocumentAsync();
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var name in new[]
                 {
                     "MovementResponse", "ItemResponse", "ReturnResponse", "ProductionOrderResponse",
                     "SalesOrderResponse", "SalesOrderSummaryResponse",
                 })
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal(schema.GetProperty("properties").EnumerateObject().Count(), schema.GetProperty("required").GetArrayLength());
        }
    }

    /// <summary>
    /// Decisions #28, #33: the OpenAPI document is served in Development only; elsewhere the path is unknown (404). The
    /// caller is signed in, because without a token any unknown path answers 401 first (design 2.2).
    /// </summary>
    [Fact]
    public async Task Document_is_not_served_outside_development()
    {
        await using var testing = new TestApiFactory();
        using var client = testing.CreateClientWithToken(await testing.SignInAsync(TestApiFactory.AdminEmail, TestApiFactory.AdminPassword));

        var response = await client.GetAsync(DocumentPath, Cancel);

        await ApiAssert.ProblemAsync(response, HttpStatusCode.NotFound, "not-found", "No endpoint matches this path.");
    }

    // ---- The sample requests file -------------------------------------------------------------------------------------

    /// <summary>
    /// T-021: <c>src/InventoryTracker.Api/InventoryTracker.Api.http</c> has a sample request for every endpoint of design
    /// section 4. A request line counts when its method matches and its path matches the template, with each
    /// <c>{param}</c> filled by a <c>{{variable}}</c> or a number. (That the walk-through runs top to bottom on a fresh
    /// database is a manual check; see the T-021 report.)
    /// </summary>
    [Fact]
    public void Http_file_has_a_sample_request_for_every_endpoint()
    {
        var requests = File.ReadAllLines(HttpFilePath())
            .Select(line => Regex.Match(line, @"^(GET|POST|PUT|DELETE|PATCH) \{\{api\}\}(\S*)$"))
            .Where(match => match.Success)
            .Select(match => (Method: match.Groups[1].Value, Path: match.Groups[2].Value.Split('?')[0]))
            .ToList();

        var missing = RoleMatrixTests.Endpoints
            .Where(row =>
            {
                var pattern = "^" + Regex.Replace(Regex.Escape(row.Template), @"\\\{\w+}", @"(\{\{[^}]+\}\}|\d+)") + "$";
                return !requests.Any(request => request.Method == row.Method && Regex.IsMatch(request.Path, pattern));
            })
            .Select(row => row.Key)
            .ToList();

        Assert.Empty(missing);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private async Task<JsonDocument> FetchDocumentAsync()
    {
        using var client = app.CreateClient();
        using var response = await client.GetAsync(DocumentPath, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
    }

    /// <summary>
    /// Every operation of the document, keyed <c>METHOD /path</c> with the path relative to <c>/api/v1</c>, as
    /// <see cref="MatrixRow.Key"/> is. Paths outside <c>/api/v1</c> would keep their full path, and so fail the comparison.
    /// </summary>
    private static Dictionary<string, JsonElement> Operations(JsonDocument document)
    {
        var operations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            var template = path.Name.StartsWith(ScenarioApi.ApiPath + "/", StringComparison.Ordinal)
                ? path.Name[ScenarioApi.ApiPath.Length..]
                : path.Name;
            foreach (var operation in path.Value.EnumerateObject())
            {
                operations.Add($"{operation.Name.ToUpperInvariant()} {template}", operation.Value);
            }
        }

        return operations;
    }

    private static List<string> Responses(JsonElement operation) =>
        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name).ToList();

    /// <summary>Collects the JSON path of every schema whose <c>type</c> list mixes a number type with <c>string</c>.</summary>
    private static void FindNumbersAllowedAsStrings(JsonElement element, string path, List<string> found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
            {
                var types = type.EnumerateArray().Select(value => value.GetString()).ToList();
                if (types.Contains("string") && (types.Contains("integer") || types.Contains("number")))
                {
                    found.Add(path);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                FindNumbersAllowedAsStrings(property.Value, $"{path}.{property.Name}", found);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                FindNumbersAllowedAsStrings(item, $"{path}[{index++}]", found);
            }
        }
    }

    /// <summary>The .http file, found by walking up from the test output folder to the repository root.</summary>
    private static string HttpFilePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InventoryTracker.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "InventoryTracker.Api", "InventoryTracker.Api.http");
            }
        }

        throw new InvalidOperationException("Repository root (InventoryTracker.slnx) not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// The real app in the Development environment, where Program.cs maps <c>/openapi/v1.json</c> (decision #33), with the
    /// test database and secrets of <see cref="TestApiFactory"/>.
    /// </summary>
    /// <remarks>
    /// In Development, ASP.NET Core also loads the developer's <c>dotnet user-secrets</c> (a <c>secrets.json</c> file
    /// outside the repository). The test settings would win anyway, but that source is removed so that nothing from the
    /// owner's machine reaches a test run, as in the Testing environment the other tests use.
    /// </remarks>
    public sealed class DevelopmentApp : TestApiFactory
    {
        /// <summary>Applies the test settings, then switches to Development without user-secrets.</summary>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(configuration =>
            {
                foreach (var secrets in configuration.Sources.OfType<JsonConfigurationSource>().Where(source => source.Path == "secrets.json").ToList())
                {
                    configuration.Sources.Remove(secrets);
                }
            });
        }
    }
}
