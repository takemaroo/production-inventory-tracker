using InventoryTracker.Api.Common.Validation;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// Architecture 4.1 item 3 and design 2.3, 2.5, 2.6: enum and date values received as text, and text cleaning.
/// </summary>
public class ParserTests
{
    /// <summary>A stand-in enum, so these tests do not depend on the domain enums T-002 is writing.</summary>
    public enum Shape
    {
        RawMaterial,
        FinishedProduct,
    }

    /// <summary>A stand-in with four names, like the order status.</summary>
    public enum Status
    {
        Created,
        InProduction,
        Completed,
        Cancelled,
    }

    /// <summary>Design 2.3: enum names are accepted in any letter case.</summary>
    [Theory]
    [InlineData("RawMaterial", Shape.RawMaterial)]
    [InlineData("rawmaterial", Shape.RawMaterial)]
    [InlineData("FINISHEDPRODUCT", Shape.FinishedProduct)]
    public void Enum_names_are_accepted_in_any_case(string value, Shape expected)
    {
        Assert.True(EnumParser.TryParse(value, out Shape result));
        Assert.Equal(expected, result);
    }

    /// <summary>Architecture 4.1 item 3: only names are valid; numbers, blanks, unknown names and comma lists are refused.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Raw")]
    [InlineData(" RawMaterial")]
    [InlineData("RawMaterial,FinishedProduct")]
    public void Anything_but_a_name_is_refused(string? value)
    {
        Assert.False(EnumParser.TryParse(value, out Shape _));
    }

    /// <summary>Design 7.2: the unknown-type message lists the names: "Type must be RawMaterial or FinishedProduct.".</summary>
    [Fact]
    public void Message_lists_two_names_with_or()
    {
        Assert.Equal("Type must be RawMaterial or FinishedProduct.", EnumParser.AllowedValuesMessage<Shape>("Type"));
    }

    /// <summary>Design 2.5: the message for an unknown filter value lists every name.</summary>
    [Fact]
    public void Message_lists_many_names_with_commas_and_or()
    {
        Assert.Equal("status must be Created, InProduction, Completed or Cancelled.",
            EnumParser.AllowedValuesMessage<Status>("status"));
    }

    /// <summary>Design 7.2: a missing or unknown required value adds the message under its JSON path.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Liquid")]
    public void Required_enum_adds_the_message_when_missing_or_unknown(string? value)
    {
        var errors = new ValidationErrors();

        var result = EnumParser.ParseRequired<Shape>(value, "type", "Type", errors);

        Assert.Null(result);
        Assert.Equal(["Type must be RawMaterial or FinishedProduct."], errors.ToDictionary()["type"]);
    }

    /// <summary>Design 2.5: an absent filter means "no filter", a valid one parses, an unknown one (status=Done) is an error.</summary>
    [Fact]
    public void Optional_enum_filter()
    {
        var errors = new ValidationErrors();

        Assert.Null(EnumParser.ParseOptional<Status>(null, "status", "status", errors));
        Assert.Null(EnumParser.ParseOptional<Status>("", "status", "status", errors));
        Assert.False(errors.HasErrors);

        Assert.Equal(Status.Completed, EnumParser.ParseOptional<Status>("completed", "status", "status", errors));
        Assert.False(errors.HasErrors);

        Assert.Null(EnumParser.ParseOptional<Status>("Done", "status", "status", errors));
        Assert.True(errors.HasErrors);
    }

    /// <summary>Design 2.5: dates are exactly yyyy-MM-dd.</summary>
    [Fact]
    public void Date_in_the_design_format_is_accepted()
    {
        Assert.True(DateParser.TryParse("2026-11-01", out var date));
        Assert.Equal(new DateOnly(2026, 11, 1), date);
    }

    /// <summary>Design 2.5: a date in any other format, or an impossible date, is refused (from=yesterday is 400).</summary>
    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-11-1")]
    [InlineData("01/11/2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-11-01T00:00:00Z")]
    public void Other_date_formats_are_refused(string value)
    {
        Assert.False(DateParser.TryParse(value, out _));
    }

    /// <summary>Design 2.5: an optional date that is missing gives no error; a bad one adds a message naming the parameter.</summary>
    [Fact]
    public void Optional_date()
    {
        var errors = new ValidationErrors();

        Assert.Null(DateParser.ParseOptional(null, "from", errors));
        Assert.False(errors.HasErrors);

        Assert.Null(DateParser.ParseOptional("yesterday", "from", errors));
        Assert.Equal(["'from' must be a date in the format yyyy-MM-dd."], errors.ToDictionary()["from"]);
    }

    /// <summary>Design 2.5 and 7.2: from later than to gives "'from' must be on or before 'to'."; equal dates are one valid day.</summary>
    [Fact]
    public void Date_range_order()
    {
        var sameDay = new ValidationErrors();
        DateParser.CheckRange(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), sameDay);
        Assert.False(sameDay.HasErrors);

        var reversed = new ValidationErrors();
        DateParser.CheckRange(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), reversed);
        Assert.Equal(["'from' must be on or before 'to'."], reversed.ToDictionary()["from"]);
    }

    /// <summary>Design 2.6: text is trimmed, and text that is empty after trimming counts as missing (null).</summary>
    [Theory]
    [InlineData("  kg  ", "kg")]
    [InlineData("Delivery note 4471", "Delivery note 4471")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Text_clean_trims_and_treats_blank_as_missing(string? value, string? expected)
    {
        Assert.Equal(expected, Text.Clean(value));
    }
}
