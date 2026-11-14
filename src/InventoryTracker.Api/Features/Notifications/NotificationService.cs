using System.Linq.Expressions;
using System.Text.Json;
using InventoryTracker.Api.Common.Errors;
using InventoryTracker.Api.Common.Paging;
using InventoryTracker.Api.Common.Time;
using InventoryTracker.Api.Common.Validation;
using InventoryTracker.Api.Data;
using InventoryTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Api.Features.Notifications;

/// <summary>
/// Reading notifications and marking them read (US-23): the list, one notification, mark one read, mark all read.
/// Every method works for one Manager, the caller, whose user id the controller takes from the token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read status is per Manager</b> (AC-23.3, decision #7). A notification is one shared row; "Manager X has read it"
/// is a separate <see cref="NotificationRead"/> row keyed by (notification, user) (architecture 3.2). So
/// <c>isRead</c> is worked out per request for the caller only, and marking read adds a row for the caller only, which
/// cannot change what any other Manager sees. A Manager created later has no rows at all, so every older notification
/// is unread for them (AC-23.4, PRD assumption 16) with no extra code.
/// </para>
/// <para>
/// Notifications are never changed or deleted here (AC-23.5); they are created by the events that raise them
/// (completion, complementary request, cancellation) through <see cref="NotificationComposer"/>.
/// </para>
/// </remarks>
public sealed class NotificationService(AppDbContext db, Clock clock)
{
    /// <summary>
    /// The <c>400</c> message for an <c>unreadOnly</c> value that is not a boolean (design 2.5: a filter value in the
    /// wrong format is <c>400</c>). The design gives no wording for it; this follows its other filter messages, such as
    /// "status must be Created, InProduction, Completed or Cancelled." (decision #102).
    /// </summary>
    public const string UnreadOnlyMessage = "unreadOnly must be true or false.";

    /// <summary>
    /// AC-23.1: one page of notifications, newest first, each with <c>isRead</c> for <paramref name="userId"/>. With
    /// <paramref name="unreadOnly"/> <c>true</c>, only the ones that Manager has not read, so <c>totalCount</c> is
    /// their unread count (design 3.11). <c>400</c> <c>validation</c> for paging outside its limits or an
    /// <c>unreadOnly</c> that is not <c>true</c> or <c>false</c>.
    /// </summary>
    public async Task<PagedResult<NotificationResponse>> ListAsync(
        PageQuery page, string? unreadOnly, string userId, CancellationToken cancellationToken)
    {
        // Design 2.2 step 4: check the whole query first, so one 400 lists every problem (design 6).
        var errors = new ValidationErrors();
        page.Validate(errors);
        var onlyUnread = ParseUnreadOnly(unreadOnly, errors);
        errors.ThrowIfAny();

        var query = db.Notifications.AsQueryable();

        // AC-23.1: "unread" means this Manager has no NotificationRead row for it (architecture 3.2). EF Core turns
        // Any(...) into a NOT EXISTS sub-query, which the (NotificationId, UserId) primary key answers directly.
        if (onlyUnread)
        {
            query = query.Where(notification => !notification.Reads.Any(read => read.UserId == userId));
        }

        // PRD 4.1, decision #17: newest first, like every list, not unread first. One completion raises its
        // notifications in the same second, so the id breaks the tie (newest id first); without it the order of equal
        // times, and so which rows land on which page, would be up to the database. (SQLite happens to return ties in
        // this order anyway, because its CreatedAt index keeps the row id with each entry; PostgreSQL makes no such
        // promise, so the tiebreak is written out rather than relied on.)
        var rows = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Select(AsRow(userId))
            .ToPagedResultAsync(page, cancellationToken);

        return new PagedResult<NotificationResponse>(
            rows.Items.Select(ToResponse).ToList(), rows.Page, rows.PageSize, rows.TotalCount);
    }

    /// <summary>
    /// AC-23.1: one notification, in the same shape as a list row, with <c>isRead</c> for <paramref name="userId"/>,
    /// or <c>404</c> "Notification {id} not found." (AC-23.5).
    /// </summary>
    public async Task<NotificationResponse> GetAsync(int id, string userId, CancellationToken cancellationToken)
    {
        var row = await db.Notifications
            .Where(notification => notification.Id == id)
            .Select(AsRow(userId))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NotFound(id);

        return ToResponse(row);
    }

    /// <summary>
    /// AC-23.3: marks one notification read for <paramref name="userId"/> only. Idempotent: marking a notification the
    /// Manager has already read succeeds and changes nothing (design 7.2), so its first read time is kept.
    /// <c>404</c> "Notification {id} not found." for an unknown id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check "is there already a row?" and the insert run in one write transaction (architecture 5.2). On SQLite
    /// that transaction holds the database's single write lock from its start (<c>BEGIN IMMEDIATE</c>), so two
    /// simultaneous marks by the same Manager run one after the other: the second sees the first one's row and does
    /// nothing, and both answer <c>204</c>. The composite primary key is therefore never hit in normal running.
    /// </para>
    /// <para>
    /// If it ever were (a write that bypassed the transaction rule), the shared exception handler would turn the
    /// primary-key error into <c>409</c> <c>concurrent-change</c>, never a <c>500</c> (architecture 4.1 item 11). An
    /// "insert or ignore" statement would avoid even that, but it needs hand-written SQL that differs between SQLite and
    /// PostgreSQL, for a case the transaction already rules out.
    /// </para>
    /// </remarks>
    public async Task MarkReadAsync(int id, string userId, CancellationToken cancellationToken)
    {
        await db.InWriteTransactionAsync(async () =>
        {
            // Design 2.2 step 3, architecture 5.1: the record named in the URL first.
            if (!await db.Notifications.AnyAsync(notification => notification.Id == id, cancellationToken))
            {
                throw NotFound(id);
            }

            // Design 7.2: "Marking an already-read notification read again succeeds (204)". Nothing to add.
            var alreadyRead = await db.NotificationReads
                .AnyAsync(read => read.NotificationId == id && read.UserId == userId, cancellationToken);
            if (alreadyRead)
            {
                return;
            }

            // AC-23.3: a row for this Manager only; other Managers' read status is in their own rows.
            // InWriteTransactionAsync saves the added row before it commits.
            db.NotificationReads.Add(new NotificationRead { NotificationId = id, UserId = userId, ReadAt = clock.UtcNow });
        }, cancellationToken);
    }

    /// <summary>
    /// AC-23.3: marks every notification that exists now as read for <paramref name="userId"/> only. Notifications
    /// the Manager had already read keep their first read time; notifications raised later are unread. Succeeds even
    /// when there is nothing to mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one SQL statement instead of a loop.</b> Notifications are never deleted (AC-23.5), so after a year there
    /// can be thousands, and a new Manager's first read-all must add a read record for every one of them. Adding them
    /// as entities would make EF Core send one <c>INSERT</c> per record (in batches, but still one row each, plus the
    /// round trip that first loads every unread id into memory). All of that happens while this request holds SQLite's
    /// single write lock (architecture 5.2), so every other write in the app (sends, completions, receipts) waits for
    /// it: the review measured 420 to 630 ms for 5,007 notifications. <c>INSERT ... SELECT</c> lets the database find
    /// the unread notifications and add their records itself, in one statement, without sending any rows back and
    /// forth, so the lock is held for as short a time as possible.
    /// </para>
    /// <para>
    /// The statement is plain standard SQL that SQLite and PostgreSQL both run unchanged. The table and column names
    /// are the ones the entity configurations give (<c>NotificationReadConfiguration</c>,
    /// <c>NotificationConfiguration</c>: table names set there, column names equal to the property names); each is
    /// quoted so PostgreSQL keeps its capital letters. The read-all tests fail if they ever drift.
    /// </para>
    /// <para>
    /// It runs inside the same write transaction as every change (architecture 5.2): EF Core sends raw SQL on the
    /// transaction already open on the context. So no notification can be raised between choosing the unread ones
    /// and adding their records, and two simultaneous calls (or a call racing a single mark) run one after the other
    /// and never add the same record twice (see <see cref="MarkReadAsync"/>).
    /// </para>
    /// <para>
    /// <b>No 409 backstop here, and why none is needed.</b> Raw SQL does not go through <c>SaveChanges</c>, so a
    /// primary-key violation would arrive as a plain <c>SqliteException</c>, not the <c>DbUpdateException</c> that the
    /// shared handler turns into <c>409</c> <c>concurrent-change</c> (architecture 4.1 item 11): it would be a
    /// <c>500</c>. The statement cannot hit the (NotificationId, UserId) key, for three reasons. The <c>SELECT</c> yields
    /// each notification once, because <c>Id</c> is the primary key of <c>Notifications</c>. <c>NOT EXISTS</c> skips
    /// every notification this Manager already has a record for, checked within the same statement. And the write
    /// transaction keeps writers one at a time: <c>BEGIN IMMEDIATE</c> on SQLite, and on PostgreSQL later the advisory
    /// lock of architecture 5.2. So no other request can add one of this Manager's records while the statement runs.
    /// </para>
    /// </remarks>
    public async Task MarkAllReadAsync(string userId, CancellationToken cancellationToken)
    {
        await db.InWriteTransactionAsync(async () =>
        {
            var now = clock.UtcNow;

            // ExecuteSqlAsync takes an interpolated string as a FormattableString: EF Core turns each {value} into a
            // database parameter (@p0, @p1...) rather than pasting the text into the SQL, so a user id can never change
            // the statement (no SQL injection). The DateTime parameter is stored in the same text form EF Core uses
            // for ReadAt everywhere else.
            //
            // AC-23.3: NOT EXISTS skips notifications this Manager has already read, so those keep their first read
            // time; notifications raised after this statement have no record and stay unread. Only this Manager's
            // records are added, so no other Manager's read status changes.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "NotificationReads" ("NotificationId", "UserId", "ReadAt")
                SELECT n."Id", {userId}, {now}
                FROM "Notifications" AS n
                WHERE NOT EXISTS (
                    SELECT 1 FROM "NotificationReads" AS r
                    WHERE r."NotificationId" = n."Id" AND r."UserId" = {userId})
                """, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Design 5.10: <c>unreadOnly</c> is <c>true</c> or <c>false</c>, default <c>false</c>. Missing or empty means the
    /// default. Any letter case is accepted, like enum values (design 2.3, <c>EnumParser</c>), but nothing else:
    /// surrounding spaces, numbers and other words add <see cref="UnreadOnlyMessage"/> (design 2.5: a filter value in
    /// the wrong format is <c>400</c>; decision #102).
    /// </summary>
    /// <remarks>
    /// The value arrives as text rather than as a <c>bool</c> parameter so the error is this message, in the style of
    /// the design's other filter messages, rather than model binding's generic "The value 'yes' is not valid".
    /// </remarks>
    private static bool ParseUnreadOnly(string? value, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // An exact comparison, ignoring only letter case. bool.TryParse is not used because it also accepts
        // surrounding spaces (" true "), which enum filters do not.
        if (string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, bool.FalseString, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        errors.Add("unreadOnly", UnreadOnlyMessage);
        return false;
    }

    /// <summary>Design 7.2, AC-23.5: the <c>404</c> for an unknown notification id.</summary>
    private static NotFoundException NotFound(int id) => new($"Notification {id} not found.");

    /// <summary>
    /// Turns a row read from the database into the response. The JSON parsing happens here, in memory, because the
    /// database cannot produce a <see cref="JsonElement"/>; the query only reads the stored text.
    /// </summary>
    private static NotificationResponse ToResponse(NotificationRow row) => new(
        row.Id,
        row.Type,
        row.OrderNumber,
        row.ComplementaryOrderNumber,
        row.CreatedAt,
        row.Message,
        row.IsRead,
        // Deserializing to JsonElement parses the stored text into a stand-alone element that the response can keep
        // after this method returns. Nothing is renamed, re-rounded or reordered: the client gets the stored values.
        JsonSerializer.Deserialize<JsonElement>(row.DataJson));

    /// <summary>
    /// The one projection from a stored notification to a <see cref="NotificationRow"/>, shared by the list and the
    /// single read so a row looks the same in both. It is an <see cref="Expression{TDelegate}"/>, not a compiled
    /// method, so EF Core can translate it into SQL: the columns are read directly and <c>IsRead</c> becomes an
    /// <c>EXISTS</c> sub-query for <paramref name="userId"/>, sent as a parameter (never pasted into the SQL text).
    /// </summary>
    private static Expression<Func<Notification, NotificationRow>> AsRow(string userId) => notification =>
        new NotificationRow(
            notification.Id,
            notification.Type,
            notification.ProductionOrderNumber,
            notification.ComplementaryOrderNumber,
            notification.CreatedAt,
            notification.Message,
            notification.Reads.Any(read => read.UserId == userId),
            notification.DataJson);

    /// <summary>
    /// What the query reads for one notification: its columns plus <c>IsRead</c>, computed in SQL for the caller.
    /// </summary>
    private sealed record NotificationRow(
        int Id,
        NotificationType Type,
        int OrderNumber,
        int? ComplementaryOrderNumber,
        DateTime CreatedAt,
        string Message,
        bool IsRead,
        string DataJson);
}
