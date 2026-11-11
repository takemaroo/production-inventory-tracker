using System.Globalization;
using InventoryTracker.Api.Common.Quantities;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>Design 6.1: how numbers are written inside messages.</summary>
public class MessageFormatTests
{
    /// <summary>T-001 AC 5: quantities use a comma thousands separator and up to 3 decimals with trailing zeros removed.</summary>
    [Theory]
    [InlineData("1015", "1,015")]
    [InlineData("974.999", "974.999")]
    [InlineData("0.6", "0.6")]
    [InlineData("0", "0")]
    [InlineData("12.5000", "12.5")]
    [InlineData("1234567.891", "1,234,567.891")]
    [InlineData("-25", "-25")]
    public void Number_follows_design_6_1(string value, string expected)
    {
        Assert.Equal(expected, MessageFormat.Number(Parse(value)));
    }

    /// <summary>Design 6.1: a value that rounds to zero, even from below, is written "0", never "-0".</summary>
    [Fact]
    public void Number_never_writes_negative_zero()
    {
        Assert.Equal("0", MessageFormat.Number(-0.0001m));
    }

    /// <summary>T-001 AC 5: signed differences carry "+" or the ASCII "-": 29 gives "+29".</summary>
    [Theory]
    [InlineData("29", "+29")]
    [InlineData("-25", "-25")]
    [InlineData("1015", "+1,015")]
    [InlineData("-0.75", "-0.75")]
    [InlineData("0", "0")]
    public void SignedNumber_follows_design_6_1(string value, string expected)
    {
        Assert.Equal(expected, MessageFormat.SignedNumber(Parse(value)));
    }

    /// <summary>T-001 AC 5: percentages always have 2 decimals and a sign: 2.9 gives "+2.90%", -2.5 gives "-2.50%".</summary>
    [Theory]
    [InlineData("2.9", "+2.90%")]
    [InlineData("-2.5", "-2.50%")]
    [InlineData("0.1", "+0.10%")]
    [InlineData("1.5", "+1.50%")]
    public void SignedPercent_follows_design_6_1(string value, string expected)
    {
        Assert.Equal(expected, MessageFormat.SignedPercent(Parse(value)));
    }

    /// <summary>
    /// AC-20.4 and PRD 4.1: percentages are shown rounded to 2 places, half away from zero; 2.0001 is shown as "+2.00%"
    /// (the above-tolerance decision uses the unrounded value elsewhere).
    /// </summary>
    [Theory]
    [InlineData("2.0001", "+2.00%")]
    [InlineData("2.005", "+2.01%")]
    [InlineData("-2.005", "-2.01%")]
    [InlineData("-0.004", "0.00%")]
    public void SignedPercent_rounds_to_two_places(string value, string expected)
    {
        Assert.Equal(expected, MessageFormat.SignedPercent(Parse(value)));
    }

    /// <summary>Design 6.1: formatting is the same whatever the server's regional settings (invariant culture).</summary>
    [Fact]
    public void Formatting_ignores_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // Spanish uses "." for thousands and "," for decimals.
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");

            Assert.Equal("1,015.5", MessageFormat.Number(1015.5m));
            Assert.Equal("+2.90%", MessageFormat.SignedPercent(2.9m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
