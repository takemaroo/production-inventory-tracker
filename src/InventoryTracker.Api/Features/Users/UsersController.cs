using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Paging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryTracker.Api.Features.Users;

/// <summary>
/// User management, <c>/api/v1/users</c> (US-03, design 4 and 5.2). Thin: every rule is in <see cref="UserService"/>
/// and <see cref="PasswordService"/>.
/// </summary>
/// <remarks>
/// <c>[Authorize(Roles = Roles.Manager)]</c> on the class applies to every action: only a Manager may manage users
/// (PRD 4.2, AC-03.9). The authorization middleware checks it before any action runs, so a caller without a token
/// gets <c>401</c> (AC-01.6: nobody unauthenticated can create a user) and a signed-in non-Manager gets <c>403</c>,
/// both before the user id in the URL is even looked at (design 2.2).
/// </remarks>
[ApiController]
[Route("api/v1/users")]
[Authorize(Roles = Roles.Manager)]
public sealed class UsersController(UserService users, PasswordService passwords) : ControllerBase
{
    /// <summary>Lists users newest first, one page at a time, with email, roles and active status (AC-03.4).</summary>
    /// <remarks>
    /// The parameter must not be called <c>page</c>: model binding would then read <c>?page=2</c> as the prefix of the
    /// whole object and look for <c>page.page</c>, so the page number would be ignored.
    /// </remarks>
    [HttpGet]
    public Task<PagedResult<UserResponse>> List([FromQuery] PageQuery paging, CancellationToken cancellationToken) =>
        users.ListAsync(paging, cancellationToken);

    /// <summary>Returns one user, or <c>404</c> "User not found.".</summary>
    [HttpGet("{id}")]
    public Task<UserResponse> Get(string id, CancellationToken cancellationToken) =>
        users.GetAsync(id, cancellationToken);

    /// <summary>
    /// Creates a user with an email, an initial password and one or more roles (AC-03.1). <c>201</c> with the user and
    /// a <c>Location</c> header; <c>400</c> for bad input (AC-03.3); <c>409</c> <c>email-taken</c> (AC-03.2).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await users.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/users/{user.Id}", user);
    }

    /// <summary>
    /// Replaces the user's roles (AC-03.5). The new roles apply from the user's next sign-in. <c>409</c>
    /// <c>last-manager</c> when it would remove Manager from the last active Manager (AC-03.8).
    /// </summary>
    [HttpPut("{id}/roles")]
    public Task<UserResponse> SetRoles(string id, SetRolesRequest request, CancellationToken cancellationToken) =>
        users.SetRolesAsync(id, request, cancellationToken);

    /// <summary>
    /// Deactivates the user (AC-03.6): they cannot sign in and their existing tokens get <c>401</c>. <c>409</c>
    /// <c>last-manager</c> for the last active Manager (AC-03.8). Repeating it changes nothing.
    /// </summary>
    [HttpPost("{id}/deactivate")]
    public Task<UserResponse> Deactivate(string id, CancellationToken cancellationToken) =>
        users.DeactivateAsync(id, cancellationToken);

    /// <summary>Reactivates the user so they can sign in again (AC-03.6). Repeating it changes nothing.</summary>
    [HttpPost("{id}/reactivate")]
    public Task<UserResponse> Reactivate(string id, CancellationToken cancellationToken) =>
        users.ReactivateAsync(id, cancellationToken);

    /// <summary>
    /// Sets a new password for the user (AC-03.7); the policy applies. <c>204</c>. The user's existing tokens stop
    /// working, and any lock-out ends so they can sign in at once (decision #72).
    /// </summary>
    [HttpPut("{id}/password")]
    public async Task<IActionResult> SetPassword(string id, SetPasswordRequest request, CancellationToken cancellationToken)
    {
        await passwords.SetPasswordAsync(id, request, cancellationToken);
        return NoContent();
    }
}
