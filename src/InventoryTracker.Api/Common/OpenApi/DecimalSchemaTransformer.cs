using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace InventoryTracker.Api.Common.OpenApi;

/// <summary>
/// Describes every C# <see cref="decimal"/> (quantities, amounts per unit, percentages) as a JSON number with format
/// <c>decimal</c>, instead of the generator's default <c>double</c>.
/// </summary>
/// <remarks>
/// Design 2.4: quantities are exact decimals with at most 3 decimal places (6 for amounts per unit), read without going
/// through floating point. Format <c>double</c> would tell a client's code generator to use binary floating point,
/// which cannot hold a value such as 0.1 exactly. Both are JSON numbers, so nothing on the wire changes.
/// A <em>schema transformer</em> runs once for every type and property schema the generator builds;
/// <c>context.JsonTypeInfo.Type</c> is the C# type the schema describes.
/// </remarks>
public sealed class DecimalSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <summary>Sets the format of a <see cref="decimal"/> or <c>decimal?</c> schema to <c>decimal</c>.</summary>
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(decimal) || type == typeof(decimal?))
        {
            schema.Format = "decimal";
        }

        return Task.CompletedTask;
    }
}
