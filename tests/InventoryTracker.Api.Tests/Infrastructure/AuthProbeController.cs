using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// A test-only controller with one action per kind of access rule, so <c>AuthTests</c> can check sign-in, token and
/// role handling (AC-01.5, AC-05.1, AC-05.2) before any real feature endpoint exists.
/// </summary>
/// <remarks>
/// Added to the app only by <see cref="ProbeApiFactory"/>; it never ships. Unlike <see cref="ErrorProbeController"/>
/// it has no <c>[AllowAnonymous]</c>, so the real rules apply.
/// </remarks>
[ApiController]
[Route("test-probe/auth")]
public sealed class AuthProbeController : ControllerBase
{
    /// <summary>No attribute: protected only by the fallback "signed-in" policy, like every "All" endpoint (design 4).</summary>
    [HttpGet("signed-in")]
    public ProbeIdentity SignedIn() => Describe();

    /// <summary>Like a receipt or a send (PRD 4.2): Warehouse or Manager.</summary>
    [HttpGet("warehouse-or-manager")]
    [Authorize(Roles = Roles.WarehouseOrManager)]
    public ProbeIdentity WarehouseOrManager() => Describe();

    /// <summary>Like completing an order (PRD 4.2): Production or Manager.</summary>
    [HttpGet("production-or-manager")]
    [Authorize(Roles = Roles.ProductionOrManager)]
    public ProbeIdentity ProductionOrManager() => Describe();

    /// <summary>Like managing users (PRD 4.2): Manager only.</summary>
    [HttpGet("manager")]
    [Authorize(Roles = Roles.Manager)]
    public ProbeIdentity ManagerOnly() => Describe();

    /// <summary>What the app knows about the caller, read from the token's claims.</summary>
    private ProbeIdentity Describe() => new(
        User.Identity?.Name,
        User.FindAll(JwtTokenService.RoleClaim).Select(claim => claim.Value).ToList());
}

/// <summary>The caller as the probe sees them.</summary>
/// <param name="Email"><c>User.Identity.Name</c>, which is the email claim.</param>
/// <param name="Roles">The role claims of the token.</param>
public sealed record ProbeIdentity(string? Email, IReadOnlyList<string> Roles);
