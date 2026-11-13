namespace InventoryTracker.Api.Common.Contracts;

/// <summary>
/// A person as every response shows them (design 2.3, "People"): <c>{ "id": "8b1c…", "email": "ana.ruiz@example.com" }</c>.
/// The PRD gives users an email and no display name. Used wherever a response says who did something (AC-05.3): the user
/// of a movement, and who created, sent, completed or cancelled an order, a complementary order or a return.
/// </summary>
/// <param name="Id">The user's Identity id.</param>
/// <param name="Email">The user's email.</param>
public sealed record PersonResponse(string Id, string Email);
