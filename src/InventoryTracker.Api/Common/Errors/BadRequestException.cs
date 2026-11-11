namespace InventoryTracker.Api.Common.Errors;

/// <summary>
/// <c>400</c> with a specific <c>code</c> and one message, for business rules the PRD treats as invalid
/// input rather than a conflict, for example <c>receipt-not-raw-material</c> or <c>reason-required</c>.
/// </summary>
/// <remarks>
/// Use <see cref="ValidationException"/> instead when the problem belongs to one or more fields and the
/// response should name them in <c>errors</c>.
/// </remarks>
/// <example><c>throw new BadRequestException("receipt-not-raw-material", "Receipts are for raw materials only.");</c></example>
public sealed class BadRequestException : ApiException
{
    /// <summary>Creates the exception with the design 7.2 code and message.</summary>
    public BadRequestException(string code, string detail)
        : base(StatusCodes.Status400BadRequest, code, detail)
    {
    }
}
