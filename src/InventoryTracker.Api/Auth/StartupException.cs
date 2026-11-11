namespace InventoryTracker.Api.Auth;

/// <summary>
/// A start-up check failed, so the application must not start (US-02, PRD section 5, design 7.4).
/// </summary>
/// <remarks>
/// The message is the exact design 7.4 text the owner sees in the console. Program.cs catches this exception, logs
/// the message at Critical level and exits with code 1 before listening for requests. Nothing else catches it.
/// </remarks>
public sealed class StartupException(string message) : Exception(message);
