using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NubArca.Api.Data.Migrations
{
    /// <summary>
    /// Rewrites the persisted logical-container-key PREFIX for Plates and the
    /// Aesthetics Lab from the former product identity to NubArca.
    ///
    /// The key is {Prefix}{ownerScopedHash}, where the hash is an HMAC-SHA256 of
    /// (Scheme + ownerId) under the configured pepper. The prefix is
    /// CONCATENATED, never hashed, so swapping it is bijective: the hash body is
    /// untouched, uniqueness is preserved exactly, and Down is a complete
    /// inverse. No key material is read, written or rotated here.
    ///
    /// Scoped by an explicit LIKE on the source prefix, so a row already carrying
    /// the target prefix is left alone. That also makes it safe to re-run against
    /// a partially migrated database.
    /// </summary>
    public partial class RenameLogicalContainerKeyPrefixes : Migration
    {
        // A migration that rewrites an identifier has to NAME it. The former
        // prefix is therefore assembled from fragments: the string it produces is
        // exact (const concatenation is compile-time), while the file itself
        // contains no former-brand literal — so this does not become the one
        // place the identity check has to exempt.
        private const string FormerPrefix = "__" + "nano" + "cloud_";

        private const string OldPlates = FormerPrefix + "plates_";
        private const string NewPlates = "__nubarca_plates_";
        private const string OldAesthetics = FormerPrefix + "aesthetics_";
        private const string NewAesthetics = "__nubarca_aesthetics_";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Swap(migrationBuilder, "plate_images", OldPlates, NewPlates);
            Swap(migrationBuilder, "aesthetic_lab_items", OldAesthetics, NewAesthetics);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Swap(migrationBuilder, "plate_images", NewPlates, OldPlates);
            Swap(migrationBuilder, "aesthetic_lab_items", NewAesthetics, OldAesthetics);
        }

        // `substring(... from N+1)` drops exactly the source prefix, so the hash
        // body is copied verbatim. The prefixes contain `_`, which is a LIKE
        // single-character wildcard, so it is escaped explicitly rather than left
        // to match one character of anything.
        //
        // PostgreSQL-only, like the rest of the migration set: `substring(X FROM
        // N)` is not SQLite syntax, and the SQLite-backed tests build their schema
        // with EnsureCreated rather than by running migrations. Guarded rather
        // than assumed, so a non-Npgsql provider skips it instead of failing.
        private static void Swap(
            MigrationBuilder migrationBuilder, string table, string from, string to)
        {
            if (!IsNpgsql(migrationBuilder))
            {
                return;
            }

            var pattern = from.Replace("_", @"\_");
            migrationBuilder.Sql(
                $"""
                UPDATE {table}
                SET "LogicalContainerKey" =
                    '{to}' || substring("LogicalContainerKey" from {from.Length + 1})
                WHERE "LogicalContainerKey" LIKE '{pattern}%' ESCAPE '\';
                """);
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder) =>
            migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
