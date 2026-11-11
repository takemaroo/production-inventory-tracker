using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A test-only controller whose actions throw each kind of error, so <c>ErrorHandlingTests</c> can check the
/// response shapes of design section 6 through the real pipeline before any feature exists.
/// </summary>
/// <remarks>
/// It lives in the test project and is added to the app only by <see cref="ProbeApiFactory"/>, as an
/// "application part" (an extra assembly MVC searches for controllers). It never ships and never appears in
/// the real API or its OpenAPI document.
/// <para>
/// <c>[AllowAnonymous]</c>: from T-003 on, every endpoint requires a token. These probes test error shapes, not
/// sign-in, so they opt out to keep the tests independent of authentication.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("test-probe")]
public sealed class ErrorProbeController : ControllerBase
{
    /// <summary>Message used by the unhandled-exception probe; it must never reach a response body.</summary>
    public const string SecretMessage = "INTERNAL-DETAIL-MARKER-7f3a: this text must never reach a client";

    /// <summary>Throws a <see cref="NotFoundException"/>.</summary>
    [HttpGet("not-found")]
    public IActionResult ThrowNotFound() => throw new NotFoundException("Order #99 not found.");

    /// <summary>Throws a <see cref="ConflictException"/> with a PRD message.</summary>
    [HttpGet("conflict")]
    public IActionResult ThrowConflict() => throw new ConflictException(
        "another-order-in-production", "Order #11 is in production. Only one order can be in production at a time.");

    /// <summary>Throws a <see cref="BadRequestException"/> with a PRD message.</summary>
    [HttpGet("bad-request")]
    public IActionResult ThrowBadRequest() =>
        throw new BadRequestException("receipt-not-raw-material", "Receipts are for raw materials only.");

    /// <summary>Collects two field errors with <see cref="ValidationErrors"/> and throws them.</summary>
    [HttpGet("validation")]
    public IActionResult ThrowValidation()
    {
        var errors = new ValidationErrors();
        errors.Add("quantity", "Quantity must be greater than 0.");
        errors.Add("lines[1].itemId", "Item 42 does not exist.");
        errors.ThrowIfAny();
        return Ok();
    }

    /// <summary>Throws an exception nobody expects.</summary>
    [HttpGet("unhandled")]
    public IActionResult ThrowUnhandled() => throw new InvalidOperationException(SecretMessage);

    /// <summary>
    /// Throws what EF Core throws when SQLite refuses a write. <paramref name="kind"/> picks the SQLite error:
    /// <c>unique</c>, <c>primary-key</c>, <c>not-null</c>, <c>foreign-key</c>, <c>busy</c>, or <c>none</c> for a
    /// DbUpdateException with no database error inside.
    /// </summary>
    [HttpGet("database/{kind}")]
    public IActionResult ThrowDatabaseError(string kind)
    {
        // SQLite primary code 19 = constraint; the extended code says which constraint. 5 = database busy.
        SqliteException? inner = kind switch
        {
            "unique" => new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: ProductionOrders.Status'.", 19, 2067),
            "primary-key" => new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: Units.Id'.", 19, 1555),
            "not-null" => new SqliteException("SQLite Error 19: 'NOT NULL constraint failed: Units.Name'.", 19, 1299),
            "foreign-key" => new SqliteException("SQLite Error 19: 'FOREIGN KEY constraint failed'.", 19, 787),
            "busy" => new SqliteException("SQLite Error 5: 'database is locked'.", 5, 5),
            _ => null,
        };

        throw new DbUpdateException("An error occurred while saving the entity changes.", inner);
    }

    /// <summary>Binds an optional number from the query string, so tests can send one that is not a number.</summary>
    [HttpGet("filter")]
    public IActionResult Filter([FromQuery] ProbeFilter filter) => Ok(filter);

    /// <summary>Echoes a JSON body, so tests can send unreadable ones.</summary>
    [HttpPost("body")]
    public IActionResult Body(ProbeBody body) => Ok(body);

    /// <summary>Validates the paging parameters and returns an empty page.</summary>
    [HttpGet("paging")]
    public PagedResult<int> Paging([FromQuery] PageQuery paging)
    {
        paging.ThrowIfInvalid();
        return new PagedResult<int>([], paging.Page, paging.PageSize, 0);
    }
}

/// <summary>Body of the probe's POST action: a number, a text and a nested list, to test each error path.</summary>
/// <param name="Quantity">A JSON number; a string here must be refused.</param>
/// <param name="Name">Any text.</param>
/// <param name="Lines">A nested list, to check paths such as <c>lines[0].quantity</c>.</param>
public sealed record ProbeBody(decimal Quantity, string Name, IReadOnlyList<ProbeLine>? Lines);

/// <summary>Query parameters of the probe's filter action. No [FromQuery(Name)], so MVC keys errors "ItemId".</summary>
public sealed class ProbeFilter
{
    /// <summary>An optional id filter, like <c>?itemId=3</c> on a list endpoint.</summary>
    public int? ItemId { get; set; }

    /// <summary>A nested object, so MVC produces a dotted key such as <c>Range.From</c>.</summary>
    public ProbeRange? Range { get; set; }
}

/// <summary>Nested query values of <see cref="ProbeFilter"/>.</summary>
public sealed class ProbeRange
{
    /// <summary>An optional number.</summary>
    public int? From { get; set; }
}

/// <summary>One nested line of <see cref="ProbeBody"/>.</summary>
/// <param name="Quantity">A JSON number.</param>
public sealed record ProbeLine(decimal Quantity);
