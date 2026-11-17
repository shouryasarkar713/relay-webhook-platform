using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Relay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SplitClaimAndPublishAttemptCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClaimCount",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PublishAttemptCount",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill existing records: ClaimCount = AttemptCount, PublishAttemptCount = AttemptCount
            migrationBuilder.Sql("UPDATE dbo.OutboxMessages SET ClaimCount = AttemptCount, PublishAttemptCount = AttemptCount;");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "OutboxMessages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimCount",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "PublishAttemptCount",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "OutboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
