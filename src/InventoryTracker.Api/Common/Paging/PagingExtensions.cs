using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Common.Paging;

/// <summary>
/// Runs a database query one page at a time, so every list endpoint pages the same way.
/// </summary>
public static class PagingExtensions
{
    /// <summary>
    /// Counts every row of <paramref name="query"/>, then loads only the rows of the requested page.
    /// </summary>
    /// <remarks>
    /// <paramref name="query"/> must already be filtered and sorted (newest first, design 2.5), because
    /// <c>Skip</c>/<c>Take</c> without an <c>OrderBy</c> gives an unpredictable order. An <c>IQueryable</c> is a
    /// query that has not run yet; EF Core turns it into SQL (<c>SELECT COUNT(*)</c>, then
    /// <c>... LIMIT pageSize OFFSET skip</c>) only when <c>CountAsync</c> or <c>ToListAsync</c> is awaited.
    /// The two statements are not in one transaction; for a read-only list a row added in between only
    /// shifts the page, which is acceptable.
    /// </remarks>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query, PageQuery page, CancellationToken cancellationToken = default)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page.Page, page.PageSize, totalCount);
    }
}
