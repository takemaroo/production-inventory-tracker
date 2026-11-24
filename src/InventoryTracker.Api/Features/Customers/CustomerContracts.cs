using InventoryTracker.Api.Common.Contracts;

namespace InventoryTracker.Api.Features.Customers;

/// <summary>
/// (v1.1) Body of <c>POST /api/v1/customers</c> and <c>PUT /api/v1/customers/{id}</c> (design 5.11, architecture 4.1
/// item 15). <c>PUT</c> is a full replacement, as for items: leaving <c>notes</c> out, or sending <c>null</c>, clears
/// the stored notes.
/// </summary>
/// <param name="Name">Required, trimmed, 1 to 100 characters, unique ignoring letter case (AC-29.1, AC-29.2).</param>
/// <param name="Notes">
/// Optional free text, for example an address and phone, trimmed, up to 500 characters. Empty or blank is stored as no
/// notes (design 2.6). Never written to the log (decision #163).
/// </param>
/// <remarks>
/// Both fields are nullable so that a missing name reaches <see cref="CustomerService"/>, which answers with the
/// design's own "Name is required." rather than a framework message (architecture 6, Validation). <c>Notes</c> is the
/// optional field, so it comes last with <c>= null</c>; that changes nothing when JSON is read, but makes the OpenAPI
/// description show it as not required (decision #110).
/// </remarks>
public sealed record CustomerRequest(string? Name, string? Notes = null);

/// <summary>(v1.1) A customer as the API returns it (design 5.11, AC-29.6).</summary>
/// <param name="Id">The customer's id. Once given out it is never given to another customer (decision #162).</param>
/// <param name="Name">The current name as stored: trimmed, in the letter case it was entered (AC-29.3).</param>
/// <param name="Notes">The notes, or null when there are none.</param>
/// <param name="IsActive">False after a deactivate (AC-29.4). A deactivated customer is still listed and readable.</param>
/// <param name="CreatedAt">When the customer was created, UTC, whole seconds (AC-29.1, AC-05.3). Lists are newest first by it.</param>
/// <param name="CreatedBy">Who created the customer (AC-05.3), as every response shows a person (design 2.3).</param>
public sealed record CustomerResponse(
    int Id,
    string Name,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    PersonResponse CreatedBy);
