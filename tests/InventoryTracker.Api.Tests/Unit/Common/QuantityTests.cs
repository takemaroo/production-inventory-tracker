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

    // ---- Decision #80: input maximums (T-008a) ---------------------------------------------------------------------

    /// <summary>
    /// T-008a, decision #80: the quantity limit is 999,999,999.999 on the size of the value, so it holds for signed
    /// adjustments too. The limit itself passes, the next whole number fails, on both sides of zero, and 0.001 above the
    /// limit (the smallest step a 3-place quantity can take) fails.
    /// </summary>
    [Theory]
    [InlineData("999999999.999", true)]
    [InlineData("1000000000", false)]
    [InlineData("1000000000.000", false)]
    [InlineData("-999999999.999", true)]
    [InlineData("-1000000000", false)]
    [InlineData("0", true)]
    [InlineData("0.001", true)]
    public void IsWithinMax_checks_the_size_of_the_quantity(string value, bool expected)
    {
        Assert.Equal(expected, Quantity.IsWithinMax(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// T-008a, decision #80: the recipe amount limit is 999,999.999999. The limit passes and 1,000,000 fails, on both
    /// sides of zero; 0.000001 above the limit (the smallest step a 6-place amount can take) fails.
    /// </summary>
    [Theory]
    [InlineData("999999.999999", true)]
    [InlineData("1000000", false)]
    [InlineData("1000000.000000", false)]
    [InlineData("-999999.999999", true)]
    [InlineData("-1000000", false)]
    [InlineData("0.000001", true)]
    public void IsWithinMaxAmountPerUnit_checks_the_size_of_the_amount(string value, bool expected)
    {
        Assert.Equal(expected, Quantity.IsWithinMaxAmountPerUnit(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// T-008a, design 7.2: the constants have the exact values of architecture 3.1, written out digit by digit here so
    /// a change to either limit fails this test rather than silently moving every check that uses it.
    /// </summary>
    [Fact]
    public void Limits_have_the_agreed_values()
    {
        Assert.Equal(999_999_999.999m, Quantity.MaxQuantity);
        Assert.Equal(999_999.999999m, Quantity.MaxAmountPerUnit);
    }

    /// <summary>
    /// T-008a, design 7.2: the above-the-limit messages match the design text exactly, with the limits written in full.
    /// (Formatting 999,999.999999 with the 3-place number format would print "1,000,000", which is why the messages use
    /// literal text.)
    /// </summary>
    [Theory]
    [InlineData("Quantity", "Quantity must be 999,999,999.999 or less.")]
    [InlineData("Ordered quantity", "Ordered quantity must be 999,999,999.999 or less.")]
    [InlineData("Produced quantity", "Produced quantity must be 999,999,999.999 or less.")]
    [InlineData("Leftover", "Leftover must be 999,999,999.999 or less.")]
    [InlineData("Requested amount", "Requested amount must be 999,999,999.999 or less.")]
    public void AboveMaxMessage_matches_the_design_text(string fieldLabel, string expected)
    {
        Assert.Equal(expected, Quantity.AboveMaxMessage(fieldLabel));
    }

    /// <summary>T-008a, design 7.2: the recipe amount message is "Amount per unit must be 999,999.999999 or less.".</summary>
    [Fact]
    public void AmountPerUnitAboveMaxMessage_matches_the_design_text()
    {
        Assert.Equal("Amount per unit must be 999,999.999999 or less.", Quantity.AmountPerUnitAboveMaxMessage);
    }

    /// <summary>
    /// T-008a, decision #80, architecture 3.1 "Why these numbers": the largest amount per unit times the largest
    /// quantity does not overflow C# <c>decimal</c>, rounds (3 places, half away from zero) to 999,999,999,998,000.000,
    /// and that is below the <c>decimal(18,3)</c> maximum of 999,999,999,999,999.999 with at most 3 decimal places. This
    /// proves every planned amount (AC-13.1: amount per unit × ordered quantity) and every required amount for actual
    /// output (D-9: amount per unit × produced quantity) built from accepted input fits its column.
    /// </summary>
    [Fact]
    public void Max_amount_times_max_quantity_fits_a_quantity_column()
    {
        // The decimal(18,3) maximum: 15 digits before the point, 3 after (architecture 3.1).
        const decimal ColumnMaximum = 999_999_999_999_999.999m;

        // Decimal arithmetic throws OverflowException instead of wrapping, so computing the product is itself the
        // overflow check; the exception would fail the test.
        var product = Quantity.MaxAmountPerUnit * Quantity.MaxQuantity;
        var rounded = Quantity.Round3(product);

        // The exact product is 999,999,999,998,000.000000001; decimal keeps all of its digits.
        Assert.Equal(999_999_999_998_000.000000001m, product);
        Assert.Equal(999_999_999_998_000.000m, rounded);
        Assert.True(rounded < ColumnMaximum);
        Assert.True(Quantity.HasAtMostDecimals(rounded, 3));
    }
}
