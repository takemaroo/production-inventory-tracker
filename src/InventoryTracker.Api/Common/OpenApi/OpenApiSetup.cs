using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace InventoryTracker.Api.Common.OpenApi;

/// <summary>
/// Registers the OpenAPI description of the API (PRD section 5: "publishes an OpenAPI description listing every
/// endpoint"), served at <c>/openapi/v1.json</c> in Development only (decisions #28, #33). Called once from Program.cs.
/// </summary>
/// <remarks>
/// <para>
/// <b>What OpenAPI is.</b> An OpenAPI document is a JSON file that describes every endpoint of an HTTP API: its path and
/// method, its parameters, the shape of its request and response bodies, its status codes and how to authenticate. Tools
/// read it to generate client code or to show the API in a browser. ASP.NET Core's built-in generator
/// (<c>Microsoft.AspNetCore.OpenApi</c>, architecture section 1) builds it from the controllers. It uses the routes, the
/// parameter and return types, the <c>[ProducesResponseType]</c> attributes, and the <c>///</c> comments, which it uses
/// as summaries and descriptions because <c>GenerateDocumentationFile</c> is on in the project file.
/// </para>
/// <para>
/// <b>Transformers.</b> The generator lets the app adjust its output with <em>transformers</em>, classes that receive the
/// generated document (or one operation or schema of it) and change it. This API uses three:
/// <see cref="ApiResponsesTransformer"/> adds the error responses and the security requirement to each operation,
/// <see cref="BearerSecuritySchemeTransformer"/> declares the bearer token scheme and the error body once for the whole
/// document, and <see cref="DecimalSchemaTransformer"/> marks quantities as exact decimals. Which request fields are
/// required comes from the request records themselves: an optional field has a default (<c>string? Note = null</c>),
/// and the generator lists only the others as "required" (decision #110).
/// </para>
/// </remarks>
public static class OpenApiSetup
{
    /// <summary>
    /// Adds the OpenAPI generator with this API's transformers, and makes the generator describe JSON exactly as the
    /// controllers read and write it.
    /// </summary>
    public static IServiceCollection AddApiOpenApi(this IServiceCollection services)
    {
        // The generator describes types with the JSON settings of minimal APIs (Http.Json.JsonOptions), not with the
        // controllers' settings (Mvc.JsonOptions, set in Program.cs). Left alone it would describe enums as integers,
        // although they travel as names (design 2.3), and every number as "integer or string", although design 2.4
        // refuses numbers sent as strings. So the controllers' two rules that decide the shape of a value, the number
        // handling and the converters (string enums), are copied over. Only the description changes: this API has no
        // minimal-API endpoint that reads JSON, and both rules give the same output when writing an error body, the
        // one thing written with these settings.
        services.AddOptions<HttpJsonOptions>()
            .Configure<IOptions<MvcJsonOptions>>((http, mvc) =>
            {
                var controllers = mvc.Value.JsonSerializerOptions;
                http.SerializerOptions.NumberHandling = controllers.NumberHandling;
                foreach (var converter in controllers.Converters)
                {
                    http.SerializerOptions.Converters.Add(converter);
                }
            });

        // "v1" is the document name, so the document is served at /openapi/v1.json (app.MapOpenApi() in Program.cs).
        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer<DecimalSchemaTransformer>();
            options.AddOperationTransformer<ApiResponsesTransformer>();
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });

        return services;
    }
}
