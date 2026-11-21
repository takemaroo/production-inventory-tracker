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

    /// <summary>
    /// SEC-01: exactly <see cref="ValidationErrors.MaxMessages"/> messages are all listed, with no "not shown" sentence.
    /// </summary>
    [Fact]
    public void Up_to_the_cap_every_message_is_listed()
    {
        var errors = new ValidationErrors();
        for (var index = 0; index < ValidationErrors.MaxMessages; index++)
        {
            errors.Add($"lines[{index}].itemId", "Item is required.");
        }

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny());

        Assert.Equal(ValidationErrors.MaxMessages, exception.Errors!.Values.Sum(messages => messages.Length));
        Assert.Equal(string.Join(" ", Enumerable.Repeat("Item is required.", ValidationErrors.MaxMessages)), exception.Detail);
        Assert.DoesNotContain("not shown", exception.Detail);
    }

    /// <summary>
    /// SEC-01: past the cap, only the first 100 messages are kept, in the order added, in both <c>errors</c> and
    /// <c>detail</c>; <c>detail</c> ends with one sentence counting the rest (number formatted as design 6.1), and the
    /// sentence is in no field's <c>errors</c>.
    /// </summary>
    [Fact]
    public void Past_the_cap_messages_are_counted_not_listed()
    {
        var errors = new ValidationErrors();
        for (var index = 0; index < 2_000; index++)
        {
            errors.Add($"lines[{index}].itemId", $"Line {index} is wrong.");
        }

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny());

        Assert.Equal(100, ValidationErrors.MaxMessages);
        Assert.Equal(100, exception.Errors!.Count);
        Assert.Equal(["Line 99 is wrong."], exception.Errors["lines[99].itemId"]);
        Assert.False(exception.Errors.ContainsKey("lines[100].itemId"));
        Assert.StartsWith("Line 0 is wrong. Line 1 is wrong.", exception.Detail);
        Assert.EndsWith("Line 99 is wrong. 1,900 more problems are not shown.", exception.Detail);
        Assert.DoesNotContain(exception.Errors.Values.SelectMany(messages => messages), message => message.Contains("not shown"));
    }

    /// <summary>SEC-01: one message over the cap is counted in the singular.</summary>
    [Fact]
    public void One_message_over_the_cap_is_singular()
    {
        var errors = new ValidationErrors();
        for (var index = 0; index <= ValidationErrors.MaxMessages; index++)
        {
            errors.Add("roles", "Unknown role.");
        }

        var exception = Assert.Throws<ValidationException>(() => errors.ThrowIfAny());

        Assert.EndsWith("Unknown role. 1 more problem is not shown.", exception.Detail);
        Assert.Equal(ValidationErrors.MaxMessages, exception.Errors!["roles"].Length);
    }

    /// <summary>
    /// SEC-01: a million messages leave a small error. Before the cap, the reviewer's 3,000,000 null lines produced a
    /// 412 MB response; the detail is now bounded by 100 messages.
    /// </summary>
    [Fact]
    public void A_million_messages_give_a_small_detail()
    {
        var errors = new ValidationErrors();
        for (var index = 0; index < 1_000_000; index++)
        {
            errors.Add($"lines[{index}].quantity", "Quantity is required.");
        }

        Assert.True(errors.Detail.Length < 5_000, $"detail is {errors.Detail.Length} characters");
        Assert.EndsWith("999,900 more problems are not shown.", errors.Detail);
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
