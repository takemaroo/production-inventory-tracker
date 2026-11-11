namespace InventoryTracker.Api.Auth;

/// <summary>Body of <c>POST /api/v1/auth/sign-in</c> (design 5.1).</summary>
/// <param name="Email">The user's email. Letter case does not matter.</param>
/// <param name="Password">The user's password, exactly as typed.</param>
/// <remarks>
/// Both are nullable so that a missing field reaches <see cref="SignInService"/>, which answers with the design's
/// own <c>400</c> wording, instead of being refused by MVC first (architecture 6, Validation).
/// </remarks>
public sealed record SignInRequest(string? Email, string? Password);

/// <summary>Successful sign-in (design 5.1, AC-01.1).</summary>
/// <param name="AccessToken">The bearer token to send as <c>Authorization: Bearer &lt;token&gt;</c>.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresAt">When the token stops working: sign-in time plus 8 hours, UTC (AC-01.2).</param>
/// <param name="Email">The user's email as stored.</param>
/// <param name="Roles">The roles the user holds, in the order Warehouse, Production, Manager.</param>
public sealed record SignInResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAt,
    string Email,
    IReadOnlyList<string> Roles);
