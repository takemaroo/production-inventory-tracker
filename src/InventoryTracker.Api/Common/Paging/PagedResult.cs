namespace InventoryTracker.Api.Common.Paging;

/// <summary>
/// The envelope every list endpoint returns (design 2.5):
/// <c>{ "items": [...], "page": 1, "pageSize": 50, "totalCount": 132 }</c>.
/// </summary>
/// <typeparam name="T">The response shape of one list row.</typeparam>
/// <param name="Items">The rows on this page, newest first. Empty for a page past the end.</param>
/// <param name="Page">The page number that was asked for.</param>
/// <param name="PageSize">The page size that was asked for.</param>
/// <param name="TotalCount">How many rows match the filters across all pages (PRD 4.1, "with the total count").</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
