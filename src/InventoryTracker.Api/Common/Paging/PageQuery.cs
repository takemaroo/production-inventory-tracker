using InventoryTracker.Api.Common.Validation;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Common.Paging;

/// <summary>
/// The <c>page</c> and <c>pageSize</c> query parameters every list endpoint accepts (design 2.5, PRD 4.1).
/// </summary>
/// <remarks>
/// A list action takes it as a parameter marked <c>[FromQuery]</c>, which tells model binding to fill the
/// properties from the query string (<c>?page=2&amp;pageSize=20</c>); without the attribute
/// <c>[ApiController]</c> would look for it in the body. Missing parameters keep the defaults below.
/// The limits are checked by <see cref="Validate"/>, called by the service, so the response carries the
/// design 7.2 messages rather than a framework one.
/// </remarks>
public sealed class PageQuery
{
    /// <summary>PRD 4.1: a page holds 50 records unless the client asks otherwise.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>PRD 4.1: a page never holds more than 100 records.</summary>
    public const int MaxPageSize = 100;

    /// <summary>1-based page number. Default 1.</summary>
    [FromQuery(Name = "page")]
    public int Page { get; set; } = 1;

    /// <summary>Records per page, 1 to 100. Default 50.</summary>
    [FromQuery(Name = "pageSize")]
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// How many records come before this page. Computed in 64 bits and capped, so an absurd page number gives
    /// an empty page (design 2.5: a page past the end is <c>200</c> with no items) instead of an overflow.
    /// </summary>
    public int Skip => (int)Math.Min((long)(Page - 1) * PageSize, int.MaxValue);

    /// <summary>
    /// Adds the design 7.2 messages to <paramref name="errors"/> when <see cref="Page"/> is below 1 or
    /// <see cref="PageSize"/> is outside 1 to 100. Values are rejected, never silently clamped (design 9).
    /// </summary>
    public void Validate(ValidationErrors errors)
    {
        if (Page < 1)
        {
            errors.Add("page", "page must be 1 or greater.");
        }

        if (PageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }
    }

    /// <summary>
    /// Shortcut for lists with no other query parameters: validates and throws a <c>400</c> when invalid.
    /// Lists with filters should call <see cref="Validate"/> with the same collector as the filters, so every
    /// problem is reported in one response.
    /// </summary>
    public void ThrowIfInvalid()
    {
        var errors = new ValidationErrors();
        Validate(errors);
        errors.ThrowIfAny();
    }
}
