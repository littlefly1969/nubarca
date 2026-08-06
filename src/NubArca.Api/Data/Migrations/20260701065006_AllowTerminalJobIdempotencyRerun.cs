using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowTerminalJobIdempotencyRerun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_background_jobs_idempotency",
                table: "background_jobs");

            migrationBuilder.CreateIndex(
                name: "ux_background_jobs_idempotency",
                table: "background_jobs",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL AND \"Status\" IN ('queued', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_background_jobs_idempotency",
                table: "background_jobs");

            migrationBuilder.CreateIndex(
                name: "ux_background_jobs_idempotency",
                table: "background_jobs",
                column: "IdempotencyKey",
                unique: true);
        }
    }
}
