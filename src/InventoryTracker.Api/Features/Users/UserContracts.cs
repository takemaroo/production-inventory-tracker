namespace InventoryTracker.Api.Features.Users;

/// <summary>Body of <c>POST /api/v1/users</c> (design 5.2, AC-03.1).</summary>
/// <param name="Email">The new user's email; also their user name. Leading and trailing spaces are removed.</param>
/// <param name="Password">The initial password. It must meet the password policy (PRD section 5).</param>
/// <param name="Roles">One or more of Warehouse, Production, Manager, Seller, in any letter case.</param>
/// <remarks>
/// Every field is nullable and the roles are plain strings, so that a missing or unknown value reaches
/// <see cref="UserService"/>, which answers with the design's own <c>400</c> wording instead of a framework message
/// (architecture 6, Validation).
/// </remarks>
public sealed record CreateUserRequest(string? Email, string? Password, IReadOnlyList<string?>? Roles);

/// <summary>Body of <c>PUT /api/v1/users/{id}/roles</c> (design 5.2, AC-03.5).</summary>
/// <param name="Roles">The complete new set of roles (at least one). Roles not listed are removed.</param>
public sealed record SetRolesRequest(IReadOnlyList<string?>? Roles);

/// <summary>Body of <c>PUT /api/v1/users/{id}/password</c>, a Manager setting another user's password (AC-03.7).</summary>
/// <param name="NewPassword">The new password. It must meet the password policy.</param>
public sealed record SetPasswordRequest(string? NewPassword);

/// <summary>Body of <c>POST /api/v1/auth/change-password</c>, a user changing their own password (design 5.1, US-04).</summary>
/// <param name="CurrentPassword">The password the user signs in with now, exactly as typed.</param>
/// <param name="NewPassword">The new password. It must meet the password policy.</param>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// The user object of design 5.2. AC-03.4: it never contains a password or a password hash. It is built field by
/// field from the database row, so a new Identity column can never leak into a response by accident.
/// </summary>
/// <param name="Id">The string id ASP.NET Core Identity generated (design 2.1).</param>
/// <param name="Email">The email as stored (trimmed, letter case as entered, design 2.6).</param>
/// <param name="Roles">The roles the user holds now, in the order Warehouse, Production, Manager, Seller.</param>
/// <param name="IsActive">False after deactivation (AC-03.6).</param>
/// <param name="CreatedAt">When the account was created (UTC, whole seconds).</param>
public sealed record UserResponse(
    string Id,
    string Email,
    IReadOnlyList<string> Roles,
    bool IsActive,
    DateTime CreatedAt);
