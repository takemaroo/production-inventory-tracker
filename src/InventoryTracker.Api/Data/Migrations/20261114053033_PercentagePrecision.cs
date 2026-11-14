using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Api.Data.Migrations
{
    /// <summary>
    /// T-011, decision #82: widens the three stored percentages (<c>ProductionOrders.OutputVariancePercent</c>,
    /// <c>ProductionOrders.ToleranceAtCompletionPercent</c>, <c>ProductionOrderLines.UsageVariancePercent</c>) from
    /// <c>decimal(18,6)</c> to <c>decimal(28,6)</c> (architecture 3.1; see <c>ColumnRules.IsPercentage</c>).
    /// </summary>
    /// <remarks>
    /// <b>Why <c>Up</c> and <c>Down</c> are empty.</b> EF Core's SQLite provider declares every decimal column as
    /// <c>TEXT</c>, whatever its precision, and SQLite never enforces a precision, so there is no column to change: the
    /// declared type is <c>TEXT</c> before and after, and the provider generates no operation. (SQLite does keep each
    /// column's declared type; it simply does not enforce a precision.)
    /// The migration is kept because the model snapshot it updates is what records <c>(28, 6)</c> for a later PostgreSQL
    /// port, where <c>numeric(18,6)</c> would refuse a percentage of about 1e14 (task T-011, step 1b).
    /// </remarks>
    public partial class PercentagePrecision : Migration
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
