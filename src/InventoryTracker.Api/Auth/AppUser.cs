using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Api.Auth;

/// <summary>
/// A staff member who signs in to the API (US-01, US-03).
/// </summary>
/// <remarks>
/// ASP.NET Core Identity already provides <see cref="IdentityUser"/>: id, email, a salted password hash,
/// lock-out counters and a security stamp. We inherit from it and add only what the PRD needs on top.
/// The email is also used as the user name.
/// </remarks>
public class AppUser : IdentityUser
{
    /// <summary>
    /// AC-03.6: users are never deleted, only deactivated, so their name stays on every movement and order.
    /// An inactive user cannot sign in and their existing tokens are rejected.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>When the account was created (UTC). Used to list users newest first (PRD 4.1).</summary>
    public DateTime CreatedAt { get; set; }
}
