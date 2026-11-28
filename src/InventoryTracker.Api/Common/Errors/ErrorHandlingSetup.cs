using InventoryTracker.Api.Common.Json;
using Microsoft.AspNetCore.Mvc;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// Registers everything that makes errors look the same on every endpoint (design section 6).
/// Called once from Program.cs; the middleware side is <c>app.UseExceptionHandler()</c> and
/// <c>app.UseStatusCodePages()</c>, also in Program.cs.
/// </summary>
public static class ErrorHandlingSetup
{
    /// <summary>
    /// Adds ProblemDetails with this API's conventions, the central exception handler, and the response for
    /// request bodies that cannot be read.
    /// </summary>
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        // IProblemDetailsService: the framework's writer for RFC 9457 error bodies. The exception handler,
        // UseStatusCodePages (bodiless 404/405) and MVC all write through it, and the Customize hook runs on
        // every body just before it is written.
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => ProblemResponse.ApplyConventions(context.ProblemDetails, context.HttpContext));

        // Registers ApiExceptionHandler as the handler that app.UseExceptionHandler() calls.
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.Configure<ApiBehaviorOptions>(options =>
            // [ApiController]'s automatic 400 stays on for unreadable bodies only (architecture 6, Validation).
            options.InvalidModelStateResponseFactory = InvalidRequestResponse.Create);

        services.Configure<MvcOptions>(options =>
        {
            // Without this, MVC treats every non-nullable reference property of a request record (string Name)
            // as [Required] and answers "The Name field is required." before the service runs. That would skip
            // the design's check order (design 2.2: 404 before 400) and its wording ("Name is required."), so
            // all field checks are left to the services.
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;

            // Architecture 4.1 item 2: the wording for a POST/PUT sent with no body.
            options.ModelBindingMessageProvider.SetMissingRequestBodyRequiredValueAccessor(
                () => InvalidRequestResponse.MissingBodyMessage);
        });

        services.Configure<JsonOptions>(options =>
            // Keep the JSON exception object in ModelState rather than only its text, so InvalidRequestResponse
            // can tell a syntax error from a wrong type. It also keeps .NET type names out of responses.
            options.AllowInputFormatterExceptionMessages = false);

        // (v1.2) AC-37.9, architecture 14 (A-5): the same escaping as the controllers' answers (Program.cs), for the
        // error bodies. When no controller is writing the answer (the exception handler, which writes every
        // ApiException such as a 409, and unknown routes, for example), IProblemDetailsService writes ProblemDetails
        // with the minimal-API JSON settings (Http.Json.JsonOptions), which are separate from the controllers' settings
        // (Mvc.JsonOptions). Setting the encoder only in Program.cs would leave a detail such as
        // "A unit named 'kg' already exists." with its raw quotes (JsonEscapingTests shows it). See JsonEscaping for
        // what changes in the raw bytes and why.
        services.Configure<HttpJsonOptions>(options => options.SerializerOptions.Encoder = JsonEscaping.Encoder);

        return services;
    }
}
