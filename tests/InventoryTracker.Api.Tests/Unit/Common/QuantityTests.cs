using InventoryTracker.Api.Common.Quantities;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>PRD 4.1 and design 2.4: rounding of computed quantities and the decimal-place limit on input.</summary>
public class QuantityTests
{
    /// <summary>T-001 AC 4: trailing zeros do not count, so 12.5000 has at most 3 decimals.</summary>
    [Fact]
    public void Trailing_zeros_do_not_count_as_decimal_places()
    {
        Assert.True(Quantity.HasAtMostDecimals(12.5000m, 3));
    }

    /// <summary>T-001 AC 4: 0.0001 has 4 significant decimals, so it fails a 3-decimal limit.</summary>
    [Fact]
    public void Four_decimals_fail_a_three_decimal_limit()
    {
        Assert.False(Quantity.HasAtMostDecimals(0.0001m, 3));
    }

    /// <summary>PRD 4.1: the limits themselves are accepted (3 for quantities, 6 for recipe amounts per unit), one more is not.</summary>
    [Theory]
    [InlineData("974.999", 3, true)]
    [InlineData("-12.125", 3, true)]
    [InlineData("1000", 3, true)]
    [InlineData("0", 3, true)]
    [InlineData("1.0005", 3, false)]
    [InlineData("0.015", 6, true)]
    [InlineData("0.123456", 6, true)]
    [InlineData("0.1234567", 6, false)]
    public void Decimal_place_limit(string value, int places, bool expected)
    {
        Assert.Equal(expected, Quantity.HasAtMostDecimals(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), places));
    }

    /// <summary>T-001 AC 4 and PRD 4.1: rounding is half away from zero, so 1.0005 gives 1.001 (not banker's 1.000).</summary>
    [Fact]
    public void Round3_rounds_half_away_from_zero_for_positive_values()
    {
        Assert.Equal(1.001m, Quantity.Round3(1.0005m));
    }

    /// <summary>T-001 AC 4 and PRD 4.1: the same rule for negative values, so -1.0005 gives -1.001.</summary>
    [Fact]
    public void Round3_rounds_half_away_from_zero_for_negative_values()
    {
        Assert.Equal(-1.001m, Quantity.Round3(-1.0005m));
    }

    /// <summary>PRD 4.1: values off the midpoint round to the nearest third decimal; short values are unchanged.</summary>
    [Theory]
    [InlineData("1.0004", "1")]
    [InlineData("1.0006", "1.001")]
    [InlineData("974.99949", "974.999")]
    [InlineData("1020", "1020")]
    [InlineData("14.7", "14.7")]
    public void Round3_rounds_to_three_places(string value, string expected)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, culture), Quantity.Round3(decimal.Parse(value, culture)));
    }
}
