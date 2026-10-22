using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMailMessageTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_message_texts",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Text = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_message_texts", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_mail_message_texts_mail_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "mail_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Full-text search over the body (MailMessageQueryService). An
            // expression index, which the EF model cannot describe: the query
            // uses exactly to_tsvector('simple', "Text").
            migrationBuilder.Sql(
                "CREATE INDEX ix_mail_message_texts_fts ON mail_message_texts USING gin (to_tsvector('simple'::regconfig, \"Text\"));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_mail_message_texts_fts;");
            migrationBuilder.DropTable(
                name: "mail_message_texts");
        }
    }
}
