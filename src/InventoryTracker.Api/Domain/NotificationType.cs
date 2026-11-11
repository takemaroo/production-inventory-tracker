namespace InventoryTracker.Api.Domain;

/// <summary>
/// The five kinds of notification Managers receive (US-23, decision #7). The message texts are in design 7.3.
/// </summary>
/// <remarks>Stored in the database as text (the enum name), exactly as listed in design 2.3.</remarks>
public enum NotificationType
{
    /// <summary>D-9, AC-20.4: a material's usage variance is more than the tolerance, in either direction. One per material.</summary>
    UsageVariance,

    /// <summary>D-9: the produced quantity differs from the ordered quantity by any amount.</summary>
    OutputVariance,

    /// <summary>An order was completed with leftover left in production storage. One per completion (PRD section 6 row 8).</summary>
    LeftoverAtCompletion,

    /// <summary>A complementary order was requested and waits for a Manager's decision (US-16).</summary>
    ComplementaryAwaitingApproval,

    /// <summary>An order was cancelled while material was still in production storage (US-21).</summary>
    OrderCancelledWithMaterial,
}
