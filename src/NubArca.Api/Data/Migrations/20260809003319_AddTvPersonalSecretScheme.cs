using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Names the scheme of each stored TV Personal Area secret so one row can
    /// describe both credential generations. The default backfills every
    /// existing row to "pin-v1" — they ARE numeric PINs, and a television
    /// already in the field must keep unlocking with the code its owner knows
    /// until they configure the directional one from the account page. Nothing
    /// writes "pin-v1" any more; both creation paths write "dpad-v1".
    /// </summary>
    public partial class AddTvPersonalSecretScheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Scheme",
                table: "tv_personal_pins",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "pin-v1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Scheme",
                table: "tv_personal_pins");
        }
    }
}
