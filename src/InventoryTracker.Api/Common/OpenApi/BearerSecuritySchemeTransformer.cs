using InventoryTracker.Api.Common.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace InventoryTracker.Api.Common.OpenApi;

/// <summary>
/// Adjusts the whole OpenAPI document once: names the API, declares the <c>Bearer</c> security scheme (US-01: clients
/// send the sign-in token as <c>Authorization: Bearer &lt;token&gt;</c>), and defines the error body of design section 6
/// as the shared <c>ProblemDetails</c> schema.
/// </summary>
/// <remarks>
/// A <em>document transformer</em> runs after every operation of the document has been generated, so what it puts in
/// <c>components</c> (the document's shared definitions) wins over anything generated earlier. The operations point at
/// these definitions by name (<see cref="ApiResponsesTransformer"/>).
/// </remarks>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <summary>Name of the security scheme in <c>components.securitySchemes</c>; operations refer to it by this name.</summary>
    public const string SchemeName = "Bearer";

    /// <summary>
    /// Name of the error body schema in <c>components.schemas</c>. It is the name the generator itself gives the
    /// framework's <c>ProblemDetails</c> class, so this definition replaces that one instead of sitting beside it.
    /// </summary>
    public const string ProblemSchemaName = "ProblemDetails";

    /// <summary>Adds the title, the security scheme and the error body schema to <paramref name="document"/>.</summary>
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "Production & Inventory Tracker API";
        document.Info.Description =
            "Raw-material stock, production orders, complementary orders, leftover returns and notifications for one " +
            "manufacturing company (docs/01-prd.md). Sign in with POST /api/v1/auth/sign-in, then send the returned " +
            "accessToken on every other request as 'Authorization: Bearer <token>'. Tokens last 8 hours by default " +
            "(setting Jwt__TokenLifetimeHours); after expiresAt, sign in again. Every error is a ProblemDetails body " +
            "(application/problem+json) with a stable 'code'. Any request that changes data can also answer 409 " +
            "'concurrent-change' when a simultaneous request changed the data first and no more specific message " +
            "applies; read the data again and retry (design 7.2, PRD section 5).";

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>();

        // US-01, AC-01.1: sign-in returns a JWT (a signed token, architecture 6 "Tokens"). "http" + "bearer" is the
        // OpenAPI way of saying "send it in the Authorization header with the word Bearer in front".
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The accessToken returned by POST /api/v1/auth/sign-in, valid for 8 hours by default " +
                "(setting Jwt__TokenLifetimeHours; AC-01.1, AC-01.2).",
        };

        document.Components.Schemas[ProblemSchemaName] = ProblemSchema();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The error body every endpoint writes (design section 6, built by <see cref="ProblemResponse"/>): the RFC 9457
    /// fields plus this API's <c>code</c> and <c>traceId</c>, and <c>errors</c> when particular fields are to blame.
    /// </summary>
    private static OpenApiSchema ProblemSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Design 6: every error is an RFC 9457 ProblemDetails body. 'detail' is the complete readable " +
            "message; 'code' is the stable identifier for programs.",
        Required = new HashSet<string> { "title", "status", "detail", ProblemResponse.CodeField, ProblemResponse.TraceIdField },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["type"] = Text("The framework's default link for the status. Clients should use 'status' and 'code' instead."),
            ["title"] = Text("Fixed per status: 400 'Invalid request', 401 'Not signed in', 403 'Not allowed', " +
                "404 'Not found', 409 'Conflict with current state'."),
            ["status"] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Description = "The HTTP status code." },
            ["detail"] = Text("The complete message. When several messages apply they are joined with a space."),
            [ProblemResponse.CodeField] = Text("Stable kebab-case identifier, for example 'validation', 'not-found' or " +
                "'insufficient-stock'."),
            [ProblemResponse.ErrorsField] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Only on some 400 responses: the messages for each field, keyed by its camelCase JSON path " +
                    "(for example 'lines[0].quantity').",
                AdditionalProperties = new OpenApiSchema
                {
                    Type = JsonSchemaType.Array,
                    Items = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
            [ProblemResponse.TraceIdField] = Text("The request's trace id, which also appears in the server log."),
        },
    };

    private static OpenApiSchema Text(string description) => new() { Type = JsonSchemaType.String, Description = description };
}
