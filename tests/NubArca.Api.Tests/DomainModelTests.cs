using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Tests;

public class DomainModelTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=nubarca-test;Username=test;Password=test")
            .Options;

        return new AppDbContext(options);
    }

    [Fact]
    public void Model_Has_All_DbSets()
    {
        using var ctx = CreateContext();

        Assert.NotNull(ctx.Model.FindEntityType(typeof(User)));
        Assert.NotNull(ctx.Model.FindEntityType(typeof(Folder)));
        Assert.NotNull(ctx.Model.FindEntityType(typeof(FileItem)));
        Assert.NotNull(ctx.Model.FindEntityType(typeof(BlobObject)));
        Assert.NotNull(ctx.Model.FindEntityType(typeof(AuditLog)));
    }

    [Fact]
    public void User_Email_Is_Unique()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(User))!;

        var index = entity.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(User.Email));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void BlobObject_Sha256_Is_Unique()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(BlobObject))!;

        var index = entity.GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(BlobObject.Sha256));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Folder_Has_Filtered_Unique_Active_Sibling_Name()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(Folder))!;

        var index = entity.GetIndexes().Single(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(Folder.OwnerUserId),
                nameof(Folder.ParentFolderId),
                nameof(Folder.Name),
            }));

        // Private Vault: sibling-name uniqueness is now scoped to the NORMAL
        // namespace (PrivateVaultId IS NULL); a separate vault-scope index covers
        // vaulted content.
        Assert.Equal("\"DeletedAt\" IS NULL AND \"PrivateVaultId\" IS NULL", index.GetFilter());
    }

    [Fact]
    public void Folder_Has_Owner_Parent_Deleted_Index()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(Folder))!;

        Assert.Contains(entity.GetIndexes(), i =>
            !i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(Folder.OwnerUserId),
                nameof(Folder.ParentFolderId),
                nameof(Folder.DeletedAt),
            }));
    }

    [Fact]
    public void FileItem_Has_Required_Indexes()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(FileItem))!;
        var indexes = entity.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToArray()).ToList();

        Assert.Contains(indexes, ix => ix.SequenceEqual(new[]
        {
            nameof(FileItem.OwnerUserId),
            nameof(FileItem.ParentFolderId),
            nameof(FileItem.DeletedAt),
        }));

        Assert.Contains(indexes, ix => ix.SequenceEqual(new[]
        {
            nameof(FileItem.OwnerUserId),
            nameof(FileItem.Name),
        }));

        Assert.Contains(indexes, ix => ix.SequenceEqual(new[] { nameof(FileItem.BlobObjectId) }));
    }

    [Fact]
    public void AuditLog_Has_Required_Indexes()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;
        var indexes = entity.GetIndexes().Select(i => i.Properties.Select(p => p.Name).ToArray()).ToList();

        Assert.Contains(indexes, ix => ix.SequenceEqual(new[]
        {
            nameof(AuditLog.UserId),
            nameof(AuditLog.CreatedAt),
        }));

        Assert.Contains(indexes, ix => ix.SequenceEqual(new[]
        {
            nameof(AuditLog.Action),
            nameof(AuditLog.CreatedAt),
        }));
    }

    [Theory]
    [InlineData(typeof(Folder), nameof(Folder.OwnerUserId), typeof(User))]
    [InlineData(typeof(Folder), nameof(Folder.ParentFolderId), typeof(Folder))]
    [InlineData(typeof(FileItem), nameof(FileItem.OwnerUserId), typeof(User))]
    [InlineData(typeof(FileItem), nameof(FileItem.ParentFolderId), typeof(Folder))]
    [InlineData(typeof(FileItem), nameof(FileItem.BlobObjectId), typeof(BlobObject))]
    [InlineData(typeof(AuditLog), nameof(AuditLog.UserId), typeof(User))]
    public void ForeignKey_Is_Configured_With_Restrict(Type dependent, string fkProperty, Type principal)
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(dependent)!;

        var fk = entity.GetForeignKeys().SingleOrDefault(f =>
            f.Properties.Count == 1 &&
            f.Properties[0].Name == fkProperty);

        Assert.NotNull(fk);
        Assert.Equal(principal, fk!.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Theory]
    [InlineData(typeof(BlobObject), "ck_blob_objects_reference_count_non_negative")]
    [InlineData(typeof(BlobObject), "ck_blob_objects_size_bytes_non_negative")]
    [InlineData(typeof(FileItem), "ck_file_items_size_bytes_non_negative")]
    [InlineData(typeof(ShareLink), "ck_share_links_download_count_non_negative")]
    [InlineData(typeof(ShareLink), "ck_share_links_max_downloads_positive_or_null")]
    public void Numeric_Invariant_Check_Constraint_Is_Registered(Type entityType, string name)
    {
        using var ctx = CreateContext();
        // CheckConstraints are not retained in the runtime read-optimized model;
        // ask EF for the design-time model so we can verify configuration intent.
        var designModel = ctx.GetService<IDesignTimeModel>().Model;
        var entity = designModel.FindEntityType(entityType)!;

        var check = entity.GetCheckConstraints().SingleOrDefault(c => c.Name == name);

        Assert.NotNull(check);
        Assert.False(string.IsNullOrWhiteSpace(check!.Sql));
    }

    [Fact]
    public void AuditLog_UserId_ForeignKey_Is_Optional()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(AuditLog))!;

        var fk = entity.GetForeignKeys().Single(f =>
            f.Properties.Count == 1 &&
            f.Properties[0].Name == nameof(AuditLog.UserId));

        Assert.False(fk.IsRequired);
    }
}
