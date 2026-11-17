using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookRetriesAndCircuitBreakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "WebhookEndpointSubscriptions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(250)",
                oldMaxLength: 250);

            migrationBuilder.AddColumn<bool>(
                name: "CircuitHalfOpenProbeActive",
                table: "WebhookEndpoints",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CircuitOpenedAtUtc",
                table: "WebhookEndpoints",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CircuitState",
                table: "WebhookEndpoints",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Closed");

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                table: "WebhookEndpoints",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ClaimCount",
                table: "WebhookDeliveries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAtUtc",
                table: "WebhookDeliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCategory",
                table: "WebhookDeliveries",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAtUtc",
                table: "WebhookDeliveries",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("UPDATE dbo.WebhookDeliveries SET NextAttemptAtUtc = CreatedAtUtc WHERE NextAttemptAtUtc <= '1970-01-01';");

            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                table: "WebhookDeliveries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "WebhookDeliveryReplayAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReplayKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReplayedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveryReplayAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveryReplayAudits_WebhookDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "WebhookDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_Status_NextAttemptAtUtc",
                table: "WebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_TenantId_Status",
                table: "WebhookDeliveries",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryReplayAudits_DeliveryId",
                table: "WebhookDeliveryReplayAudits",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_ReplayedAtUtc",
                table: "WebhookDeliveryReplayAudits",
                columns: new[] { "TenantId", "ReplayedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_ReplayKey",
                table: "WebhookDeliveryReplayAudits",
                columns: new[] { "TenantId", "ReplayKey" },
                unique: true,
                filter: "[ReplayKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookDeliveryReplayAudits");

            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_Status_NextAttemptAtUtc",
                table: "WebhookDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_TenantId_Status",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "CircuitHalfOpenProbeActive",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "CircuitOpenedAtUtc",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "CircuitState",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "ClaimCount",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAtUtc",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "LastErrorCategory",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtc",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "ReplayCount",
                table: "WebhookDeliveries");

            migrationBuilder.AlterColumn<string>(
                name: "EventType",
                table: "WebhookEndpointSubscriptions",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);
        }
    }
}
