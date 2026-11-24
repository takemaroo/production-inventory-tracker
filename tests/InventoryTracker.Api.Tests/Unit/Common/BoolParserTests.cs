using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Features.Notifications;

namespace InventoryTracker.Api.Tests.Unit.Common;

/// <summary>
/// (v1.1) T-024, decision #161, architecture 4.1 item 19: the shared true/false query filter, used by <c>unreadOnly</c>
/// on notifications (decision #102a, unchanged) and <c>isActive</c> on customers (AC-29.6).
/// </summary>
public class BoolParserTests
{
    /// <summary>AC-29.6, decision #102a: exactly <c>true</c> or <c>false</c>, in any letter case, with no error.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("fAlSe", false)]
    public void True_and_false_are_read_in_any_letter_case(string value, bool expected)
    {
        var errors = new ValidationErrors();

        Assert.Equal(expected, BoolParser.ParseOptional(value, "isActive", errors));
        Assert.False(errors.HasErrors);
    }

    /// <summary>
    /// Decision #102a, architecture section 6: missing, empty or only spaces means "not set" (null), with no error. The
    /// caller decides what that means: no filter on customers, <c>false</c> on notifications.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    public void Missing_empty_or_spaces_is_not_set(string? value)
    {
        var errors = new ValidationErrors();

        Assert.Null(BoolParser.ParseOptional(value, "isActive", errors));
        Assert.False(errors.HasErrors);
    }

    /// <summary>
    /// Design 7.2, decision #102a: anything else, including surrounding spaces, numbers and other words, adds
    /// "{parameter} must be true or false." under the parameter, and returns null.
    /// </summary>
    [Theory]
    [InlineData(" true")]
    [InlineData("true ")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("truee")]
    public void Anything_else_adds_the_message_under_the_parameter(string value)
    {
        var errors = new ValidationErrors();

        Assert.Null(BoolParser.ParseOptional(value, "isActive", errors));

        // The collector holds what the 400 will show: the sentence as the detail, and once under the parameter's key.
        Assert.Equal("isActive must be true or false.", errors.Detail);
        Assert.Equal(["isActive"], errors.ToDictionary().Keys);
        Assert.Equal(["isActive must be true or false."], errors.ToDictionary()["isActive"]);
    }

    /// <summary>
    /// Decision #161: notifications kept their message when the parsing moved here. The sentence built for
    /// <c>unreadOnly</c> is the one the notification tests check, typed out.
    /// </summary>
    [Fact]
    public void Message_names_the_parameter()
    {
        Assert.Equal("isActive must be true or false.", BoolParser.Message("isActive"));
        Assert.Equal("unreadOnly must be true or false.", BoolParser.Message("unreadOnly"));
        Assert.Equal(NotificationService.UnreadOnlyMessage, BoolParser.Message("unreadOnly"));
    }
}
