using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;

namespace NubArca.Api.Data.Configurations;

// Attendance's two tables. Every foreign key is Restrict, like every other Party
// table: PartyStateEraser and the guest list's own erasure delete these rows out
// loud, in foreign-key order, before the guest or the party they name — never
// by a cascade nobody reads.

public sealed class PartyGuestAttendanceConfiguration : IEntityTypeConfiguration<PartyGuestAttendance>
{
    public void Configure(EntityTypeBuilder<PartyGuestAttendance> builder)
    {
        builder.ToTable("party_guest_attendance", t =>
        {
            // A closed vocabulary, held by the database too. A future source is
            // a migration that widens this, never a free string.
            t.HasCheckConstraint(
                "ck_party_guest_attendance_source",
                "\"Source\" IN ('owner', 'invitation')");
        });
        // The key IS "a person arrives once". Two concurrent check-ins of the
        // same guest — the host's and the guest's own included — meet here, and
        // only one of them becomes a row.
        builder.HasKey(a => a.PartyGuestId);
        builder.Property(a => a.PartyGuestId).ValueGeneratedNever();
        builder.Property(a => a.Source).IsRequired().HasMaxLength(16);
        builder.Property(a => a.CheckedInAt).HasColumnType("timestamp with time zone");
        builder.Property(a => a.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<PartyGuest>()
            .WithOne()
            .HasForeignKey<PartyGuestAttendance>(a => a.PartyGuestId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyAttendanceGuestConfiguration : IEntityTypeConfiguration<PartyAttendanceGuest>
{
    public void Configure(EntityTypeBuilder<PartyAttendanceGuest> builder)
    {
        builder.ToTable("party_attendance_guests", t =>
        {
            t.HasCheckConstraint("ck_party_attendance_guests_version", "\"Version\" >= 1");
            // A person is somebody. The validator trims; the database refuses
            // an empty name however it arrives.
            t.HasCheckConstraint("ck_party_attendance_guests_name", "length(\"Name\") > 0");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();

        // varchar(n) counts characters, which PostgreSQL counts as code points —
        // exactly the validator's unit.
        builder.Property(g => g.Name).IsRequired().HasMaxLength(PartyAttendanceLimits.MaxNameLength);
        builder.Property(g => g.SearchText).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(g => g.Version).HasDefaultValue(1);
        builder.Property(g => g.CheckedInAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(g => g.UpdatedAt).HasColumnType("timestamp with time zone");

        // THE IDEMPOTENCY RULE, and the party's list at the same time: its
        // leading column is the party. Two requests carrying one add's id
        // cannot both become people, however they race.
        builder.HasIndex(g => new { g.PartyId, g.ClientRequestId })
            .IsUnique()
            .HasDatabaseName("ux_party_attendance_guests_request");
        // The guest directory reads a party's other arrivals latest first, one
        // page at a time, by (CheckedInAt, Id).
        builder.HasIndex(g => new { g.PartyId, g.CheckedInAt, g.Id })
            .HasDatabaseName("ix_party_attendance_guests_party_checked_in");

        builder.HasOne<Domain.Party>()
            .WithMany()
            .HasForeignKey(g => g.PartyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
