using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// Builds the <c>400</c> response for a request MVC could not even read: malformed JSON, a value of the
/// wrong JSON type, a missing body, or a query value that is not a number (architecture 4.1 item 2).
/// </summary>
/// <remarks>
/// Model binding is the MVC step that turns the HTTP request (JSON body, query string, route values) into the
/// C# parameters of an action. Problems it finds are stored in <c>ModelState</c>. Because the controllers are
/// marked <c>[ApiController]</c>, MVC refuses to run an action whose <c>ModelState</c> is invalid and calls
/// <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> instead; <see cref="ErrorHandlingSetup"/> points
/// that at <see cref="Create"/>.
/// <para>
/// Request records carry no validation attributes (architecture 6, Validation), so this only ever sees
/// "could not read" problems. Every business check runs later, in the service, in the design's order.
/// </para>
/// </remarks>
public static class InvalidRequestResponse
{
    /// <summary>Architecture 4.1 item 2: the body is not valid JSON.</summary>
    public const string NotJsonMessage = "The request body is not valid JSON.";

    /// <summary>Architecture 4.1 item 2: the action needs a body and none was sent.</summary>
    public const string MissingBodyMessage = "A request body is required.";

    /// <summary>The whole body is valid JSON but the wrong kind of value (for example an array instead of an object).</summary>
    public const string WrongBodyTypeMessage = "The request body has the wrong type.";

    /// <summary>Turns the invalid <c>ModelState</c> of <paramref name="context"/> into a design 6 <c>400</c> body.</summary>
    public static IActionResult Create(ActionContext context)
    {
        var entries = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error => (Key: entry.Key, Error: error)))
            .ToList();

        // The JSON reader stops at the first problem, so there is at most one JsonException.
        // (ErrorHandlingSetup turns off AllowInputFormatterExceptionMessages, so the exception object is kept in
        // ModelState instead of only its text, which may name internal .NET types.)
        var json = entries.FirstOrDefault(entry => entry.Error.Exception is JsonException);
        if (json.Error is not null)
        {
            return ProblemResponse.AsActionResult(FromJsonException(json.Key, (JsonException)json.Error.Exception!));
        }

        if (entries.Any(entry => entry.Error.ErrorMessage == MissingBodyMessage))
        {
            return ProblemResponse.AsActionResult(Problem(MissingBodyMessage));
        }

        // Anything else is a value MVC could not convert, for example ?page=abc. MVC's own message is used.
        var errors = new Dictionary<string, List<string>>();
        foreach (var (key, error) in entries)
        {
            var message = string.IsNullOrEmpty(error.ErrorMessage) ? "The request is not valid." : error.ErrorMessage;
            var path = ToCamelCase(key);
            if (!errors.TryGetValue(path, out var list))
            {
                errors[path] = list = [];
            }

            list.Add(message);
        }

        var detail = string.Join(" ", errors.Values.SelectMany(list => list));
        return ProblemResponse.AsActionResult(ProblemResponse.Create(
            StatusCodes.Status400BadRequest, ErrorCodes.Validation, detail,
            errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
    }

    private static ProblemDetails FromJsonException(string modelStateKey, JsonException exception)
    {
        // A syntax error (bad character, missing quote, empty body) comes from the low-level JSON reader;
        // the serializer re-throws it as the InnerException of a JsonException that carries the path.
        // A type error ("12.5" for a number, 5 for a list) has no reader exception inside.
        if (exception.InnerException is JsonException)
        {
            return ProblemResponse.Create(StatusCodes.Status400BadRequest, ErrorCodes.Validation, NotJsonMessage);
        }

        // Design 2.4: a quantity sent as a string must be rejected; name the field so the client can fix it.
        // Property names in a body are matched ignoring case, so the serializer's path repeats the client's spelling
        // ({"Quantity": "abc"} gives $.Quantity). Design 6 keys errors by camelCase path, as for query values.
        var path = ToCamelCase(ToDesignPath(exception.Path ?? modelStateKey));
        if (path.Length == 0)
        {
            return ProblemResponse.Create(StatusCodes.Status400BadRequest, ErrorCodes.Validation, WrongBodyTypeMessage);
        }

        var message = $"{path} has the wrong type.";
        return ProblemResponse.Create(StatusCodes.Status400BadRequest, ErrorCodes.Validation, message,
            new Dictionary<string, string[]> { [path] = [message] });
    }

    private static ProblemDetails Problem(string detail) =>
        ProblemResponse.Create(StatusCodes.Status400BadRequest, ErrorCodes.Validation, detail);

    /// <summary>
    /// The JSON serializer writes paths as <c>$.lines[0].quantity</c>; design 6 keys <c>errors</c> by
    /// <c>lines[0].quantity</c>. The root itself (<c>$</c>) becomes an empty string.
    /// </summary>
    private static string ToDesignPath(string path)
    {
        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path.StartsWith('$') ? path[1..] : path;
    }

    /// <summary>
    /// MVC names query and form values after the C# property (<c>ItemId</c>, <c>Lines[0].Quantity</c>); design 6
    /// keys <c>errors</c> by camelCase JSON path (<c>itemId</c>, <c>lines[0].quantity</c>). Body paths need the same
    /// treatment, because they keep the client's own spelling. Lower-cases the first letter of every dot-separated
    /// segment.
    /// </summary>
    private static string ToCamelCase(string key) =>
        string.Join('.', key.Split('.').Select(segment =>
            segment.Length == 0 ? segment : char.ToLowerInvariant(segment[0]) + segment[1..]));
}
