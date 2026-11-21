using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;

namespace InventoryTracker.Api.Common.OpenApi;

/// <summary>
/// Completes each operation of the OpenAPI document with what the whole API does the same way: the bearer token
/// requirement, and the <c>400</c>, <c>401</c>, <c>403</c> and <c>404</c> responses that follow from an endpoint's
/// authorization, parameters and body. Every error response is described with the design 6 error body.
/// </summary>
/// <remarks>
/// <para>
/// An <em>operation transformer</em> runs once for each endpoint (each method and path pair) while the document is
/// generated. <c>context.Description</c> is ASP.NET Core's own description of the endpoint (its "API explorer" entry):
/// parameters with where they come from, and the endpoint's metadata, which holds attributes such as
/// <c>[Authorize]</c> and <c>[AllowAnonymous]</c>.
/// </para>
/// <para>
/// The rules below follow from the framework, so they hold for every endpoint and are written once here rather than as
/// an attribute on each action:
/// </para>
/// <list type="bullet">
/// <item><c>401</c> and the security requirement unless <c>[AllowAnonymous]</c>: the fallback policy refuses a request
/// without a valid token (AC-01.5).</item>
/// <item><c>403</c> when <c>[Authorize(Roles = ...)]</c> names roles (AC-05.1).</item>
/// <item><c>400</c> when the endpoint reads a body (a body that is not valid JSON is refused, architecture 4.1 item 2) or a
/// query string (paging and filters are checked, design 2.5).</item>
/// <item><c>404</c> when the path names a record (<c>{id}</c>, <c>{number}</c>, <c>{itemId}</c>; design 2.2 step 3).</item>
/// </list>
/// <para>
/// What only an action knows, its success status and whether it can answer <c>409</c>, is declared on the action with
/// <c>[ProducesResponseType]</c>, next to the code that returns it.
/// </para>
/// </remarks>
public sealed class ApiResponsesTransformer : IOpenApiOperationTransformer
{
    /// <summary>The media type of every error body (design 6).</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>Adds the security requirement and the shared error responses to <paramref name="operation"/>.</summary>
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var description = context.Description;
        var metadata = description.ActionDescriptor.EndpointMetadata;
        var responses = operation.Responses ??= new OpenApiResponses();

        // AC-01.5: everything except sign-in needs "Authorization: Bearer <token>". Saying so per operation (rather than
        // once for the document) lets sign-in be the one operation without the requirement.
        if (!metadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(BearerSecuritySchemeTransformer.SchemeName, context.Document)] = [],
                },
            ];
            AddIfMissing(responses, StatusCodes.Status401Unauthorized,
                "Not signed in (`not-authenticated`): the token is missing, invalid or expired, or its user was deactivated " +
                "or changed their password (design 7.1).");
        }

        // AC-05.1, AC-05.2: a comma in Roles means "any of these".
        var roles = metadata.OfType<IAuthorizeData>().Select(data => data.Roles).OfType<string>().ToList();
        if (roles.Count > 0)
        {
            var allowed = string.Join(" or ", roles.SelectMany(list => list.Split(',', StringSplitOptions.TrimEntries)).Distinct());
            AddIfMissing(responses, StatusCodes.Status403Forbidden,
                $"Signed in, but none of the caller's roles allows this (`forbidden`). Allowed: {allowed} (PRD 4.2).");
        }

        var sources = description.ParameterDescriptions.Select(parameter => parameter.Source).ToList();
        if (sources.Contains(BindingSource.Body) || sources.Contains(BindingSource.Query))
        {
            AddIfMissing(responses, StatusCodes.Status400BadRequest,
                "Invalid request: the body is not valid JSON, a value has the wrong type, or a field or business rule " +
                "check failed. `code` names the rule (`validation` for plain field checks); `errors` names the fields (design 6, 7.2).");
        }

        if (sources.Contains(BindingSource.Path))
        {
            AddIfMissing(responses, StatusCodes.Status404NotFound,
                "The record named in the URL does not exist (`not-found`, design 2.2 step 3).");
        }

        RemoveUnboundParameters(operation, description);
        KeepJsonOnlyForSuccess(responses);
        DescribeErrorBodies(responses, context.Document);
        SortByStatus(responses);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Leaves out query parameters that model binding never fills, such as <c>PageQuery.Skip</c>, which is computed
    /// from <c>page</c> and <c>pageSize</c> and marked <c>[BindNever]</c>. The API explorer lists every public property
    /// of a <c>[FromQuery]</c> class, so without this the document advertised a <c>Skip</c> parameter on every list.
    /// </summary>
    private static void RemoveUnboundParameters(OpenApiOperation operation, ApiDescription description)
    {
        var unbound = description.ParameterDescriptions
            .Where(parameter => parameter.Source == BindingSource.Query && parameter.ModelMetadata is { IsBindingAllowed: false })
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (unbound.Count == 0 || operation.Parameters is null)
        {
            return;
        }

        foreach (var parameter in operation.Parameters.Where(parameter => parameter.Name is not null && unbound.Contains(parameter.Name)).ToList())
        {
            operation.Parameters.Remove(parameter);
        }
    }

    /// <summary>
    /// Success bodies are JSON (design 2.3), and <c>application/json</c> is the documented form: what every client gets
    /// unless it asks otherwise. The generator lists every media type a registered output formatter could produce:
    /// <c>text/plain</c>, which never applies here because that formatter writes only plain strings, and
    /// <c>text/json</c>, the same JSON served only to a client that sends <c>Accept: text/json</c>. Listing one form
    /// keeps the description simple without hiding anything a client needs.
    /// </summary>
    private static void KeepJsonOnlyForSuccess(OpenApiResponses responses)
    {
        foreach (var (key, response) in responses)
        {
            if (key.StartsWith('2') && response.Content is { Count: > 0 } content && content.ContainsKey("application/json"))
            {
                foreach (var mediaType in content.Keys.Where(mediaType => mediaType != "application/json").ToList())
                {
                    content.Remove(mediaType);
                }
            }
        }
    }

    /// <summary>
    /// Adds a response for <paramref name="status"/> unless the action already declares one with
    /// <c>[ProducesResponseType]</c> (sign-in declares its own <c>401</c>, for example).
    /// </summary>
    private static void AddIfMissing(OpenApiResponses responses, int status, string description)
    {
        responses.TryAdd(status.ToString(), new OpenApiResponse { Description = description });
    }

    /// <summary>
    /// Design 6: every 4xx response carries the ProblemDetails body as <c>application/problem+json</c>. The generator
    /// would otherwise list the framework's ProblemDetails under several JSON media types, or no body at all.
    /// A response without its own description (the generator then uses the bare reason phrase, "Conflict") gets one.
    /// </summary>
    private static void DescribeErrorBodies(OpenApiResponses responses, OpenApiDocument? document)
    {
        foreach (var (key, response) in responses.ToList())
        {
            if (!int.TryParse(key, out var status) || status is < 400 or > 499)
            {
                continue;
            }

            var text = response.Description;
            if (string.IsNullOrEmpty(text) || text == ReasonPhrases.GetReasonPhrase(status))
            {
                text = status == StatusCodes.Status409Conflict
                    ? "Conflicts with the current state (design 2.2): `code` names the conflict."
                    : ReasonPhrases.GetReasonPhrase(status);
            }

            responses[key] = new OpenApiResponse
            {
                Description = text,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    [ProblemContentType] = new()
                    {
                        Schema = new OpenApiSchemaReference(BearerSecuritySchemeTransformer.ProblemSchemaName, document),
                    },
                },
            };
        }
    }

    /// <summary>Lists responses in status order (200 before 400 before 401), which is how people read them.</summary>
    private static void SortByStatus(OpenApiResponses responses)
    {
        var sorted = responses.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        responses.Clear();
        foreach (var (key, response) in sorted)
        {
            responses.Add(key, response);
        }
    }
}
