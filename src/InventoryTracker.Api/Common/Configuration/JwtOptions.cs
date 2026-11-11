namespace InventoryTracker.Api.Common.Configuration;

/// <summary>
/// Settings for the sign-in tokens (JWTs): who issues them, who they are for, how long they last, and the
/// secret key that signs them (architecture 6, Tokens and Configuration).
/// </summary>
/// <remarks>
/// <see cref="Issuer"/>, <see cref="Audience"/> and <see cref="TokenLifetimeHours"/> are ordinary settings from the
/// <c>Jwt</c> section of appsettings.json. <see cref="SigningKey"/> is a secret, so it never comes from a tracked
/// file: it is read from the configuration key <c>JWT_SIGNING_KEY</c>, which is fed by an environment variable or,
/// in Development, by <c>dotnet user-secrets</c> (decision #32). <c>AuthSetup</c> copies it in.
/// </remarks>
public sealed class JwtOptions
{
    /// <summary>Name of the configuration section the non-secret values come from.</summary>
    public const string SectionName = "Jwt";

    /// <summary>Configuration key (and environment variable name) of the signing key.</summary>
    public const string SigningKeyVariable = "JWT_SIGNING_KEY";

    /// <summary>
    /// HS256 signs with a 256-bit key, so the key must be at least 32 bytes. Start-up refuses a shorter one
    /// (design 7.4).
    /// </summary>
    public const int MinimumSigningKeyLength = 32;

    /// <summary>The <c>iss</c> claim: who issued the token. Tokens from any other issuer are refused.</summary>
    public string Issuer { get; set; } = "InventoryTracker";

    /// <summary>The <c>aud</c> claim: which API the token is meant for. Tokens for any other audience are refused.</summary>
    public string Audience { get; set; } = "InventoryTracker";

    /// <summary>AC-01.2 and PRD assumption 13: a token is valid for 8 hours, with no refresh.</summary>
    public int TokenLifetimeHours { get; set; } = 8;

    /// <summary>The secret from <c>JWT_SIGNING_KEY</c>. Never logged and never written to a file.</summary>
    public string SigningKey { get; set; } = "";
}
