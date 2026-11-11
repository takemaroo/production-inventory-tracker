using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryTracker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Units",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NameNormalized = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Units", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NameNormalized = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    UnitId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_Units_UnitId",
                        column: x => x.UnitId,
                        principalTable: "Units",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrders",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FinishedProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentById = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedById = table.Column<string>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledById = table.Column<string>(type: "TEXT", nullable: true),
                    StatusChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProducedQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    OutputVariance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    OutputVariancePercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    ToleranceAtCompletionPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    CompletionReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrders", x => x.Number);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_AspNetUsers_CancelledById",
                        column: x => x.CancelledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_AspNetUsers_CompletedById",
                        column: x => x.CompletedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_AspNetUsers_SentById",
                        column: x => x.SentById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_Items_FinishedProductId",
                        column: x => x.FinishedProductId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Recipes",
                columns: table => new
                {
                    FinishedProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedById = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipes", x => x.FinishedProductId);
                    table.ForeignKey(
                        name: "FK_Recipes_AspNetUsers_UpdatedById",
                        column: x => x.UpdatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Recipes_Items_FinishedProductId",
                        column: x => x.FinishedProductId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComplementaryOrders",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ParentOrderNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequestedById = table.Column<string>(type: "TEXT", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedById = table.Column<string>(type: "TEXT", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectedById = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentById = table.Column<string>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledById = table.Column<string>(type: "TEXT", nullable: true),
                    CancellationNote = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplementaryOrders", x => x.Number);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_AspNetUsers_ApprovedById",
                        column: x => x.ApprovedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_AspNetUsers_CancelledById",
                        column: x => x.CancelledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_AspNetUsers_RejectedById",
                        column: x => x.RejectedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_AspNetUsers_RequestedById",
                        column: x => x.RequestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_AspNetUsers_SentById",
                        column: x => x.SentById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrders_ProductionOrders_ParentOrderNumber",
                        column: x => x.ParentOrderNumber,
                        principalTable: "ProductionOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    RawMaterialId = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountPerUnit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    PlannedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    LeftoverApplied = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    WarehouseSend = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    CarriedLeftover = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    ComplementarySends = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    LeftoverEntered = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    LeftoverRemaining = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    ActualUsage = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    RequiredForActualOutput = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    UsageVariance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    UsageVariancePercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: true),
                    UsageVarianceAboveTolerance = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderLines_Items_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrderLines_ProductionOrders_OrderNumber",
                        column: x => x.OrderNumber,
                        principalTable: "ProductionOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Returns",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ProductionOrderNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReceivedById = table.Column<string>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledById = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Returns", x => x.Number);
                    table.ForeignKey(
                        name: "FK_Returns_AspNetUsers_CancelledById",
                        column: x => x.CancelledById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_AspNetUsers_ReceivedById",
                        column: x => x.ReceivedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Returns_ProductionOrders_ProductionOrderNumber",
                        column: x => x.ProductionOrderNumber,
                        principalTable: "ProductionOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecipeLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FinishedProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    RawMaterialId = table.Column<int>(type: "INTEGER", nullable: false),
                    AmountPerUnit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeLines_Items_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeLines_Recipes_FinishedProductId",
                        column: x => x.FinishedProductId,
                        principalTable: "Recipes",
                        principalColumn: "FinishedProductId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComplementaryOrderLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ComplementaryOrderNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LineNo = table.Column<int>(type: "INTEGER", nullable: false),
                    RawMaterialId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SentQuantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplementaryOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrderLines_ComplementaryOrders_ComplementaryOrderNumber",
                        column: x => x.ComplementaryOrderNumber,
                        principalTable: "ComplementaryOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ComplementaryOrderLines_Items_RawMaterialId",
                        column: x => x.RawMaterialId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ProductionOrderNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ComplementaryOrderNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_ComplementaryOrders_ComplementaryOrderNumber",
                        column: x => x.ComplementaryOrderNumber,
                        principalTable: "ComplementaryOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Notifications_ProductionOrders_ProductionOrderNumber",
                        column: x => x.ProductionOrderNumber,
                        principalTable: "ProductionOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    WarehouseChange = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    ProductionStorageChange = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ProductionOrderNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ComplementaryOrderNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ReturnNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    ReceiptDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Movements_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_ComplementaryOrders_ComplementaryOrderNumber",
                        column: x => x.ComplementaryOrderNumber,
                        principalTable: "ComplementaryOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_ProductionOrders_ProductionOrderNumber",
                        column: x => x.ProductionOrderNumber,
                        principalTable: "ProductionOrders",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Returns_ReturnNumber",
                        column: x => x.ReturnNumber,
                        principalTable: "Returns",
                        principalColumn: "Number",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReads",
                columns: table => new
                {
                    NotificationId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReads", x => new { x.NotificationId, x.UserId });
                    table.ForeignKey(
                        name: "FK_NotificationReads_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationReads_Notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "Notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrderLines_ComplementaryOrderNumber_RawMaterialId",
                table: "ComplementaryOrderLines",
                columns: new[] { "ComplementaryOrderNumber", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrderLines_RawMaterialId",
                table: "ComplementaryOrderLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_ApprovedById",
                table: "ComplementaryOrders",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_CancelledById",
                table: "ComplementaryOrders",
                column: "CancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_ParentOrderNumber",
                table: "ComplementaryOrders",
                column: "ParentOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_RejectedById",
                table: "ComplementaryOrders",
                column: "RejectedById");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_RequestedById",
                table: "ComplementaryOrders",
                column: "RequestedById");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_SentById",
                table: "ComplementaryOrders",
                column: "SentById");

            migrationBuilder.CreateIndex(
                name: "IX_ComplementaryOrders_Status",
                table: "ComplementaryOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Items_NameNormalized",
                table: "Items",
                column: "NameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_UnitId",
                table: "Items",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ComplementaryOrderNumber",
                table: "Movements",
                column: "ComplementaryOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ItemId",
                table: "Movements",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OccurredAt",
                table: "Movements",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ProductionOrderNumber",
                table: "Movements",
                column: "ProductionOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ReturnNumber",
                table: "Movements",
                column: "ReturnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_Type",
                table: "Movements",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_UserId",
                table: "Movements",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReads_UserId",
                table: "NotificationReads",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ComplementaryOrderNumber",
                table: "Notifications",
                column: "ComplementaryOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ProductionOrderNumber",
                table: "Notifications",
                column: "ProductionOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderLines_OrderNumber_RawMaterialId",
                table: "ProductionOrderLines",
                columns: new[] { "OrderNumber", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderLines_RawMaterialId",
                table: "ProductionOrderLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_CancelledById",
                table: "ProductionOrders",
                column: "CancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_CompletedById",
                table: "ProductionOrders",
                column: "CompletedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_CreatedAt",
                table: "ProductionOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_CreatedById",
                table: "ProductionOrders",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_FinishedProductId",
                table: "ProductionOrders",
                column: "FinishedProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_OneInProduction",
                table: "ProductionOrders",
                column: "Status",
                unique: true,
                filter: "\"Status\" = 'InProduction'");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_SentById",
                table: "ProductionOrders",
                column: "SentById");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_Status",
                table: "ProductionOrders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLines_FinishedProductId_RawMaterialId",
                table: "RecipeLines",
                columns: new[] { "FinishedProductId", "RawMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLines_RawMaterialId",
                table: "RecipeLines",
                column: "RawMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_UpdatedById",
                table: "Recipes",
                column: "UpdatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_CancelledById",
                table: "Returns",
                column: "CancelledById");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_CreatedById",
                table: "Returns",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_ItemId",
                table: "Returns",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_ProductionOrderNumber",
                table: "Returns",
                column: "ProductionOrderNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_ReceivedById",
                table: "Returns",
                column: "ReceivedById");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_Status_ItemId",
                table: "Returns",
                columns: new[] { "Status", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_Units_NameNormalized",
                table: "Units",
                column: "NameNormalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComplementaryOrderLines");

            migrationBuilder.DropTable(
                name: "Movements");

            migrationBuilder.DropTable(
                name: "NotificationReads");

            migrationBuilder.DropTable(
                name: "ProductionOrderLines");

            migrationBuilder.DropTable(
                name: "RecipeLines");

            migrationBuilder.DropTable(
                name: "Returns");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Recipes");

            migrationBuilder.DropTable(
                name: "ComplementaryOrders");

            migrationBuilder.DropTable(
                name: "ProductionOrders");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "Units");
        }
    }
}
