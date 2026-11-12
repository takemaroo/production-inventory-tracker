namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// The exact <c>detail</c> texts of the sign-in and role refusals (<c>401</c> and <c>403</c>, design 7.1) that tests of
/// many endpoints check. Kept in one place so every endpoint's <c>401</c> and <c>403</c> tests compare against the same
/// wording.
/// </summary>
/// <remarks>
/// These are <c>const</c>, not <c>static readonly</c>, so they can also be used inside attributes such as
/// <c>[InlineData(...)]</c>, which only accept values known when the code is compiled.
/// </remarks>
public static class ExpectedMessages
{
    /// <summary>AC-01.5: <c>401</c> <c>not-authenticated</c>, for a missing, expired or otherwise invalid token.</summary>
    public const string NotAuthenticated = "Your sign-in is missing or has expired. Sign in again.";

    /// <summary>AC-01.4: <c>401</c> <c>account-locked</c>, after five wrong passwords in a row.</summary>
    public const string AccountLocked = "Account temporarily locked. Try again later.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action only a Manager may do.</summary>
    public const string ManagerOnly = "Your roles do not allow this action. It needs the Manager role.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action for the Warehouse or Manager role.</summary>
    public const string WarehouseOrManager = "Your roles do not allow this action. It needs the Warehouse or Manager role.";

    /// <summary>AC-05.1: <c>403</c> <c>forbidden</c> on an action for the Production or Manager role.</summary>
    public const string ProductionOrManager = "Your roles do not allow this action. It needs the Production or Manager role.";
}
