namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// <c>404</c> <c>not-found</c>: the record named in the URL does not exist (design 2.2 step 3).
/// </summary>
/// <example><c>throw new NotFoundException($"Order #{number} not found.");</c></example>
public sealed class NotFoundException : ApiException
{
    /// <summary>Creates the exception with the design 7.2 message, for example "Unit 4 not found.".</summary>
    public NotFoundException(string detail)
        : base(StatusCodes.Status404NotFound, ErrorCodes.NotFound, detail)
    {
    }
}
