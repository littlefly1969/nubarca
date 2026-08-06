using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;

namespace NubArca.Api.Vault;

// Owner-scoped Private Vault (v0). Exclusion-first: content is marked with the
// owner's vault Id and removed from every normal flow by a global EF query
// filter (see FileItemConfiguration / FolderConfiguration). This service owns
// the password/unlock lifecycle, the short-lived access token, and the DB-only
// logical move-in / move-out. No blob bytes are ever touched; no derived
// artifacts are produced for vault content.
//
// Privacy contract enforced here:
//   * Every browse/move method requires a resolved, non-expired, non-revoked
//     token that belongs to the CURRENT owner (see ResolveVaultAsync). Foreign
//     owners can never resolve another owner's vault.
//   * Unlock returns a single generic failure (null) whether the vault is
//     missing or the password is wrong — no "exists / has content" signal.
//   * No method returns a PasswordHash, TokenHash, KDF params, BlobObjectId,
//     StorageKey, SHA, path, raw metadata, or vector.
public sealed class PrivateVaultService : IPrivateVaultService
{
    // 32 random bytes (256-bit) → URL-safe base64; only the SHA-256 hex is stored.
    private const int TokenBytes = 32;
    // Short-lived unlock proof. On refresh / new tab the raw token is gone
    // (frontend memory only) so the user re-unlocks; this is the server-side cap.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private const int MinPasswordLength = 8;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IPasswordHasher<PrivateVault> _hasher;

    public PrivateVaultService(
        AppDbContext db, TimeProvider clock, IPasswordHasher<PrivateVault> hasher)
    {
        _db = db;
        _clock = clock;
        _hasher = hasher;
    }

    // ── token helpers (same scheme as share links / exports) ────────────────
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    internal static string HashToken(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ── status / setup / unlock / lock ──────────────────────────────────────

    public async Task<PrivateVaultStatus> GetStatusAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var vault = await _db.PrivateVaults.AsNoTracking()
            .FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId, cancellationToken);
        return vault is null
            ? new PrivateVaultStatus(false, "Private", PrivateVaultEncryptionModes.None)
            : new PrivateVaultStatus(true, vault.DisplayName, vault.EncryptionMode);
    }

    public async Task<PrivateVaultSetupOutcome> SetupAsync(
        Guid ownerUserId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return PrivateVaultSetupOutcome.InvalidPassword;
        }

        var exists = await _db.PrivateVaults
            .AnyAsync(v => v.OwnerUserId == ownerUserId, cancellationToken);
        if (exists)
        {
            return PrivateVaultSetupOutcome.AlreadyConfigured;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            DisplayName = "Private",
            EncryptionMode = PrivateVaultEncryptionModes.None,
            CreatedAt = now,
        };
        vault.PasswordHash = _hasher.HashPassword(vault, password);
        _db.PrivateVaults.Add(vault);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent setup (unique OwnerUserId). Treat as
            // already configured — never surfaces internals.
            return PrivateVaultSetupOutcome.AlreadyConfigured;
        }
        return PrivateVaultSetupOutcome.Created;
    }

    public async Task<PrivateVaultUnlockResult?> UnlockAsync(
        Guid ownerUserId, string password, CancellationToken cancellationToken = default)
    {
        // Generic failure for BOTH "no vault" and "wrong password": identical
        // null return, no distinguishing signal.
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var vault = await _db.PrivateVaults
            .FirstOrDefaultAsync(v => v.OwnerUserId == ownerUserId, cancellationToken);
        if (vault is null)
        {
            return null;
        }

        var result = _hasher.VerifyHashedPassword(vault, vault.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            vault.PasswordHash = _hasher.HashPassword(vault, password);
            vault.UpdatedAt = now;
        }

        // Opportunistically drop this owner's dead tokens so the table stays lean.
        await _db.PrivateVaultAccessTokens
            .Where(t => t.OwnerUserId == ownerUserId
                && (t.RevokedAt != null || t.ExpiresAt <= now))
            .ExecuteDeleteAsync(cancellationToken);

        var raw = GenerateToken();
        var expiresAt = now.Add(TokenLifetime);
        _db.PrivateVaultAccessTokens.Add(new PrivateVaultAccessToken
        {
            Id = Guid.NewGuid(),
            PrivateVaultId = vault.Id,
            OwnerUserId = ownerUserId,
            TokenHash = HashToken(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new PrivateVaultUnlockResult(raw, expiresAt);
    }

    public async Task LockAsync(
        Guid ownerUserId, string? rawToken, CancellationToken cancellationToken = default)
    {
        // Lock revokes ALL of the owner's live tokens — leaving the private area
        // in any tab clears access everywhere. A missing/garbage token is a
        // no-op (still generic). rawToken is accepted for symmetry but lock is
        // owner-scoped and does not depend on presenting a specific token.
        _ = rawToken;
        var now = _clock.GetUtcNow().UtcDateTime;
        await _db.PrivateVaultAccessTokens
            .Where(t => t.OwnerUserId == ownerUserId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _ => (DateTime?)now),
                cancellationToken);
    }

    // Resolve a presented raw token to the owner's vault id, or null if the
    // token is missing/malformed/expired/revoked/foreign. This is the single
    // gate every browse/move endpoint calls.
    public async Task<Guid?> ResolveVaultAsync(
        Guid ownerUserId, string? rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var hash = HashToken(rawToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.PrivateVaultAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (row is null
            || row.OwnerUserId != ownerUserId
            || row.RevokedAt != null
            || row.ExpiresAt <= now)
        {
            return null;
        }
        return row.PrivateVaultId;
    }

    // ── browse (after unlock) ───────────────────────────────────────────────

    // Vault root = vault content whose parent is NOT itself in the vault (i.e.
    // the explicitly moved-in items). A moved-in folder keeps its original
    // ParentFolderId (move is a pure flag flip, no reparent), so its parent is a
    // normal folder / null → it surfaces at the vault root; its descendants have
    // in-vault parents → they surface only when their folder is opened.
    public async Task<VaultListing> ListRootAsync(
        Guid ownerUserId, Guid vaultId, CancellationToken cancellationToken = default)
    {
        var folders = await _db.Folders.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.PrivateVaultId == vaultId
                && f.DeletedAt == null
                && (f.ParentFolderId == null
                    || !_db.Folders.IgnoreQueryFilters().Any(p =>
                        p.Id == f.ParentFolderId && p.PrivateVaultId == vaultId)))
            .OrderBy(f => f.Name)
            .Select(f => new VaultFolderDto(f.Id, f.Name))
            .ToListAsync(cancellationToken);

        var files = await ProjectVaultFilesAsync(
            _db.FileItems.AsNoTracking().IgnoreQueryFilters()
                .Where(f => f.OwnerUserId == ownerUserId
                    && f.PrivateVaultId == vaultId
                    && f.DeletedAt == null
                    && (f.ParentFolderId == null
                        || !_db.Folders.IgnoreQueryFilters().Any(p =>
                            p.Id == f.ParentFolderId && p.PrivateVaultId == vaultId)))
                .OrderBy(f => f.Name),
            cancellationToken);

        return new VaultListing(null, folders, files);
    }

    public async Task<VaultListing?> ListFolderAsync(
        Guid ownerUserId, Guid vaultId, Guid folderId, CancellationToken cancellationToken = default)
    {
        // The folder itself must belong to this owner's vault.
        var inVault = await _db.Folders.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(f => f.Id == folderId
                && f.OwnerUserId == ownerUserId
                && f.PrivateVaultId == vaultId
                && f.DeletedAt == null, cancellationToken);
        if (!inVault)
        {
            return null;
        }

        var folders = await _db.Folders.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.PrivateVaultId == vaultId
                && f.ParentFolderId == folderId
                && f.DeletedAt == null)
            .OrderBy(f => f.Name)
            .Select(f => new VaultFolderDto(f.Id, f.Name))
            .ToListAsync(cancellationToken);

        var files = await ProjectVaultFilesAsync(
            _db.FileItems.AsNoTracking().IgnoreQueryFilters()
                .Where(f => f.OwnerUserId == ownerUserId
                    && f.PrivateVaultId == vaultId
                    && f.ParentFolderId == folderId
                    && f.DeletedAt == null)
                .OrderBy(f => f.Name),
            cancellationToken);

        return new VaultListing(folderId, folders, files);
    }

    // Sanitized single-file detail for a file currently in this owner's vault.
    // Bypasses the global filter (IgnoreQueryFilters) and re-imposes the vault
    // authorization by hand (owner + this vault + active). Only curated,
    // display-safe fields are projected; GPS coordinates are never surfaced.
    public async Task<VaultMediaInfo?> GetMediaInfoAsync(
        Guid ownerUserId, Guid vaultId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var row = await _db.FileItems.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.Id == fileId
                && f.OwnerUserId == ownerUserId
                && f.PrivateVaultId == vaultId
                && f.DeletedAt == null)
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.MimeType,
                f.SizeBytes,
                f.CreatedAt,
                f.Width,
                f.Height,
                Detected = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType).FirstOrDefault(),
                BlobWidth = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Width).FirstOrDefault(),
                BlobHeight = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.Height).FirstOrDefault(),
                BlobDateTaken = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DateTaken).FirstOrDefault(),
                Title = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.Title).FirstOrDefault(),
                Description = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.Description).FirstOrDefault(),
                TagsJson = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.TagsJson).FirstOrDefault(),
                Rating = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.Rating).FirstOrDefault(),
                Favorite = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => (bool?)m.IsFavorite).FirstOrDefault(),
                Location = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.LocationOverride).FirstOrDefault(),
                DateTakenOverride = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.DateTakenOverride).FirstOrDefault(),
                HasSmall = _db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small),
                HasMedium = _db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Medium),
                HasPoster = _db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title;
        return new VaultMediaInfo(
            row.Id,
            row.Name,
            title,
            title ?? row.Name,
            VaultMediaKinds.From(row.Detected, row.MimeType),
            row.MimeType,
            row.SizeBytes,
            row.Width ?? row.BlobWidth,
            row.Height ?? row.BlobHeight,
            row.CreatedAt,
            row.DateTakenOverride ?? row.BlobDateTaken,
            string.IsNullOrWhiteSpace(row.Description) ? null : row.Description,
            ParseTags(row.TagsJson),
            row.Rating,
            row.Favorite ?? false,
            string.IsNullOrWhiteSpace(row.Location) ? null : row.Location,
            row.HasSmall,
            row.HasMedium,
            row.HasPoster);
    }

    // Projects a filtered+ordered vault-file query into display-safe DTOs. The
    // coarse media kind and display name are computed in memory (a nested string
    // ternary is fragile to translate); the SQL side only fetches the raw fields
    // plus derivative-existence flags. No derivative is opened or generated.
    private async Task<List<VaultFileDto>> ProjectVaultFilesAsync(
        IQueryable<FileItem> files, CancellationToken cancellationToken)
    {
        var rows = await files
            .Select(f => new
            {
                f.Id,
                f.Name,
                f.MimeType,
                f.SizeBytes,
                f.CreatedAt,
                f.Width,
                f.Height,
                Detected = _db.BlobMetadata.Where(m => m.BlobObjectId == f.BlobObjectId)
                    .Select(m => m.DetectedContentType).FirstOrDefault(),
                Title = _db.FileItemUserMetadata.Where(m => m.FileItemId == f.Id)
                    .Select(m => m.Title).FirstOrDefault(),
                HasSmall = _db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Small),
                HasPoster = _db.FileThumbnails.Any(t => t.FileItemId == f.Id && t.Size == ThumbnailSizes.Poster),
            })
            .ToListAsync(cancellationToken);

        return rows.ConvertAll(r =>
        {
            var title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title;
            return new VaultFileDto(
                r.Id,
                r.Name,
                title,
                title ?? r.Name,
                VaultMediaKinds.From(r.Detected, r.MimeType),
                r.MimeType,
                r.SizeBytes,
                r.CreatedAt,
                r.Width,
                r.Height,
                r.HasSmall,
                r.HasPoster);
        });
    }

    private static IReadOnlyList<string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return Array.Empty<string>();
        }
        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
            return tags is null
                ? Array.Empty<string>()
                : tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    // ── move-in (normal → vault) ────────────────────────────────────────────

    // DB-only logical move. Marks the requested folders (+ ALL their active
    // descendant folders + files) and the requested files with the vault id.
    // Blob bytes are never touched. Idempotent: already-vault rows are excluded
    // by the global filter, so re-running moves nothing.
    public async Task<VaultMoveResult> MoveInAsync(
        Guid ownerUserId,
        Guid vaultId,
        IReadOnlyList<Guid> fileIds,
        IReadOnlyList<Guid> folderIds,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var movedFolders = 0;
            var movedFiles = 0;

            // Requested folders that are currently NORMAL (global filter) and
            // owned + active. Collect each one's full descendant folder subtree.
            var requestedFolders = folderIds.Count == 0
                ? new List<Guid>()
                : await _db.Folders.AsNoTracking()
                    .Where(f => folderIds.Contains(f.Id)
                        && f.OwnerUserId == ownerUserId
                        && f.DeletedAt == null)
                    .Select(f => f.Id)
                    .ToListAsync(cancellationToken);

            var allFolderIds = new HashSet<Guid>();
            foreach (var root in requestedFolders)
            {
                await CollectDescendantFolderIdsAsync(ownerUserId, root, allFolderIds, cancellationToken);
            }

            if (allFolderIds.Count > 0)
            {
                var folderList = allFolderIds.ToList();
                movedFolders = await _db.Folders
                    .Where(f => folderList.Contains(f.Id) && f.OwnerUserId == ownerUserId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.PrivateVaultId, _ => (Guid?)vaultId)
                        .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);

                // Files directly inside any moved folder (global filter → still
                // normal only). Descendant files of nested folders are covered
                // because every descendant folder is in folderList.
                movedFiles += await _db.FileItems
                    .Where(f => f.OwnerUserId == ownerUserId
                        && f.ParentFolderId != null
                        && folderList.Contains(f.ParentFolderId.Value))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.PrivateVaultId, _ => (Guid?)vaultId)
                        .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);
            }

            // Explicitly requested files (any already vaulted are excluded by the
            // global filter, so no double count with the folder pass above).
            if (fileIds.Count > 0)
            {
                var fileList = fileIds.ToList();
                movedFiles += await _db.FileItems
                    .Where(f => fileList.Contains(f.Id) && f.OwnerUserId == ownerUserId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(f => f.PrivateVaultId, _ => (Guid?)vaultId)
                        .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                        cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new VaultMoveResult(movedFiles, movedFolders);
        });
    }

    // ── move-out (vault → normal) ───────────────────────────────────────────

    // Restores the selected TOP-LEVEL vault items (and, for folders, their whole
    // vault subtree) to normal by clearing PrivateVaultId. Items return to their
    // original location (ParentFolderId was never changed on move-in). If a
    // top-level item would collide with an existing normal sibling name, it is
    // renamed with a numeric suffix so data is never trapped and no unique-index
    // violation occurs. Only top-level vault items are eligible; a mid-subtree
    // id is ignored (restoring it alone would orphan it under a hidden parent).
    public async Task<VaultMoveResult> MoveOutAsync(
        Guid ownerUserId,
        Guid vaultId,
        IReadOnlyList<Guid> fileIds,
        IReadOnlyList<Guid> folderIds,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            await TreeMutationLock.AcquireAsync(_db, ownerUserId, cancellationToken);

            var movedFolders = 0;
            var movedFiles = 0;

            // ---- top-level folders ----
            if (folderIds.Count > 0)
            {
                var topFolders = await _db.Folders.IgnoreQueryFilters()
                    .Where(f => folderIds.Contains(f.Id)
                        && f.OwnerUserId == ownerUserId
                        && f.PrivateVaultId == vaultId
                        && f.DeletedAt == null
                        && (f.ParentFolderId == null
                            || !_db.Folders.IgnoreQueryFilters().Any(p =>
                                p.Id == f.ParentFolderId && p.PrivateVaultId == vaultId)))
                    .ToListAsync(cancellationToken);

                foreach (var folder in topFolders)
                {
                    // Whole subtree back to normal.
                    var subtree = new HashSet<Guid>();
                    await CollectVaultDescendantFolderIdsAsync(
                        ownerUserId, vaultId, folder.Id, subtree, cancellationToken);
                    var subtreeList = subtree.ToList();

                    folder.Name = await ResolveNormalFolderNameAsync(
                        ownerUserId, folder.ParentFolderId, folder.Name, cancellationToken);
                    folder.PrivateVaultId = null;
                    folder.UpdatedAt = now;

                    // Descendant folders (excluding this top folder, updated above).
                    var descendantFolders = subtreeList.Where(id => id != folder.Id).ToList();
                    if (descendantFolders.Count > 0)
                    {
                        await _db.Folders.IgnoreQueryFilters()
                            .Where(f => descendantFolders.Contains(f.Id)
                                && f.OwnerUserId == ownerUserId
                                && f.PrivateVaultId == vaultId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(f => f.PrivateVaultId, _ => (Guid?)null)
                                .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                                cancellationToken);
                    }

                    // All files anywhere in the subtree.
                    movedFiles += await _db.FileItems.IgnoreQueryFilters()
                        .Where(f => f.OwnerUserId == ownerUserId
                            && f.PrivateVaultId == vaultId
                            && f.ParentFolderId != null
                            && subtreeList.Contains(f.ParentFolderId.Value))
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(f => f.PrivateVaultId, _ => (Guid?)null)
                            .SetProperty(f => f.UpdatedAt, _ => (DateTime?)now),
                            cancellationToken);

                    movedFolders += subtreeList.Count;
                }
                await _db.SaveChangesAsync(cancellationToken);
            }

            // ---- top-level files (parent not in vault) ----
            if (fileIds.Count > 0)
            {
                var topFiles = await _db.FileItems.IgnoreQueryFilters()
                    .Where(f => fileIds.Contains(f.Id)
                        && f.OwnerUserId == ownerUserId
                        && f.PrivateVaultId == vaultId
                        && f.DeletedAt == null
                        && (f.ParentFolderId == null
                            || !_db.Folders.IgnoreQueryFilters().Any(p =>
                                p.Id == f.ParentFolderId && p.PrivateVaultId == vaultId)))
                    .ToListAsync(cancellationToken);

                foreach (var file in topFiles)
                {
                    file.Name = await ResolveNormalFileNameAsync(
                        ownerUserId, file.ParentFolderId, file.Name, cancellationToken);
                    file.PrivateVaultId = null;
                    file.UpdatedAt = now;
                    movedFiles++;
                }
                await _db.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new VaultMoveResult(movedFiles, movedFolders);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // BFS over currently-NORMAL owned folders (global filter applies), adding
    // root + all active descendants to `into`.
    private async Task CollectDescendantFolderIdsAsync(
        Guid ownerUserId, Guid rootId, HashSet<Guid> into, CancellationToken cancellationToken)
    {
        if (!into.Add(rootId))
        {
            return;
        }
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var childIds = await _db.Folders.AsNoTracking()
                .Where(f => f.ParentFolderId == parentId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            foreach (var childId in childIds)
            {
                if (into.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
    }

    // BFS over owned folders that are IN the given vault (bypasses the global
    // filter), adding root + all in-vault descendants to `into`.
    private async Task CollectVaultDescendantFolderIdsAsync(
        Guid ownerUserId, Guid vaultId, Guid rootId, HashSet<Guid> into,
        CancellationToken cancellationToken)
    {
        if (!into.Add(rootId))
        {
            return;
        }
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var childIds = await _db.Folders.AsNoTracking().IgnoreQueryFilters()
                .Where(f => f.ParentFolderId == parentId
                    && f.OwnerUserId == ownerUserId
                    && f.PrivateVaultId == vaultId
                    && f.DeletedAt == null)
                .Select(f => f.Id)
                .ToListAsync(cancellationToken);
            foreach (var childId in childIds)
            {
                if (into.Add(childId))
                {
                    queue.Enqueue(childId);
                }
            }
        }
    }

    private async Task<string> ResolveNormalFolderNameAsync(
        Guid ownerUserId, Guid? parentFolderId, string desired, CancellationToken cancellationToken)
    {
        var taken = await _db.Folders.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
        return UniqueName(desired, new HashSet<string>(taken, StringComparer.Ordinal), hasExtension: false);
    }

    private async Task<string> ResolveNormalFileNameAsync(
        Guid ownerUserId, Guid? parentFolderId, string desired, CancellationToken cancellationToken)
    {
        var taken = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == ownerUserId
                && f.ParentFolderId == parentFolderId
                && f.DeletedAt == null)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);
        return UniqueName(desired, new HashSet<string>(taken, StringComparer.Ordinal), hasExtension: true);
    }

    private static string UniqueName(string desired, HashSet<string> taken, bool hasExtension)
    {
        if (!taken.Contains(desired))
        {
            return desired;
        }
        string stem = desired, ext = string.Empty;
        if (hasExtension)
        {
            var dot = desired.LastIndexOf('.');
            if (dot > 0)
            {
                stem = desired[..dot];
                ext = desired[dot..];
            }
        }
        for (var n = 1; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
        // Extremely unlikely fallback — a random suffix guarantees uniqueness.
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}
