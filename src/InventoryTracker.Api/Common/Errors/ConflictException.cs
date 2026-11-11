namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// <c>409</c>: the request is well-formed but conflicts with the current state: an order's status,
/// available stock, another order in production, a duplicate name (design 2.2 steps 5 and 7).
/// </summary>
/// <example><c>throw new ConflictException("already-sent", "Materials have already been sent for this order. ...");</c></example>
public sealed class ConflictException : ApiException
{
    /// <summary>Creates the exception with the design 7.2 code and message.</summary>
    public ConflictException(string code, string detail)
        : base(StatusCodes.Status409Conflict, code, detail)
    {
    }
}
