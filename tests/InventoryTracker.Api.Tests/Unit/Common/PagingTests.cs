using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Validation;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// Design 2.5 and PRD 4.1: page parameters and their limits. Paging an actual database query is tested in
/// <c>Integration/CommonFoundationsTests</c>, because unit tests use no database (architecture 7).
/// </summary>
public class PagingTests
{
    /// <summary>T-001 AC 6: defaults are page 1 and page size 50.</summary>
    [Fact]
    public void Defaults_are_page_1_and_size_50()
    {
        var query = new PageQuery();

        Assert.Equal(1, query.Page);
        Assert.Equal(50, query.PageSize);
        Assert.Equal(0, query.Skip);
    }

    /// <summary>T-001 AC 6: page below 1 and pageSize outside 1–100 add the design 7.2 messages under their names.</summary>
    [Fact]
    public void Out_of_range_values_add_design_messages()
    {
        var errors = new ValidationErrors();

        new PageQuery { Page = 0, PageSize = 101 }.Validate(errors);

        var byPath = errors.ToDictionary();
        Assert.Equal(["page must be 1 or greater."], byPath["page"]);
        Assert.Equal(["pageSize must be between 1 and 100."], byPath["pageSize"]);
    }

    /// <summary>T-001 AC 6: ThrowIfInvalid throws a 400 validation exception, and accepts the limits 1 and 100.</summary>
    [Fact]
    public void ThrowIfInvalid_throws_only_outside_the_limits()
    {
        new PageQuery { Page = 1, PageSize = 1 }.ThrowIfInvalid();
        new PageQuery { Page = 7, PageSize = 100 }.ThrowIfInvalid();

        var exception = Assert.Throws<ValidationException>(() => new PageQuery { PageSize = 0 }.ThrowIfInvalid());
        Assert.Equal("pageSize must be between 1 and 100.", exception.Detail);
    }

    /// <summary>Design 2.5: Skip is (page − 1) × pageSize, and a huge page number does not overflow.</summary>
    [Fact]
    public void Skip_counts_earlier_pages_without_overflow()
    {
        Assert.Equal(40, new PageQuery { Page = 3, PageSize = 20 }.Skip);
        Assert.Equal(int.MaxValue, new PageQuery { Page = int.MaxValue, PageSize = 100 }.Skip);
    }
}
