namespace InventoryTracker.Api.Common.Configuration;

/// <summary>
/// Business settings read from the <c>Inventory</c> section of appsettings.json.
/// </summary>
/// <remarks>
/// ASP.NET Core configuration merges several sources, and later ones win: appsettings.json, then
/// appsettings.{Environment}.json, then user-secrets (Development only), then environment variables.
/// So this value can be overridden without editing a file, for example with the environment variable
/// <c>Inventory__TolerancePercent=3</c> (a double underscore stands for the section separator).
/// Code receives the values by asking for <c>IOptions&lt;InventoryOptions&gt;</c> in its constructor.
/// </remarks>
public sealed class InventoryOptions
{
    /// <summary>Name of the configuration section these values come from.</summary>
    public const string SectionName = "Inventory";

    /// <summary>
    /// D-1: the global tolerance t, as a percentage (2 means 2%). It sets the send ranges (D-7, AC-18.1)
    /// and the usage variance threshold (D-9, AC-20.4). PRD section 5: a value below 0 or above 100 must
    /// stop the application at start-up (implemented in T-003).
    /// </summary>
    public decimal TolerancePercent { get; set; } = 2m;
}
