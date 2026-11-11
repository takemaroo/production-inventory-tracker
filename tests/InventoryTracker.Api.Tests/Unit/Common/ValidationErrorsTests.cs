using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Validation;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>Architecture 6, Validation, and design 6: collecting field messages and throwing them as one 400.</summary>
public class ValidationErrorsTests
{
    /// <summary>Nothing collected means nothing is thrown, so a valid request carries on.</summary>
    [Fact]
    public void No_errors_does_not_throw()
    {
        var errors = new ValidationErrors();

        errors.ThrowIfAny();

        Assert.False(errors.HasErrors);
    }

    /// <summary>
    /// Design 6: errors are keyed by JSON path, detail is every message joined with a space in the order added,
    /// and the code is "validation".
    /// </summary>
    [Fact]
    public void Collected_errors_are_thrown_as_one_validation_exception()
    {
        var errors = new ValidationErrors();
        errors.Add("quantity", "Quantity must be greater than 0.");
        errors.Add("lines[0].itemId", "Item 9 does not exist.");
        errors.Add("quantity", "Quantity can have at most 3 decimal places.");

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny());

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("validation", exception.Code);
        Assert.Equal(
            "Quantity must be greater than 0. Item 9 does not exist. Quantity can have at most 3 decimal places.",
            exception.Detail);
        Assert.NotNull(exception.Errors);
        Assert.Equal(
            ["Quantity must be greater than 0.", "Quantity can have at most 3 decimal places."],
            exception.Errors["quantity"]);
        Assert.Equal(["Item 9 does not exist."], exception.Errors["lines[0].itemId"]);
    }

    /// <summary>AC-15.6 and design 6: a rule with its own code (send-out-of-range) still lists every failing line in errors.</summary>
    [Fact]
    public void A_specific_code_can_be_used()
    {
        var errors = new ValidationErrors();
        errors.Add("lines[0].quantity", "Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg).");
        errors.Add("lines[1].quantity", "Potato starch: send must be between 14.7 and 15.3 kg (planned 15 kg, leftover applied 0 kg).");

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny("send-out-of-range"));

        Assert.Equal("send-out-of-range", exception.Code);
        Assert.Equal(2, exception.Errors!.Count);
        Assert.Equal(
            "Cheese: send must be between 975 and 1,015 kg (planned 1,000 kg, leftover applied 5 kg). " +
            "Potato starch: send must be between 14.7 and 15.3 kg (planned 15 kg, leftover applied 0 kg).",
            exception.Detail);
    }

    /// <summary>The single-field shortcut puts the message in both detail and errors.</summary>
    [Fact]
    public void Single_field_shortcut()
    {
        var exception = new ValidationException("page", "page must be 1 or greater.");

        Assert.Equal("page must be 1 or greater.", exception.Detail);
        Assert.Equal(["page must be 1 or greater."], exception.Errors!["page"]);
    }

    /// <summary>Architecture 6: each ApiException subclass carries the status and code its response needs; Message equals Detail.</summary>
    [Fact]
    public void Exception_types_carry_status_and_code()
    {
        var notFound = new NotFoundException("Unit 4 not found.");
        var conflict = new ConflictException("unit-in-use", "Unit is used by one or more items.");
        var badRequest = new BadRequestException("reason-required", "A reason is required when leftover is recorded.");

        Assert.Equal((404, "not-found"), (notFound.StatusCode, notFound.Code));
        Assert.Equal((409, "unit-in-use"), (conflict.StatusCode, conflict.Code));
        Assert.Equal((400, "reason-required"), (badRequest.StatusCode, badRequest.Code));
        Assert.Null(badRequest.Errors);
        Assert.Equal("Unit 4 not found.", notFound.Message);
    }
}
