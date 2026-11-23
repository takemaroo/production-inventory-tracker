using InventoryTracker.Api.Auth;

namespace InventoryTracker.Api.Tests.Unit.Auth;

/// <summary>
/// (v1.1) T-023, design 7.1, architecture 4.1 item 13: the <c>403</c> sentence that
/// <see cref="ForbiddenResponseHandler.Message"/> builds from the roles an endpoint allows. Version 1 only ever had one
/// or two roles in a requirement; version 1.1 adds three-role sets, joined as "A, B or C".
/// </summary>
/// <remarks>
/// The expected sentences are typed out here, word for word from design 7.1, not taken from
/// <c>ExpectedMessages</c>, so this test stands on its own as the check of the sentence-building rule (decision #88).
/// </remarks>
public class ForbiddenResponseMessageTests
{
    /// <summary>
    /// Design 7.1, the three new v1.1 sentences: the roles appear in the order given, joined with ", " and the last two
    /// with " or ".
    /// </summary>
    [Theory]
    [InlineData(new[] { "Warehouse", "Production", "Manager" },
        "Your roles do not allow this action. It needs the Warehouse, Production or Manager role.")]
    [InlineData(new[] { "Warehouse", "Manager", "Seller" },
        "Your roles do not allow this action. It needs the Warehouse, Manager or Seller role.")]
    [InlineData(new[] { "Manager", "Seller" },
        "Your roles do not allow this action. It needs the Manager or Seller role.")]
    public void Message_names_the_roles_as_design_7_1_writes_them(string[] roles, string expected)
    {
        Assert.Equal(expected, ForbiddenResponseHandler.Message(roles));
    }

    /// <summary>Design 7.1, the version 1 sentences, unchanged: one role, and two roles joined with " or ".</summary>
    [Theory]
    [InlineData(new[] { "Manager" }, "Your roles do not allow this action. It needs the Manager role.")]
    [InlineData(new[] { "Warehouse", "Manager" }, "Your roles do not allow this action. It needs the Warehouse or Manager role.")]
    public void Version_1_sentences_are_unchanged(string[] roles, string expected)
    {
        Assert.Equal(expected, ForbiddenResponseHandler.Message(roles));
    }

    /// <summary>
    /// Architecture 4.1 item 13, decision #156: each new <see cref="Roles"/> constant is written in role order, so the
    /// roles ASP.NET Core reads from <c>[Authorize(Roles = ...)]</c> (split at the commas, in that order) give design
    /// 7.1's sentence. A constant written in another order ("Seller,Manager") would fail here.
    /// </summary>
    [Theory]
    [InlineData(Roles.WarehouseProductionOrManager, "Your roles do not allow this action. It needs the Warehouse, Production or Manager role.")]
    [InlineData(Roles.WarehouseManagerOrSeller, "Your roles do not allow this action. It needs the Warehouse, Manager or Seller role.")]
    [InlineData(Roles.ManagerOrSeller, "Your roles do not allow this action. It needs the Manager or Seller role.")]
    public void Each_new_roles_constant_gives_its_design_sentence(string rolesAttribute, string expected)
    {
        Assert.Equal(expected, ForbiddenResponseHandler.Message(rolesAttribute.Split(',')));
    }
}
