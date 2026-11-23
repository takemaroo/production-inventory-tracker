using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Api.Data.Migrations
{
    /// <summary>
    /// (v1.1) T-022, decision #154: the whole version 1.1 schema in one migration. It creates <c>Customers</c>,
    /// <c>SalesOrders</c> and <c>SalesOrderLines</c> with their indexes, and adds <c>Movements.SalesOrderNumber</c> with
    /// its foreign key and the filtered unique index <c>IX_Movements_OneShipmentPerLine</c> (architecture 3.2). It
    /// changes no existing row (AC-30.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the first migration that runs on a database with real data</b>: the owner's version 1 database, with
    /// the whole stock history in <c>Movements</c>. EF Core runs a migration's statements inside one transaction, so if
    /// anything fails, everything is rolled back and the database stays exactly as version 1 left it.
    /// </para>
    /// <para>
    /// <b><c>Up</c> is edited by hand (architecture 3.4, risk 15).</b> EF Core's SQLite provider cannot add a foreign key
    /// to an existing table, so for the generated <c>AddColumn</c> + <c>AddForeignKey</c> on <c>Movements</c> it rebuilds
    /// the table: it copies every movement into <c>ef_temp_Movements</c>, drops the old table, renames the copy and
    /// recreates the indexes, spread over three transactions with <c>PRAGMA foreign_keys</c> between them. A stop part-way
    /// could leave the owner's movements without indexes, or the migration unrecorded. Those two calls were replaced by
    /// one <c>ALTER TABLE ... ADD ... REFERENCES</c>, which SQLite runs in place. <b>Do not delete and regenerate this
    /// migration</b>: that brings the rebuild back. <c>UpgradeTests</c> fails if the migration's SQL ever contains
    /// <c>ef_temp_</c> again.
    /// </para>
    /// <para>
    /// <c>Down</c>, the <c>.Designer.cs</c> file and the model snapshot are exactly as generated. <c>Down</c> does rebuild
    /// the table; it is for a developer's own database only and is never run on the owner's. Once any movement names a
    /// sales order, <c>Down</c> stops and rolls back (the movements still point at the sales orders it tries to drop); to
    /// undo the migration then, delete the developer database instead.
    /// </para>
    /// </remarks>
    public partial class SalesOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NameNormalized = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false),
                    ShippedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShippedById = table.Column<string>(type: "TEXT", nullable: true),
                    ShipmentNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledById = table.Column<string>(type: "TEXT", nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StatusChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Number);
                    table.ForeignKey(
                        name: "FK_SalesOrders_AspNetUsers_CancelledById",
                        column: x => x.CancelledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_AspNetUsers_ShippedById",
                        column: x => x.ShippedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Why not EF Core's own AddColumn + AddForeignKey: on SQLite they rebuild the whole Movements table over three
            // transactions (architecture 3.4). SQLite can add a nullable column with a REFERENCES clause in place, in this
            // migration's single transaction.
            migrationBuilder.Sql("ALTER TABLE \"Movements\" ADD \"SalesOrderNumber\" INTEGER NULL CONSTRAINT \"FK_Movements_SalesOrders_SalesOrderNumber\" REFERENCES \"SalesOrders\" (\"Number\") ON DELETE RESTRICT;");

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SalesOrderNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_SalesOrderNumber",
                        column: x => x.SalesOrderNumber,
                        principalTable: "SalesOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OneShipmentPerLine",
                table: "Movements",
                columns: new[] { "SalesOrderNumber", "ItemId" },
                unique: true,
                filter: "\"SalesOrderNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CreatedAt",
                table: "Customers",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CreatedById",
                table: "Customers",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NameNormalized",
                table: "Customers",
                column: "NameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ItemId",
                table: "SalesOrderLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_SalesOrderNumber_ItemId",
                table: "SalesOrderLines",
                columns: new[] { "SalesOrderNumber", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CancelledById",
                table: "SalesOrders",
                column: "CancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CreatedAt",
                table: "SalesOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CreatedById",
                table: "SalesOrders",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_ShippedById",
                table: "SalesOrders",
                column: "ShippedById");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Status_ShippedAt",
                table: "SalesOrders",
                columns: new[] { "Status", "ShippedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Movements_SalesOrders_SalesOrderNumber",
                table: "Movements");

            migrationBuilder.DropTable(
                name: "SalesOrderLines");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Movements_OneShipmentPerLine",
                table: "Movements");

            migrationBuilder.DropColumn(
                name: "SalesOrderNumber",
                table: "Movements");
        }
    }
}
