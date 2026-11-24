using InventoryTracker.Api.Common.Errors;

namespace InventoryTracker.Api.Common.Validation;

/// <summary>
/// The size limits every request must respect, so that one request cannot tie up the server (security review SEC-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why limits are needed.</b> Every change to data runs inside one write transaction, and on SQLite only one write
/// transaction can run at a time (architecture 5.2). Before these limits, a single request with millions of order lines
/// was checked line by line inside that transaction, so every other user's save waited behind it for seconds, and the
/// error response listing every bad line grew to hundreds of megabytes. Four limits now bound the work of any request:
/// </para>
/// <list type="bullet">
/// <item><see cref="MaxBodyBytes"/>: the web server refuses a larger body with <c>413</c> before any of our code runs
/// (set in Program.cs).</item>
/// <item><see cref="MaxLines"/>: a body with more lines is refused with <c>400</c> <c>too-many-lines</c>, by
/// <see cref="CheckLineCount{T}"/>, before the write transaction begins.</item>
/// <item><see cref="MaxRoles"/>: a user body with more roles is refused with <c>400</c> <c>validation</c>, by
/// <see cref="CheckRoleCount{T}"/>, also before the write transaction (decision #125).</item>
/// <item><see cref="ValidationErrors.MaxMessages"/>: an error response lists at most that many problems.</item>
/// </list>
/// </remarks>
public static class RequestLimits
{
    /// <summary>
    /// The largest request body the API accepts: 1 MB (1,000,000 bytes). Kestrel's own default is 30 MB.
    /// </summary>
    /// <remarks>
    /// The largest legitimate body is a recipe, send or completion with <see cref="MaxLines"/> lines. One line such as
    /// <c>{"itemId":2147483647,"amountPerUnit":999999.999999},</c> is about 50 bytes, so 500 lines are about 25 KB, and
    /// about 40 KB even when the client indents its JSON. A reason or note of 500 characters is at most 6 KB, even
    /// written entirely as JSON escapes. 1 MB is more than twenty times the largest real body, and small enough that a
    /// refused body costs the server almost nothing to read.
    /// </remarks>
    public const long MaxBodyBytes = 1_000_000;

    /// <summary>
    /// <see cref="MaxBodyBytes"/> in megabytes, for messages and the OpenAPI document ("1 MB"). Keep the two in step.
    /// </summary>
    public const int MaxBodyMegabytes = (int)(MaxBodyBytes / 1_000_000);

    /// <summary>
    /// The most lines one request may carry: a recipe (AC-08.1), a send (AC-15.1), a completion (AC-19.1), a
    /// complementary request (AC-16.1), a complementary send (AC-18.1) or (v1.1) a new sales order (AC-24.5).
    /// </summary>
    /// <remarks>
    /// A real recipe lists each raw material of one product, which is usually a handful and rarely more than a few
    /// dozen. 500 leaves ten times that as headroom. The same cap applies to every line-carrying body on purpose: an
    /// order has exactly one line per recipe line (AC-13.1), and a complementary order a subset of them, so a correct
    /// send, completion or complementary body never has more lines than a recipe can have. (v1.1) Real sales orders have
    /// 1 to 10 lines (PRD section 5, architecture risk 19), so the same cap of 500 applies to them with room to spare.
    /// At 500 lines, checking a body inside the write transaction takes well under a millisecond.
    /// </remarks>
    public const int MaxLines = 500;

    /// <summary>
    /// The most roles one <c>POST /users</c> or <c>PUT /users/{id}/roles</c> body may list (decision #125).
    /// </summary>
    /// <remarks>
    /// There are only four roles (Warehouse, Production, Manager, Seller), so a real request lists at most four. Repeats are
    /// allowed and ignored (AC-03.3), so 10 leaves room for a client that repeats a name by mistake. Without a cap, 1 MB
    /// of <c>null</c> roles was checked one by one inside the write transaction of a role change, and made other users'
    /// saves wait up to 1.3 seconds (found after SEC-01).
    /// </remarks>
    public const int MaxRoles = 10;

    /// <summary>The <c>413</c> <c>request-too-large</c> message (design 7.2, decisions #122 and #124).</summary>
    public static readonly string BodyTooLargeMessage =
        $"The request body is too large. The limit is {MaxBodyMegabytes} MB.";

    /// <summary>The <c>400</c> <c>too-many-lines</c> message, under <c>lines</c> (design 7.2, decision #123).</summary>
    public static readonly string TooManyLinesMessage = $"At most {MaxLines} lines are allowed.";

    /// <summary>The <c>400</c> <c>validation</c> message, under <c>roles</c> (decision #125).</summary>
    public static readonly string TooManyRolesMessage = $"At most {MaxRoles} roles are allowed.";

    /// <summary>
    /// Refuses a body whose <c>lines</c> list is longer than <see cref="MaxLines"/>, with <c>400</c>
    /// <c>too-many-lines</c> under the path <c>lines</c>. A missing list passes: whether lines are required is the
    /// service's own check.
    /// </summary>
    /// <remarks>
    /// Services call this <em>before</em> <c>InWriteTransactionAsync</c>, so an oversized list is refused without ever
    /// taking the database's write lock and cannot make anyone else wait (SEC-01). It needs nothing from the database.
    /// The consequence is deliberate: this check comes before the <c>404</c> for the record in the URL, in the same way
    /// as a body that is not valid JSON does (design 2.2, decision #23).
    /// The rest of a body's checks stay inside the transaction, in the design 2.2 order (the <c>404</c> for the record
    /// in the URL comes first, and most of them need the record); with at most <see cref="MaxLines"/> lines they take
    /// well under a millisecond.
    /// </remarks>
    /// <typeparam name="T">The type of one line; any list counts.</typeparam>
    /// <param name="lines">The request body's <c>lines</c>.</param>
    /// <exception cref="ValidationException"><c>400</c> <c>too-many-lines</c>.</exception>
    public static void CheckLineCount<T>(IReadOnlyCollection<T>? lines) =>
        CheckCount(lines, MaxLines, "lines", TooManyLinesMessage, ErrorCodes.TooManyLines);

    /// <summary>
    /// Refuses a user body whose <c>roles</c> list is longer than <see cref="MaxRoles"/>, with <c>400</c>
    /// <c>validation</c> <see cref="TooManyRolesMessage"/> under the path <c>roles</c> (decision #125). A missing list
    /// passes: "At least one role is required." is the user service's own check.
    /// </summary>
    /// <remarks>
    /// Called first in <c>POST /users</c> and <c>PUT /users/{id}/roles</c>, before <c>InWriteTransactionAsync</c>, for the
    /// same reason as <see cref="CheckLineCount{T}"/>: an oversized list is refused without the write lock. On the role
    /// change it therefore also comes before the <c>404</c> for the user in the URL.
    /// </remarks>
    /// <typeparam name="T">The type of one entry; any list counts.</typeparam>
    /// <param name="roles">The request body's <c>roles</c>.</param>
    /// <exception cref="ValidationException"><c>400</c> <c>validation</c>.</exception>
    public static void CheckRoleCount<T>(IReadOnlyCollection<T>? roles) =>
        CheckCount(roles, MaxRoles, "roles", TooManyRolesMessage, ErrorCodes.Validation);

    /// <summary>
    /// The shared check behind both caps: throws one field error under <paramref name="path"/> when
    /// <paramref name="list"/> has more than <paramref name="max"/> entries. Only the count is read, so the cost is the
    /// same however long the list is.
    /// </summary>
    private static void CheckCount<T>(IReadOnlyCollection<T>? list, int max, string path, string message, string code)
    {
        if (list is not null && list.Count > max)
        {
            throw new ValidationException(message, new Dictionary<string, string[]> { [path] = [message] }, code);
        }
    }
}
