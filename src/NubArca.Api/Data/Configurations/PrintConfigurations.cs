using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Data.Configurations;

public sealed class PrintStationConfiguration : IEntityTypeConfiguration<PrintStation>
{
    public void Configure(EntityTypeBuilder<PrintStation> builder)
    {
        builder.ToTable("print_stations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(120);
        builder.Property(x => x.DesiredState).IsRequired().HasMaxLength(20);
        builder.Property(x => x.CredentialHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.AgentVersion).HasMaxLength(64);
        builder.Property(x => x.LastSeenAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.CredentialHash).IsUnique()
            .HasFilter("\"CredentialHash\" IS NOT NULL")
            .HasDatabaseName("ux_print_stations_credential_hash");
        builder.HasIndex(x => new { x.OwnerUserId, x.CreatedAt })
            .HasDatabaseName("ix_print_stations_owner_created");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PrintStationEnrollmentConfiguration : IEntityTypeConfiguration<PrintStationEnrollment>
{
    public void Configure(EntityTypeBuilder<PrintStationEnrollment> builder)
    {
        builder.ToTable("print_station_enrollments");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(x => x.TokenHash).IsUnique()
            .HasDatabaseName("ux_print_station_enrollments_token_hash");
        builder.HasIndex(x => new { x.PrintStationId, x.ExpiresAt })
            .HasDatabaseName("ix_print_station_enrollments_station_expires");
        builder.HasOne<PrintStation>().WithMany().HasForeignKey(x => x.PrintStationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PrinterDeviceConfiguration : IEntityTypeConfiguration<PrinterDevice>
{
    public void Configure(EntityTypeBuilder<PrinterDevice> builder)
    {
        builder.ToTable("printer_devices");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.DeviceKey).IsRequired().HasMaxLength(256);
        builder.Property(x => x.DisplayName).IsRequired().HasMaxLength(160);
        builder.Property(x => x.Manufacturer).HasMaxLength(120);
        builder.Property(x => x.Model).HasMaxLength(120);
        builder.Property(x => x.AdapterKind).IsRequired().HasMaxLength(40);
        builder.Property(x => x.CapabilitiesJson).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.LastObservedState).IsRequired().HasMaxLength(24);
        builder.Property(x => x.LastSeenAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(x => new { x.PrintStationId, x.DeviceKey }).IsUnique()
            .HasDatabaseName("ux_printer_devices_station_device_key");
        builder.HasOne<PrintStation>().WithMany().HasForeignKey(x => x.PrintStationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.ToTable("print_jobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Kind).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Format).IsRequired().HasMaxLength(24);
        builder.Property(x => x.State).IsRequired().HasMaxLength(24);
        builder.Property(x => x.RenderSpecificationJson).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.ArtifactStorageKey).HasMaxLength(256);
        builder.Property(x => x.ArtifactContentType).HasMaxLength(80);
        builder.Property(x => x.ClaimTokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.FailureCode).HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.RenderedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.ClaimedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.LeaseUntil).HasColumnType("timestamp with time zone");
        builder.Property(x => x.SubmittedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(x => new { x.PrintStationId, x.State, x.CreatedAt })
            .HasDatabaseName("ix_print_jobs_station_state_created");
        builder.HasIndex(x => new { x.OwnerUserId, x.CreatedAt })
            .HasDatabaseName("ix_print_jobs_owner_created");
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PrintStation>().WithMany().HasForeignKey(x => x.PrintStationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PrinterDevice>().WithMany().HasForeignKey(x => x.PrinterDeviceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FileItem>().WithMany().HasForeignKey(x => x.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyPrintProfileConfiguration : IEntityTypeConfiguration<PartyPrintProfile>
{
    public void Configure(EntityTypeBuilder<PartyPrintProfile> builder)
    {
        builder.ToTable("party_print_profiles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        // One profile per party album, enforced by the database rather than by
        // whichever code path happens to create it.
        builder.HasIndex(x => x.PartyAlbumId).IsUnique()
            .HasDatabaseName("ux_party_print_profiles_album");
        builder.HasIndex(x => x.OwnerUserId)
            .HasDatabaseName("ix_party_print_profiles_owner");
        builder.Property(x => x.FooterText).HasMaxLength(PartyPrintLimits.FooterMaxLength);
        builder.Property(x => x.PublicSequenceNext).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
        builder.HasOne<Album>().WithMany().HasForeignKey(x => x.PartyAlbumId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PrintStation>().WithMany().HasForeignKey(x => x.PrintStationId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<PrinterDevice>().WithMany().HasForeignKey(x => x.PrinterDeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class PrintJobSourceConfiguration : IEntityTypeConfiguration<PrintJobSource>
{
    public void Configure(EntityTypeBuilder<PrintJobSource> builder)
    {
        builder.ToTable("print_job_sources");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        // One photograph per slot: the shape of a strip is a constraint, not a
        // convention the writing code is trusted to keep.
        builder.HasIndex(x => new { x.PrintJobId, x.SlotIndex }).IsUnique()
            .HasDatabaseName("ux_print_job_sources_job_slot");
        builder.HasOne<PrintJob>().WithMany().HasForeignKey(x => x.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);
        // Restrict, like the job's own source: a photograph that is part of a
        // print cannot be deleted out from under it.
        builder.HasOne<FileItem>().WithMany().HasForeignKey(x => x.FileItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PartyPrintRequestConfiguration : IEntityTypeConfiguration<PartyPrintRequest>
{
    public void Configure(EntityTypeBuilder<PartyPrintRequest> builder)
    {
        builder.ToTable("party_print_requests");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.IdempotencyKeyHash).IsRequired().HasMaxLength(64).IsFixedLength();
        builder.Property(x => x.Product).IsRequired().HasMaxLength(16);
        builder.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        // THE guarantee against a second physical print: one accepted request per
        // key per party, refused by the database even when two requests race.
        builder.HasIndex(x => new { x.PartyAlbumId, x.IdempotencyKeyHash }).IsUnique()
            .HasDatabaseName("ux_party_print_requests_album_key");
        builder.HasOne<Album>().WithMany().HasForeignKey(x => x.PartyAlbumId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PrintJob>().WithMany().HasForeignKey(x => x.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
