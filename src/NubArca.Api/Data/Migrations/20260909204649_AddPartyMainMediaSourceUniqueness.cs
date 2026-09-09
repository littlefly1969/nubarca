using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// One album is one party's <c>main</c> media source, and the database says
    /// so.
    ///
    /// <para>P1 left this as an application rule: the composite key
    /// <c>(PartyId, AlbumId)</c> stops an album contributing to the same party
    /// twice, but nothing stopped two parties both naming it — while
    /// <c>EnsureForAlbumAsync</c> has always needed exactly one answer to "which
    /// party is this album's". A lookup that has to pick a winner is a lookup
    /// whose answer can change; this replaces the plain album index with a
    /// unique one over <c>(AlbumId, Role)</c> so a second claim loses in the
    /// database rather than in whichever check happened to run first.</para>
    ///
    /// <para>Keyed on Role rather than on the literal <c>main</c>, so the roles
    /// the table exists for get the same rule without another migration — and
    /// without a database enum.</para>
    ///
    /// <para>No data is touched. A duplicate is a state the application could
    /// not produce: P1 created one source per album and every writer since has
    /// gone through <c>EnsureForAlbumAsync</c>, so this fails loudly rather than
    /// deleting somebody's party to make room for the constraint.</para>
    /// </summary>
    public partial class AddPartyMainMediaSourceUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_party_media_sources_album",
                table: "party_media_sources");

            migrationBuilder.CreateIndex(
                name: "ux_party_media_sources_album_role",
                table: "party_media_sources",
                columns: new[] { "AlbumId", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_party_media_sources_album_role",
                table: "party_media_sources");

            migrationBuilder.CreateIndex(
                name: "ix_party_media_sources_album",
                table: "party_media_sources",
                column: "AlbumId");
        }
    }
}
