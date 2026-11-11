using InventoryTracker.Api.Auth;
using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Api.Tests.Infrastructure;

/// <summary>
/// Identity's real password hasher, except that it can hold one <em>dummy</em> password check open until the test lets
/// it go. Lets a test prove that the account's sign-in lock is not held during the dummy check (decision #62).
/// </summary>
/// <remarks>
/// Identity asks for <see cref="IPasswordHasher{TUser}"/> through dependency injection, so a test registers this class
/// in its place (<see cref="TestApiFactory.ReplaceServices"/>). Real checks pass the real user, who has an email; the
/// sign-in service's dummy check passes a blank <see cref="AppUser"/> with no email, which is how this class tells the
/// two apart without any test hook in the application code.
/// </remarks>
public sealed class PausingPasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _real = new();
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private int _pausesLeft;

    /// <summary>Completes when a paused dummy check has started and is waiting.</summary>
    public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>From now on, the next dummy check (and only that one) waits until <see cref="Release"/>.</summary>
    public void PauseNextDummyCheck() => Interlocked.Exchange(ref _pausesLeft, 1);

    /// <summary>Lets the paused dummy check finish.</summary>
    public void Release() => _release.Set();

    /// <inheritdoc />
    public string HashPassword(AppUser user, string password) => _real.HashPassword(user, password);

    /// <inheritdoc />
    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        if (user.Email is null && Interlocked.Exchange(ref _pausesLeft, 0) == 1)
        {
            Paused.TrySetResult();

            // A synchronous wait, like the real check's CPU work; the timeout keeps a broken test from hanging forever.
            _release.Wait(TimeSpan.FromSeconds(30));
        }

        return _real.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
