using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// What a party TELLS its guests: six typed slots, at most one per kind.
    ///
    /// <para>One new table and nothing else. The composite key
    /// <c>(PartyId, Kind)</c> is the "one slot per kind" rule itself rather than
    /// a surrogate id plus a unique index, so there is no second way to write
    /// the same fact and no id for a client to address a slot by instead of
    /// naming what it is.</para>
    ///
    /// <para>Deliberately no slug, no sort order and no block list: the order a
    /// guest reads these in is a product decision, not data somebody drags
    /// around. The payload column is bounded generously — the real limits are
    /// per FIELD and enforced before anything is stored.</para>
    ///
    /// <para><c>LibraryAccessExpiresAt</c> is NOT added here: it has existed on
    /// parties since the aggregate root landed, and this release is where it
    /// starts being read.</para>
    /// </summary>
    public partial class AddPartyGuestContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "party_guest_contents",
                columns: table => new
                {
                    PartyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    VisibleBefore = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    VisibleLive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    VisibleAfter = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ContentJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party_guest_contents", x => new { x.PartyId, x.Kind });
                    table.ForeignKey(
                        name: "FK_party_guest_contents_parties_PartyId",
                        column: x => x.PartyId,
                        principalTable: "parties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party_guest_contents");
        }
    }
}
