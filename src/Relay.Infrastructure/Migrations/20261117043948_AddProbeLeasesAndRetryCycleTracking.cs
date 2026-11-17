using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProbeLeasesAndRetryCycleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_ReplayKey",
                table: "WebhookDeliveryReplayAudits");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HalfOpenProbeLeaseExpiresAtUtc",
                table: "WebhookEndpoints",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HalfOpenProbeToken",
                table: "WebhookEndpoints",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCycleAttemptCount",
                table: "WebhookDeliveries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryCycleStartedAtUtc",
                table: "WebhookDeliveries",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_DeliveryId_ReplayKey",
                table: "WebhookDeliveryReplayAudits",
                columns: new[] { "TenantId", "DeliveryId", "ReplayKey" },
                unique: true,
                filter: "[ReplayKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_DeliveryId_ReplayKey",
                table: "WebhookDeliveryReplayAudits");

            migrationBuilder.DropColumn(
                name: "HalfOpenProbeLeaseExpiresAtUtc",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "HalfOpenProbeToken",
                table: "WebhookEndpoints");

            migrationBuilder.DropColumn(
                name: "RetryCycleAttemptCount",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "RetryCycleStartedAtUtc",
                table: "WebhookDeliveries");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveryReplayAudits_TenantId_ReplayKey",
                table: "WebhookDeliveryReplayAudits",
                columns: new[] { "TenantId", "ReplayKey" },
                unique: true,
                filter: "[ReplayKey] IS NOT NULL");
        }
    }
}
