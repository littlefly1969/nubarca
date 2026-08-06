using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDerivativeDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "derivative_diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Size = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DetectedContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DetectedFormat = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    FirstAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Backend = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GeneratorVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_derivative_diagnostics", x => x.Id);
                    table.CheckConstraint("ck_derivative_diagnostics_attempt_count_non_negative", "\"AttemptCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_derivative_diagnostics_file_items_FileItemId",
                        column: x => x.FileItemId,
                        principalTable: "file_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_derivative_diagnostics_size_status",
                table: "derivative_diagnostics",
                columns: new[] { "Size", "Status" });

            migrationBuilder.CreateIndex(
                name: "ux_derivative_diagnostics_file_size",
                table: "derivative_diagnostics",
                columns: new[] { "FileItemId", "Size" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "derivative_diagnostics");
        }
    }
}
