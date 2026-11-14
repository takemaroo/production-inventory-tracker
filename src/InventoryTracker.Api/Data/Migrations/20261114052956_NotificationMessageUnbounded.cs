using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Api.Data.Migrations
{
    /// <summary>
    /// T-011, decision #63: removes the 1,000-character limit on <c>Notifications.Message</c>, so a notification that
    /// lists every material of a large order is stored whole, never truncated (architecture 3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <em>migration</em> is a recorded step of schema change. EF Core compares the model in code with the model
    /// snapshot (<c>AppDbContextModelSnapshot</c>, the model as of the last migration) and writes the difference as
    /// <c>Up</c> (apply) and <c>Down</c> (undo) operations. At start-up the app applies every migration the database
    /// has not had yet, in order, and records each one in the <c>__EFMigrationsHistory</c> table.
    /// </para>
    /// <para>
    /// <b>Why <c>Up</c> and <c>Down</c> are empty.</b> EF Core's SQLite provider declares every string column as
    /// <c>TEXT</c>, with or without a maximum length, and SQLite never enforces a length, so there is no column to
    /// change: the column was declared <c>TEXT</c> before and is declared <c>TEXT</c> after. The provider therefore
    /// generates no operation. (SQLite does keep each column's declared type; it simply does not enforce a length.)
    /// The migration is still needed: it moves the model snapshot to "no maximum length", so a later PostgreSQL port
    /// creates <c>text</c> rather than <c>character varying(1000)</c>, which PostgreSQL would enforce. The committed
    /// <c>DomainModel</c> migration is left untouched (task T-011, step 1).
    /// </para>
    /// </remarks>
    public partial class NotificationMessageUnbounded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing to do on SQLite; see the class remarks.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo on SQLite; see the class remarks.
        }
    }
}
