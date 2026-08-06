using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoProbeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudioChannels",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioCodec",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioSampleRate",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DurationSeconds",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FrameRate",
                table: "blob_metadata",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasAudio",
                table: "blob_metadata",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Rotation",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VideoBitrate",
                table: "blob_metadata",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoCodec",
                table: "blob_metadata",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VideoExtractedAt",
                table: "blob_metadata",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoExtractionErrorCode",
                table: "blob_metadata",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoExtractionStatus",
                table: "blob_metadata",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<int>(
                name: "VideoExtractionVersion",
                table: "blob_metadata",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_blob_metadata_duration_non_negative",
                table: "blob_metadata",
                sql: "\"DurationSeconds\" IS NULL OR \"DurationSeconds\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_blob_metadata_frame_rate_non_negative",
                table: "blob_metadata",
                sql: "\"FrameRate\" IS NULL OR \"FrameRate\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_blob_metadata_video_bitrate_non_negative",
                table: "blob_metadata",
                sql: "\"VideoBitrate\" IS NULL OR \"VideoBitrate\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_blob_metadata_duration_non_negative",
                table: "blob_metadata");

            migrationBuilder.DropCheckConstraint(
                name: "ck_blob_metadata_frame_rate_non_negative",
                table: "blob_metadata");

            migrationBuilder.DropCheckConstraint(
                name: "ck_blob_metadata_video_bitrate_non_negative",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "AudioChannels",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "AudioCodec",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "AudioSampleRate",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "FrameRate",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "HasAudio",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "Rotation",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoBitrate",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoCodec",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoExtractedAt",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoExtractionErrorCode",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoExtractionStatus",
                table: "blob_metadata");

            migrationBuilder.DropColumn(
                name: "VideoExtractionVersion",
                table: "blob_metadata");
        }
    }
}
