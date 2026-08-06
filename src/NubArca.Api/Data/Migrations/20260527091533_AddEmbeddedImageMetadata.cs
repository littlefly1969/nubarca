using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddedImageMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BodySerialNumber",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CameraMake",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CameraModel",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ColorSpace",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateTaken",
                table: "blob_metadata",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DateTakenOffset",
                table: "blob_metadata",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DateTakenSource",
                table: "blob_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExposureBias",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExposureProgram",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExposureTime",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionVersion",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FNumber",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Flash",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FocalLength",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocalLength35mm",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpsAltitude",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpsLatitude",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpsLongitude",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasIccProfile",
                table: "blob_metadata",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IccProfileName",
                table: "blob_metadata",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IsoSpeed",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LensMake",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LensModel",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LensSerialNumber",
                table: "blob_metadata",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeteringMode",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Orientation",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Software",
                table: "blob_metadata",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteBalance",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BodySerialNumber",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "CameraMake",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "CameraModel",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "ColorSpace",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "DateTaken",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "DateTakenOffset",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "DateTakenSource",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "ExposureBias",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "ExposureProgram",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "ExposureTime",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "ExtractionVersion",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "FNumber",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "Flash",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "FocalLength",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "FocalLength35mm",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "GpsAltitude",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "GpsLatitude",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "GpsLongitude",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "HasIccProfile",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "IccProfileName",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "IsoSpeed",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "LensMake",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "LensModel",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "LensSerialNumber",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "MeteringMode",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "Orientation",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "Software",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "WhiteBalance",
                table: "blob_metadata");
        }
    }
}
