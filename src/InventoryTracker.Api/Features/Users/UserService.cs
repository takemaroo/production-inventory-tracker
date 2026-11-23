using System.ComponentModel.DataAnnotations;
using InventoryTracker.Api.Auth;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Users;

/// <summary>
/// Every rule of user management (US-03): creating users, listing them, changing roles, deactivating and
/// reactivating. Passwords are handled by <see cref="PasswordService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>How roles are stored.</b> ASP.NET Core Identity keeps roles in two tables. <c>AspNetRoles</c> has one row per
/// role (Warehouse, Production, Manager, Seller; created at start-up by <see cref="StartupChecks"/>). <c>AspNetUserRoles</c>
/// has one row per (user, role) pair, so a user with two roles has two rows (decision #8). <c>UserManager</c>'s
/// <c>AddToRolesAsync</c>, <c>RemoveFromRolesAsync</c> and <c>GetRolesAsync</c> read and write those rows.
/// </para>
/// <para>
/// <b>Why a role change waits for the next sign-in (AC-03.5, PRD assumption 15).</b> At sign-in the roles are
/// <em>copied</em> into the token as <c>role</c> claims (<see cref="JwtTokenService"/>). <c>[Authorize(Roles = ...)]</c>
/// looks only at those claims, never at the tables, so a token keeps the roles it was issued with until it expires
/// (8 hours) or the user signs in again. The per-request check (<see cref="TokenUserCheck"/>) would end the token
/// only if the user's security stamp changed, and a role change does not change it: Identity's
/// <c>AddToRolesAsync</c>/<c>RemoveFromRolesAsync</c> save the user row but leave <c>SecurityStamp</c> alone
/// (Identity 10.0.15; <c>UsersTests</c> proves it). To cut someone's access off at once, a Manager deactivates them.
/// </para>
/// <para>
/// Every change runs inside <c>db.InWriteTransactionAsync</c> (architecture 5.2). Identity's EF Core store saves
/// through the same request-scoped <c>AppDbContext</c>, so its writes join that transaction: a refusal half-way
/// through (for example the last-Manager rule) leaves nothing saved, and two simultaneous requests are handled one
/// after the other, each seeing the other's result. The one exception is the slow password hash of a new user, made
/// before the transaction opens so it does not hold up other writes (decision #70, see <see cref="PasswordService"/>).
/// </para>
/// </remarks>
public sealed class UserService(
    AppDbContext db, UserManager<AppUser> userManager, Clock clock, ILogger<UserService> logger)
{
    /// <summary>Design 7.2, Users: the code for AC-03.2.</summary>
    public const string EmailTakenCode = "email-taken";

    /// <summary>AC-03.2, exact PRD wording.</summary>
    public const string EmailTakenMessage = "A user with this email already exists.";

    /// <summary>Design 7.2, Users: the code for AC-03.8.</summary>
    public const string LastManagerCode = "last-manager";

    /// <summary>AC-03.8, exact PRD wording.</summary>
    public const string LastManagerMessage = "At least one active Manager is required.";

    /// <summary>Design 7.2, Users: an unknown user id in the URL.</summary>
    public const string UserNotFoundMessage = "User not found.";

    /// <summary>Design 7.2, Users (AC-03.3): bad email format, including an email with a space in it.</summary>
    public const string InvalidEmailMessage = "Email is not a valid email address.";

    /// <summary>Design 7.2, Users (AC-03.3, AC-03.5): an empty or missing role list.</summary>
    public const string RolesRequiredMessage = "At least one role is required.";

    /// <summary>
    /// Architecture 3.2: Identity's <c>Email</c> and <c>UserName</c> columns hold at most 256 characters. Not in the
    /// design; written in the pattern of its other length messages.
    /// </summary>
    public const int MaxEmailLength = 256;

    /// <summary>
    /// The same check Identity's own user validator uses: exactly one <c>@</c>, not at the start or the end. It
    /// accepts spaces, which is why <see cref="CleanEmail"/> refuses them separately (decision #64).
    /// </summary>
    private static readonly EmailAddressAttribute EmailFormat = new();

    /// <summary>
    /// <c>GET /users</c> (AC-03.4): one page of users, newest first (PRD 4.1), with email, roles and active status.
    /// </summary>
    public async Task<PagedResult<UserResponse>> ListAsync(PageQuery page, CancellationToken cancellationToken)
    {
        page.ThrowIfInvalid();

        // Only the fields of the response are selected, never PasswordHash or SecurityStamp (AC-03.4).
        // Design 2.5: newest first, then by id descending so users created in the same second keep a stable order.
        var rows = await db.Users.AsNoTracking()
            .OrderByDescending(user => user.CreatedAt)
            .ThenByDescending(user => user.Id)
            .Select(user => new { user.Id, user.Email, user.IsActive, user.CreatedAt })
            .ToPagedResultAsync(page, cancellationToken);

        // One query for the roles of every user on the page, rather than one query per user.
        var rolesByUser = await RolesByUserAsync(rows.Items.Select(row => row.Id).ToList(), cancellationToken);

        var items = rows.Items
            .Select(row => new UserResponse(row.Id, row.Email!, Roles.InStandardOrder(rolesByUser[row.Id]), row.IsActive, row.CreatedAt))
            .ToList();
        return new PagedResult<UserResponse>(items, rows.Page, rows.PageSize, rows.TotalCount);
    }

    /// <summary><c>GET /users/{id}</c>: one user, or <c>404</c> "User not found.".</summary>
    public async Task<UserResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        // A plain read: no transaction, nothing tracked, and only the response's fields (AC-03.4).
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => new { candidate.Id, candidate.Email, candidate.IsActive, candidate.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(UserNotFoundMessage);

        var roles = (await RolesByUserAsync([id], cancellationToken))[id];
        return new UserResponse(user.Id, user.Email!, Roles.InStandardOrder(roles), user.IsActive, user.CreatedAt);
    }

    /// <summary>
    /// <c>POST /users</c> (AC-03.1 to AC-03.3): creates an active user with an initial password and one or more roles.
    /// More than <see cref="RequestLimits.MaxRoles"/> roles are refused first, with <c>400</c> <c>validation</c>
    /// (decision #125).
    /// </summary>
    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        // Decision #125: more than RequestLimits.MaxRoles roles is refused first, in a response of its own (no other field
        // is checked), so a huge list is never checked entry by entry.
        RequestLimits.CheckRoleCount(request.Roles);

        // Design 2.2 step 4: the input shape. Every other field problem is reported together in one response. These
        // checks look only at the request, not at the database, so they can run before the write transaction opens.
        var errors = new ValidationErrors();
        var email = CleanEmail(request.Email, errors);
        if (string.IsNullOrEmpty(request.Password))
        {
            // Not trimmed: spaces may be part of a password.
            errors.Add("password", PasswordService.PasswordRequiredMessage);
        }

        var roles = ParseRoles(request.Roles, errors);
        errors.ThrowIfAny();

        // Decision #64, architecture 3.2: UserName is set to exactly the same trimmed text as Email. Identity's
        // index on NormalizedEmail is not unique, but its index on NormalizedUserName is. Because the two are
        // always equal, that unique index makes "one account per email, ignoring case" (AC-03.2) a database rule,
        // which holds even if two creates for the same email arrive at the same moment.
        var user = new AppUser { UserName = email, Email = email, IsActive = true };

        // AC-03.3: the password policy is still an input check (400), so it comes before the duplicate check (409).
        await PasswordService.ThrowIfWeakAsync(userManager, user, request.Password!, "password");

        // Decision #70: the salted hash (never the password itself, see SignInService) takes about 45 ms to make, so
        // it is made here, before the transaction, where it does not hold up other writes (see PasswordService).
        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, request.Password!);

        return await db.InWriteTransactionAsync(async () =>
        {
            // AC-03.2: FindByNameAsync compares the upper-cased NormalizedUserName, so "Ana@Example.com" and
            // " ana@example.com " (trimmed above) are the same account.
            if (await userManager.FindByNameAsync(email!) is not null)
            {
                throw new ConflictException(EmailTakenCode, EmailTakenMessage);
            }

            // Taken inside the transaction, so creation times follow the order in which creates are saved (newest
            // first, PRD 4.1).
            user.CreatedAt = clock.UtcNow;

            // This CreateAsync overload takes no password: the hash is already set. It validates the user again,
            // gives them a fresh security stamp and saves the row.
            var created = await userManager.CreateAsync(user);
            if (created.Errors.Any(error => error.Code is nameof(IdentityErrorDescriber.DuplicateUserName)
                    or nameof(IdentityErrorDescriber.DuplicateEmail)))
            {
                throw new ConflictException(EmailTakenCode, EmailTakenMessage);
            }

            ThrowIfFailed(created, "create a user");
            ThrowIfFailed(await userManager.AddToRolesAsync(user, roles), "give a new user their roles");

            logger.LogInformation("User {UserId} created with roles {Roles}.", user.Id, string.Join(",", roles));
            return new UserResponse(user.Id, user.Email!, roles, user.IsActive, user.CreatedAt);
        }, cancellationToken);
    }

    /// <summary>
    /// <c>PUT /users/{id}/roles</c> (AC-03.5): replaces the user's roles with the given set (at least one). The change
    /// applies from the user's next sign-in; tokens they already hold keep their old roles until they expire.
    /// More than <see cref="RequestLimits.MaxRoles"/> roles are refused first, with <c>400</c> <c>validation</c>
    /// (decision #125).
    /// </summary>
    public Task<UserResponse> SetRolesAsync(string id, SetRolesRequest request, CancellationToken cancellationToken)
    {
        // Decision #125: the number of roles is checked first, outside the write transaction, so a huge list is refused
        // without taking SQLite's single write lock and never makes another user's save wait. It therefore comes before
        // the 404 for the user in the URL. The other role checks stay inside, in the design 2.2 order (404 first); with
        // at most RequestLimits.MaxRoles roles they take microseconds.
        RequestLimits.CheckRoleCount(request.Roles);

        return db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 order: the record in the URL (404), then the input (400), then the state (409).
            var user = await FindOrThrowAsync(id);

            var errors = new ValidationErrors();
            var roles = ParseRoles(request.Roles, errors);
            errors.ThrowIfAny();

            var current = await userManager.GetRolesAsync(user);
            var toRemove = current.Except(roles, StringComparer.Ordinal).ToList();
            var toAdd = roles.Except(current, StringComparer.Ordinal).ToList();

            // AC-03.8: an inactive Manager does not count as one, so only an active user losing Manager is checked.
            if (user.IsActive && toRemove.Contains(Roles.Manager))
            {
                await ThrowIfLastActiveManagerAsync(user);
            }

            // Only the differences are written. These calls insert or delete AspNetUserRoles rows and save the user
            // row; they do not change the security stamp, so the user's existing tokens keep working (AC-03.5).
            if (toRemove.Count > 0)
            {
                ThrowIfFailed(await userManager.RemoveFromRolesAsync(user, toRemove), "remove roles");
            }

            if (toAdd.Count > 0)
            {
                ThrowIfFailed(await userManager.AddToRolesAsync(user, toAdd), "add roles");
            }

            if (toRemove.Count > 0 || toAdd.Count > 0)
            {
                logger.LogInformation("Roles of user {UserId} set to {Roles}.", user.Id, string.Join(",", roles));
            }

            return new UserResponse(user.Id, user.Email!, roles, user.IsActive, user.CreatedAt);
        }, cancellationToken);
    }

    /// <summary>
    /// <c>POST /users/{id}/deactivate</c> (AC-03.6): the user can no longer sign in, and every token issued to them so
    /// far is refused with <c>401</c> from the next request on. Repeating it changes nothing (design 5.2).
    /// </summary>
    public Task<UserResponse> DeactivateAsync(string id, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var user = await FindOrThrowAsync(id);
            if (!user.IsActive)
            {
                return await ToResponseAsync(user); // design 5.2: already inactive, nothing changes
            }

            // AC-03.8: deactivating the last active Manager would leave nobody able to manage users. Design 9: a
            // Manager may deactivate themselves, as long as another active Manager remains.
            var roles = Roles.InStandardOrder(await userManager.GetRolesAsync(user));
            if (roles.Contains(Roles.Manager))
            {
                await ThrowIfLastActiveManagerAsync(user);
            }

            // The security stamp is a random value Identity keeps on each user row, and every token carries a copy of
            // the stamp it was issued with (the "stamp" claim). TokenUserCheck compares the two on every request. Giving
            // the user a new stamp therefore ends every token issued before this moment, all at once, with no list of
            // tokens to keep. IsActive = false alone would already refuse the tokens, but the new stamp keeps them
            // refused after a later reactivation too (architecture 4.1 item 6): the user must sign in again.
            // UpdateSecurityStampAsync saves the whole user row, so IsActive is saved in the same write.
            user.IsActive = false;
            ThrowIfFailed(await userManager.UpdateSecurityStampAsync(user), "deactivate a user");

            logger.LogInformation("User {UserId} deactivated.", user.Id);
            return new UserResponse(user.Id, user.Email!, roles, user.IsActive, user.CreatedAt);
        }, cancellationToken);

    /// <summary>
    /// <c>POST /users/{id}/reactivate</c> (AC-03.6): the user can sign in again. Tokens from before the deactivation
    /// stay invalid, because deactivation changed the security stamp. Repeating it changes nothing (design 5.2).
    /// </summary>
    public Task<UserResponse> ReactivateAsync(string id, CancellationToken cancellationToken) =>
        db.InWriteTransactionAsync(async () =>
        {
            var user = await FindOrThrowAsync(id);
            if (!user.IsActive)
            {
                user.IsActive = true;
                ThrowIfFailed(await userManager.UpdateAsync(user), "reactivate a user");
                logger.LogInformation("User {UserId} reactivated.", user.Id);
            }

            return await ToResponseAsync(user);
        }, cancellationToken);

    /// <summary>
    /// Design 2.6 and decision #64: trims the email and checks it. Adds a message to <paramref name="errors"/> and
    /// returns the trimmed email (or null when it is missing).
    /// </summary>
    private static string? CleanEmail(string? value, ValidationErrors errors)
    {
        var email = Text.Clean(value);
        if (email is null)
        {
            errors.Add("email", "Email is required.");
        }
        // Decision #116: counted in Unicode code points, so an emoji is one character (Text.Length).
        else if (Text.Length(email) > MaxEmailLength)
        {
            errors.Add("email", $"Email must be {MaxEmailLength} characters or fewer.");
        }
        else if (email.Any(char.IsWhiteSpace) || !EmailFormat.IsValid(email))
        {
            // Decision #64: a space left inside the email ("ana smith@example.com") is refused. Identity would accept
            // it, and the user name must be a clean copy of the email.
            errors.Add("email", InvalidEmailMessage);
        }

        return email;
    }

    /// <summary>
    /// AC-03.3 and design 2.3: reads the role names in any letter case. An empty list and each unknown name add the
    /// design 7.2 message. Returns the valid roles without repeats, in the order Warehouse, Production, Manager, Seller.
    /// </summary>
    private static IReadOnlyList<string> ParseRoles(IReadOnlyList<string?>? values, ValidationErrors errors)
    {
        if (values is null || values.Count == 0)
        {
            errors.Add("roles", RolesRequiredMessage);
            return [];
        }

        var roles = new List<string>();
        for (var index = 0; index < values.Count; index++)
        {
            var match = Roles.All.FirstOrDefault(role => string.Equals(role, values[index], StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // AC-30.1, design 7.2: the message lists all four roles. It is typed out rather than built from
                // Roles.All, so the sentence reads exactly as the design writes it ("... Manager and Seller.").
                errors.Add($"roles[{index}]", $"Unknown role '{values[index]}'. Roles are Warehouse, Production, Manager and Seller.");
            }
            else
            {
                roles.Add(match);
            }
        }

        return Roles.InStandardOrder(roles);
    }

    /// <summary>
    /// AC-03.8: refuses the change when no active Manager other than <paramref name="user"/> exists. Called inside the
    /// write transaction, so two simultaneous requests (each removing a different one of the last two Managers) are
    /// handled one after the other, and the second one sees that only one Manager is left.
    /// </summary>
    private async Task ThrowIfLastActiveManagerAsync(AppUser user)
    {
        // AC-30.5: only the Manager role counts. A Seller (or a user with any other role but Manager) is not a Manager.
        var anotherActiveManager = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                join other in db.Users on userRole.UserId equals other.Id
                where role.Name == Roles.Manager && other.IsActive && other.Id != user.Id
                select other.Id)
            .AnyAsync();

        if (!anotherActiveManager)
        {
            throw new ConflictException(LastManagerCode, LastManagerMessage);
        }
    }

    /// <summary>
    /// The role names of each of the given users, read from <c>AspNetUserRoles</c> joined to <c>AspNetRoles</c> in one
    /// query. A user without roles gets an empty list from the lookup.
    /// </summary>
    private async Task<ILookup<string, string>> RolesByUserAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var roleRows = await (
                from userRole in db.UserRoles
                join role in db.Roles on userRole.RoleId equals role.Id
                where ids.Contains(userRole.UserId)
                select new { userRole.UserId, role.Name })
            .ToListAsync(cancellationToken);
        return roleRows.ToLookup(row => row.UserId, row => row.Name!);
    }

    /// <summary>Loads the user named in the URL, tracked so it can be changed, or throws <c>404</c> "User not found.".</summary>
    private async Task<AppUser> FindOrThrowAsync(string id) =>
        await userManager.FindByIdAsync(id) ?? throw new NotFoundException(UserNotFoundMessage);

    /// <summary>Builds the design 5.2 user object from the row and the roles currently stored for it.</summary>
    private async Task<UserResponse> ToResponseAsync(AppUser user) =>
        new(user.Id, user.Email!, Roles.InStandardOrder(await userManager.GetRolesAsync(user)), user.IsActive, user.CreatedAt);

    /// <summary>
    /// Identity reports problems as a result instead of throwing. Every expected failure has been checked before the
    /// call, so any other failure is a bug or an outage: it becomes a <c>500</c>, the transaction rolls back, and the
    /// details go only to the log.
    /// </summary>
    internal static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: {string.Join(", ", result.Errors.Select(error => error.Code))}");
        }
    }
}
