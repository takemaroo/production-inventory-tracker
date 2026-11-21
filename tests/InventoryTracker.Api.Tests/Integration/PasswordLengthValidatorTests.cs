using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Api.Tests.Integration;

/// <summary>
/// Decision #118, PRD section 5: <see cref="PasswordLengthValidator"/> counts the 8-character password minimum in
/// Unicode code points. It runs against the app's real <see cref="UserManager{TUser}"/>, so these tests also prove that
/// it is registered next to Identity's own validator and reads the configured minimum and error text.
/// </summary>
/// <remarks>
/// The endpoint and start-up tests for the same rule are in <c>UsersTests</c> and <c>StartupTests</c>, next to the
/// other password-policy cases.
/// </remarks>
public class PasswordLengthValidatorTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    /// <summary>"Aa1" and three cheese emoji: 6 characters, but 9 UTF-16 code units, so Identity's own rule passes it.</summary>
    private const string SixWithEmoji = "Aa1\U0001F9C0\U0001F9C0\U0001F9C0";

    /// <summary>"Aa1" and five cheese emoji: exactly 8 characters (13 code units).</summary>
    private const string EightWithEmoji = "Aa1\U0001F9C0\U0001F9C0\U0001F9C0\U0001F9C0\U0001F9C0";

    /// <summary>
    /// Decision #118: 6 code points is refused with Identity's own too-short error, the same code and text Identity's
    /// length rule gives ("Passwords must be at least 8 characters.").
    /// </summary>
    [Fact]
    public async Task Six_characters_with_emoji_are_too_short()
    {
        var result = await ValidateWithAsync<PasswordLengthValidator>(SixWithEmoji);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(IdentityErrorDescriber.PasswordTooShort), error.Code);
        Assert.Equal("Passwords must be at least 8 characters.", error.Description);
    }

    /// <summary>Decision #118 at the minimum: 8 code points with emoji is long enough.</summary>
    [Fact]
    public async Task Eight_characters_with_emoji_are_long_enough()
    {
        Assert.True((await ValidateWithAsync<PasswordLengthValidator>(EightWithEmoji)).Succeeded);
    }

    /// <summary>PRD section 5: the ASCII boundary is unchanged: 7 letters and digits are too short, 8 are enough.</summary>
    [Theory]
    [InlineData("Short1a", false)]
    [InlineData("Abcdefg1", true)]
    public async Task Ascii_boundary_is_unchanged(string password, bool expected)
    {
        Assert.Equal(expected, (await ValidateWithAsync<PasswordLengthValidator>(password)).Succeeded);
    }

    /// <summary>
    /// Decision #118: why the validator is needed. Identity's own <see cref="PasswordValidator{TUser}"/> accepts the
    /// 6-character password (it counts 9 code units), and the full set the app runs refuses it.
    /// </summary>
    [Fact]
    public async Task Identity_alone_accepts_six_emoji_characters_and_the_registered_set_refuses_them()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser();

        Assert.Contains(users.PasswordValidators, validator => validator is PasswordValidator<AppUser>);
        Assert.Contains(users.PasswordValidators, validator => validator is PasswordLengthValidator);
        Assert.True((await ValidateWithAsync<PasswordValidator<AppUser>>(SixWithEmoji)).Succeeded);

        var results = new List<IdentityResult>();
        foreach (var validator in users.PasswordValidators)
        {
            results.Add(await validator.ValidateAsync(users, user, SixWithEmoji));
        }

        Assert.Contains(results, result => !result.Succeeded);
    }

    /// <summary>Runs the registered validator of type <typeparamref name="TValidator"/> on <paramref name="password"/>.</summary>
    private async Task<IdentityResult> ValidateWithAsync<TValidator>(string password)
        where TValidator : IPasswordValidator<AppUser>
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var validator = users.PasswordValidators.OfType<TValidator>().Single();
        return await validator.ValidateAsync(users, new AppUser(), password);
    }
}
