using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Folders;
using NubArca.Api.Security;
using NubArca.Api.Storage;

namespace NubArca.Api.Albums.Sharing;

// SHARE-COPY-01: one-time detached album copies.
//
// THE ONE IDEA THIS FILE EXISTS TO PROTECT
// ----------------------------------------
// An accepted copy is the recipient's, permanently and unconditionally. Nothing
// the sender does afterwards may reach it: not editing the album, not deleting
// it, not deleting the source files, not having their account disabled. That is
// achieved by making acceptance read NOTHING from the source — every byte
// identity and every displayed field comes from the snapshot taken at send time.
// If you ever find yourself joining back to album_items or the sender's
// FileItems inside AcceptAsync, the guarantee is gone.
//
// RETENTION
// ---------
// A pending manifest OWNS one blob reference per row. That is what keeps the
// bytes alive when the sender permanently deletes the source between send and
// accept. The count is mirrored in BlobReferenceAuditService (one reference per
// album_transfer_items ROW of a PENDING transfer) — the two must agree, or
// `repair-references` will zero live references and the janitor will delete
// bytes a pending copy needs. On top of that, the BlobObject FK is Restrict, so
// the database refuses the delete even if the accounting ever drifted.
//
// ELIGIBILITY
// -----------
// Media contributed by ANOTHER user (SHARE-ALBUM-02) is never copyable. Their
// contribution is linked and revocable by design; putting it in a third party's
// permanently-owned album would place it beyond the revocation they were
// promised. Same for Private Vault content. Per the slice contract these
// REJECT the send with an explanation rather than being silently dropped.
public sealed class AlbumTransferService : IAlbumTransferService
{
    // How long a recipient has to answer before the offer lapses and its blob
    // references are released.
    private static readonly TimeSpan PendingWindow = TimeSpan.FromDays(30);

    // Where accepted media lands in the recipient's own tree. Mirrors the
    // "Party uploads" precedent: media that arrives from somebody else goes to
    // one predictable place rather than being scattered through the root.
    private const string ReceivedFolder = "Received albums";

    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    private readonly IBlobService _blobs;
    private readonly IFolderService _folders;
    private readonly IAuditLogger _audit;
    private readonly long _defaultUserQuotaBytes;

    public AlbumTransferService(
        AppDbContext db,
        TimeProvider time,
        IBlobService blobs,
        IFolderService folders,
        IAuditLogger audit,
        IOptions<BlobStorageOptions>? storageOptions = null)
    {
        _db = db;
        _time = time;
        _blobs = blobs;
        _folders = folders;
        _audit = audit;
        // Same convention as FileItemService: null options ⇒ 0 ⇒ unlimited.
        _defaultUserQuotaBytes = storageOptions?.Value.DefaultUserQuotaBytes ?? 0;
    }

    // ── Sender ──────────────────────────────────────────────────────────────

    public async Task<AlbumTransferPreview?> PreviewAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            return null;
        }

        var (eligible, blockers) = await ClassifyAsync(ownerUserId, albumId, cancellationToken);
        return new AlbumTransferPreview(
            album.Name,
            eligible.Count,
            eligible.Sum(x => x.SizeBytes),
            blockers);
    }

    public async Task<(AlbumTransferSendResult Result, SentAlbumTransferDto? Transfer, IReadOnlyList<AlbumTransferBlocker> Blockers)>
        SendAsync(
            Guid ownerUserId, Guid albumId, string? recipientEmail,
            CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AlbumTransferBlocker> none = [];

        var album = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId && a.OwnerUserId == ownerUserId)
            .Select(a => new { a.Name, a.Description, a.CoverFileItemId })
            .FirstOrDefaultAsync(cancellationToken);
        if (album is null)
        {
            // Same result for "not yours" and "does not exist": the route must
            // not be usable to discover album ids.
            return (AlbumTransferSendResult.AlbumNotFound, null, none);
        }

        var normalized = NormalizeEmail(recipientEmail);
        if (normalized is null)
        {
            return (AlbumTransferSendResult.RecipientNotFound, null, none);
        }

        var recipient = await _db.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == normalized && u.DisabledAt == null)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        if (recipient is null)
        {
            return (AlbumTransferSendResult.RecipientNotFound, null, none);
        }
        if (recipient.Id == ownerUserId)
        {
            return (AlbumTransferSendResult.RecipientIsSender, null, none);
        }

        var (eligible, blockers) = await ClassifyAsync(ownerUserId, albumId, cancellationToken);
        if (blockers.Count > 0)
        {
            // Never a silent omission — the owner is told what stopped it.
            return (AlbumTransferSendResult.ContainsIneligibleItems, null, blockers);
        }
        if (eligible.Count == 0)
        {
            return (AlbumTransferSendResult.EmptyAlbum, null, none);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var transfer = new AlbumTransfer
        {
            Id = Guid.NewGuid(),
            SourceAlbumId = albumId,
            SenderUserId = ownerUserId,
            RecipientUserId = recipient.Id,
            Title = album.Name,
            Description = album.Description,
            ItemCount = eligible.Count,
            TotalSizeBytes = eligible.Sum(x => x.SizeBytes),
            State = AlbumTransferStates.Pending,
            CreatedAt = now,
            ExpiresAt = now + PendingWindow,
            UpdatedAt = now,
        };

        var items = new List<AlbumTransferItem>(eligible.Count);
        for (var i = 0; i < eligible.Count; i++)
        {
            var src = eligible[i];
            items.Add(new AlbumTransferItem
            {
                Id = Guid.NewGuid(),
                AlbumTransferId = transfer.Id,
                SortOrder = i,
                BlobObjectId = src.BlobObjectId,
                SourceFileItemId = src.FileItemId,
                Name = src.Name,
                MimeType = src.MimeType,
                SizeBytes = src.SizeBytes,
                Width = src.Width,
                Height = src.Height,
                EffectiveDateTaken = src.EffectiveDateTaken,
            });
        }

        // The cover is snapshotted as a MANIFEST item, not a live FileItemId —
        // the source file may be gone by the time the recipient accepts.
        if (album.CoverFileItemId is Guid coverFileId)
        {
            transfer.CoverTransferItemId = items
                .FirstOrDefault(x => x.SourceFileItemId == coverFileId)?.Id;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var duplicate = false;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            _db.AlbumTransfers.Add(transfer);
            _db.AlbumTransferItems.AddRange(items);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // ux_album_transfers_pending_album_recipient: a concurrent send
                // already created the pending offer. Not an error worth a stack
                // trace — the caller is told it is already pending.
                await tx.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                duplicate = true;
                return;
            }

            // Acquire ONE reference per manifest ROW, matching exactly what
            // BlobReferenceAuditService recomputes. Inside the transaction, so a
            // failure here cannot leave a manifest holding unowned bytes.
            foreach (var item in items)
            {
                await _blobs.AcquireExistingAsync(item.BlobObjectId, cancellationToken);
            }

            await _audit.WriteAsync(
                ownerUserId, AuditActions.AlbumTransferSend,
                AuditEntityTypes.AlbumTransfer, transfer.Id, null,
                new
                {
                    recipientUserId = recipient.Id,
                    sourceAlbumId = albumId,
                    itemCount = transfer.ItemCount,
                    totalSizeBytes = transfer.TotalSizeBytes,
                },
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
        });

        if (duplicate)
        {
            return (AlbumTransferSendResult.AlreadyPending, null, none);
        }

        return (
            AlbumTransferSendResult.Ok,
            ToSentDto(transfer, recipient.DisplayName, recipient.Email),
            none);
    }

    public async Task<IReadOnlyList<SentAlbumTransferDto>> ListSentAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.AlbumTransfers.AsNoTracking()
            .Where(t => t.SenderUserId == ownerUserId)
            .Join(_db.Users.AsNoTracking(),
                t => t.RecipientUserId, u => u.Id,
                (t, u) => new { Transfer = t, u.DisplayName, u.Email })
            .OrderByDescending(x => x.Transfer.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => ToSentDto(x.Transfer, x.DisplayName, x.Email))
            .ToList();
    }

    public async Task<AlbumTransferResponseResult> CancelAsync(
        Guid senderUserId, Guid transferId, CancellationToken cancellationToken = default)
    {
        var result = AlbumTransferResponseResult.NotFound;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var transfer = await _db.AlbumTransfers
                .FirstOrDefaultAsync(
                    t => t.Id == transferId && t.SenderUserId == senderUserId,
                    cancellationToken);
            if (transfer is null)
            {
                result = AlbumTransferResponseResult.NotFound;
                return;
            }
            if (transfer.State == AlbumTransferStates.Accepted)
            {
                // Already theirs. A copy is never recallable — this is the whole
                // point of "detached".
                result = AlbumTransferResponseResult.AlreadyResolved;
                return;
            }
            if (transfer.State != AlbumTransferStates.Pending)
            {
                result = AlbumTransferResponseResult.AlreadyResolved;
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            // Conditional claim first: a cancel racing an accept must lose
            // cleanly rather than releasing references out from under a copy
            // that is being materialised.
            var claimed = await _db.AlbumTransfers
                .Where(t => t.Id == transfer.Id && t.State == AlbumTransferStates.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, AlbumTransferStates.Cancelled)
                    .SetProperty(t => t.CancelledAt, now)
                    .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken);
            if (claimed != 1)
            {
                result = AlbumTransferResponseResult.AlreadyResolved;
                await tx.RollbackAsync(cancellationToken);
                return;
            }
            _db.Entry(transfer).State = EntityState.Detached;

            await ReleaseManifestAsync(transfer.Id, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                senderUserId, AuditActions.AlbumTransferCancel,
                AuditEntityTypes.AlbumTransfer, transfer.Id, null,
                new { recipientUserId = transfer.RecipientUserId, itemCount = transfer.ItemCount },
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            result = AlbumTransferResponseResult.Ok;
        });
        return result;
    }

    // ── Recipient ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ReceivedAlbumTransferDto>> ListReceivedAsync(
        Guid recipientUserId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var rows = await _db.AlbumTransfers.AsNoTracking()
            .Where(t => t.RecipientUserId == recipientUserId)
            // A cancelled offer is withdrawn: the recipient never gets to see
            // that it briefly existed.
            .Where(t => t.State != AlbumTransferStates.Cancelled)
            // A lapsed offer, or one whose sender has since been disabled, reads
            // as gone even before the sweep runs — the recipient is never shown
            // an Accept button that would fail.
            .Where(t => t.State != AlbumTransferStates.Pending
                || (t.ExpiresAt > now
                    && _db.Users.Any(u => u.Id == t.SenderUserId && u.DisabledAt == null)))
            .Join(_db.Users.AsNoTracking(),
                t => t.SenderUserId, u => u.Id,
                (t, u) => new { Transfer = t, u.DisplayName, u.Email })
            .OrderByDescending(x => x.Transfer.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new ReceivedAlbumTransferDto(
                x.Transfer.Id,
                x.Transfer.Title,
                x.Transfer.Description,
                x.DisplayName,
                RecipientEmailMask.Mask(x.Email),
                x.Transfer.ItemCount,
                x.Transfer.TotalSizeBytes,
                x.Transfer.State,
                x.Transfer.CreatedAt,
                x.Transfer.ExpiresAt,
                x.Transfer.CreatedAlbumId))
            .ToList();
    }

    public async Task<AlbumTransferAcceptance> AcceptAsync(
        Guid recipientUserId, Guid transferId, CancellationToken cancellationToken = default)
    {
        var outcome = new AlbumTransferAcceptance(AlbumTransferResponseResult.NotFound, null);

        // The destination folder is ensured OUTSIDE the acceptance transaction:
        // it is the recipient's own tree, idempotent, and harmless if the
        // acceptance later rolls back (an empty folder, not a partial album).
        var folderId = await _folders.EnsureFolderPathAsync(
            recipientUserId, null, [ReceivedFolder], cancellationToken);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
            // Same per-owner lock the upload path uses, so a concurrent upload
            // and a concurrent acceptance cannot both observe "under quota".
            await TreeMutationLock.AcquireAsync(_db, recipientUserId, cancellationToken);

            var transfer = await _db.AlbumTransfers
                .FirstOrDefaultAsync(
                    t => t.Id == transferId && t.RecipientUserId == recipientUserId,
                    cancellationToken);
            if (transfer is null)
            {
                // Not addressed to this user, or no such transfer. A transfer id
                // must never confirm its own existence to a stranger.
                outcome = new AlbumTransferAcceptance(AlbumTransferResponseResult.NotFound, null);
                return;
            }

            // IDEMPOTENCE. A retried request — double click, flaky network,
            // client retry — returns the album that already exists rather than
            // creating a second one.
            if (transfer.State == AlbumTransferStates.Accepted)
            {
                outcome = new AlbumTransferAcceptance(
                    AlbumTransferResponseResult.Ok, transfer.CreatedAlbumId);
                return;
            }
            if (transfer.State == AlbumTransferStates.Cancelled)
            {
                outcome = new AlbumTransferAcceptance(AlbumTransferResponseResult.Cancelled, null);
                return;
            }
            if (transfer.State != AlbumTransferStates.Pending)
            {
                outcome = new AlbumTransferAcceptance(
                    transfer.State == AlbumTransferStates.Expired
                        ? AlbumTransferResponseResult.Expired
                        : AlbumTransferResponseResult.AlreadyResolved,
                    null);
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            if (transfer.ExpiresAt <= now)
            {
                outcome = new AlbumTransferAcceptance(AlbumTransferResponseResult.Expired, null);
                return;
            }

            // The sender must STILL be active. Disablement can be the response
            // to a compromised account, and completing a pending transfer would
            // carry out an operation that account originated after it was shut
            // off. Re-checked here rather than trusted from send time.
            var senderActive = await _db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == transfer.SenderUserId && u.DisabledAt == null,
                    cancellationToken);
            if (!senderActive)
            {
                outcome = new AlbumTransferAcceptance(
                    AlbumTransferResponseResult.SenderUnavailable, null);
                return;
            }

            var manifest = await _db.AlbumTransferItems.AsNoTracking()
                .Where(i => i.AlbumTransferId == transfer.Id)
                .OrderBy(i => i.SortOrder)
                .ToListAsync(cancellationToken);

            // QUOTA. The recipient's normal limit, enforced on LOGICAL bytes
            // exactly like an upload — dedup deliberately buys them nothing,
            // because the copy is their own file from here on.
            if (_defaultUserQuotaBytes > 0)
            {
                var usedBytes = await _db.FileItems
                    .IgnoreQueryFilters()
                    .Where(f => f.OwnerUserId == recipientUserId)
                    .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0L;
                var required = manifest.Sum(i => i.SizeBytes);
                if (usedBytes + required > _defaultUserQuotaBytes)
                {
                    // Nothing has been written yet, and the transaction rolls
                    // back regardless: there is no partial album to clean up.
                    outcome = new AlbumTransferAcceptance(
                        AlbumTransferResponseResult.QuotaExceeded, null,
                        RequiredBytes: required,
                        RemainingBytes: Math.Max(0, _defaultUserQuotaBytes - usedBytes));
                    return;
                }
            }

            // CLAIM THE TRANSITION FIRST, conditionally on the row still being
            // pending. Two concurrent accepts (double click, client retry, two
            // tabs) both reach here; exactly one updates a row, and the loser
            // creates nothing. The recipient's tree lock already serialises them
            // on PostgreSQL — this makes it correct without relying on that, and
            // is the same shape as SHARE-ALBUM-03's version claim.
            var albumId = Guid.NewGuid();
            var claimed = await _db.AlbumTransfers
                .Where(t => t.Id == transfer.Id && t.State == AlbumTransferStates.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, AlbumTransferStates.Accepted)
                    .SetProperty(t => t.CreatedAlbumId, albumId)
                    .SetProperty(t => t.RespondedAt, now)
                    .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken);
            if (claimed != 1)
            {
                // Somebody else resolved it between our read and this write.
                // Re-read to answer with what actually happened rather than
                // guessing — a concurrent accept by this same recipient must
                // still look idempotent.
                _db.ChangeTracker.Clear();
                var current = await _db.AlbumTransfers.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == transferId, cancellationToken);
                outcome = current?.State switch
                {
                    AlbumTransferStates.Accepted => new AlbumTransferAcceptance(
                        AlbumTransferResponseResult.Ok, current.CreatedAlbumId),
                    AlbumTransferStates.Cancelled => new AlbumTransferAcceptance(
                        AlbumTransferResponseResult.Cancelled, null),
                    AlbumTransferStates.Expired => new AlbumTransferAcceptance(
                        AlbumTransferResponseResult.Expired, null),
                    _ => new AlbumTransferAcceptance(
                        AlbumTransferResponseResult.AlreadyResolved, null),
                };
                await tx.RollbackAsync(cancellationToken);
                return;
            }
            // Our own tracked copy must not fight the ExecuteUpdate on save.
            _db.Entry(transfer).State = EntityState.Detached;

            var album = new Album
            {
                Id = albumId,
                OwnerUserId = recipientUserId,
                Name = transfer.Title,
                Description = transfer.Description,
                // Never inherited: sharing, publication and TV exposure are
                // decisions for the new owner to make from scratch.
                ShowOnTv = false,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Albums.Add(album);

            // Names must be unique among ACTIVE siblings
            // (ux_file_items_active_sibling_name). Two accepted copies that
            // happen to contain "IMG_0001.jpg" both land in the same folder, and
            // so do two items with the same name inside ONE album — either
            // collides and aborts the whole acceptance. Mirrors the vault's
            // ResolveNormalFileNameAsync: read the folder's taken names once,
            // then reserve each new one as we go.
            var taken = new HashSet<string>(
                await _db.FileItems.AsNoTracking()
                    .Where(f => f.OwnerUserId == recipientUserId
                        && f.ParentFolderId == folderId
                        && f.DeletedAt == null)
                    .Select(f => f.Name)
                    .ToListAsync(cancellationToken),
                StringComparer.Ordinal);

            for (var i = 0; i < manifest.Count; i++)
            {
                var snap = manifest[i];
                var name = UniqueName(snap.Name, taken);
                taken.Add(name);
                var file = new FileItem
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = recipientUserId,
                    ParentFolderId = folderId,
                    // Same physical bytes — dedup preserved, nothing re-hashed
                    // or rewritten. Logical ownership is entirely separate.
                    BlobObjectId = snap.BlobObjectId,
                    // Never inherited: the vault is per-owner, and a copy always
                    // arrives as ordinary visible content.
                    PrivateVaultId = null,
                    MediaLibraryState = MediaLibraryState.Active,
                    Name = name,
                    MimeType = snap.MimeType,
                    SizeBytes = snap.SizeBytes,
                    Width = snap.Width,
                    Height = snap.Height,
                    CreatedAt = now,
                    EffectiveDateTaken = snap.EffectiveDateTaken,
                    EffectiveDateTakenSource = "embedded",
                };
                _db.FileItems.Add(file);

                _db.AlbumItems.Add(new AlbumItem
                {
                    Id = Guid.NewGuid(),
                    AlbumId = album.Id,
                    FileItemId = file.Id,
                    // The recipient owns the file, so the SHARE-ALBUM-02
                    // invariant AddedByUserId == FileItem.OwnerUserId holds.
                    AddedByUserId = recipientUserId,
                    SortOrder = i,
                    AddedAt = now,
                });

                if (transfer.CoverTransferItemId == snap.Id)
                {
                    album.CoverFileItemId = file.Id;
                }

                // The new FileItem owns its own reference. Acquired BEFORE the
                // manifest releases, so the count never dips toward zero and the
                // janitor can never see these bytes as unreferenced.
                await _blobs.AcquireExistingAsync(snap.BlobObjectId, cancellationToken);
            }

            // Released only AFTER the recipient's own references are acquired
            // above, and inside this same transaction: there is never a window
            // where the manifest has let go but the recipient's rows are not yet
            // durable.
            await ReleaseManifestAsync(transfer.Id, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                recipientUserId, AuditActions.AlbumTransferAccept,
                AuditEntityTypes.AlbumTransfer, transfer.Id, null,
                new
                {
                    senderUserId = transfer.SenderUserId,
                    createdAlbumId = album.Id,
                    itemCount = manifest.Count,
                    totalSizeBytes = transfer.TotalSizeBytes,
                },
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            outcome = new AlbumTransferAcceptance(AlbumTransferResponseResult.Ok, album.Id);
        });

        return outcome;
    }

    public async Task<AlbumTransferResponseResult> DeclineAsync(
        Guid recipientUserId, Guid transferId, CancellationToken cancellationToken = default)
    {
        var result = AlbumTransferResponseResult.NotFound;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

            var transfer = await _db.AlbumTransfers
                .FirstOrDefaultAsync(
                    t => t.Id == transferId && t.RecipientUserId == recipientUserId,
                    cancellationToken);
            if (transfer is null)
            {
                result = AlbumTransferResponseResult.NotFound;
                return;
            }
            if (transfer.State == AlbumTransferStates.Cancelled)
            {
                result = AlbumTransferResponseResult.Cancelled;
                return;
            }
            if (transfer.State != AlbumTransferStates.Pending)
            {
                result = AlbumTransferResponseResult.AlreadyResolved;
                return;
            }

            var now = _time.GetUtcNow().UtcDateTime;
            var claimed = await _db.AlbumTransfers
                .Where(t => t.Id == transfer.Id && t.State == AlbumTransferStates.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, AlbumTransferStates.Declined)
                    .SetProperty(t => t.RespondedAt, now)
                    .SetProperty(t => t.UpdatedAt, now),
                    cancellationToken);
            if (claimed != 1)
            {
                result = AlbumTransferResponseResult.AlreadyResolved;
                await tx.RollbackAsync(cancellationToken);
                return;
            }
            _db.Entry(transfer).State = EntityState.Detached;

            await ReleaseManifestAsync(transfer.Id, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            await _audit.WriteAsync(
                recipientUserId, AuditActions.AlbumTransferDecline,
                AuditEntityTypes.AlbumTransfer, transfer.Id, null,
                new { senderUserId = transfer.SenderUserId, itemCount = transfer.ItemCount },
                cancellationToken);

            await tx.CommitAsync(cancellationToken);
            result = AlbumTransferResponseResult.Ok;
        });
        return result;
    }

    // ── Maintenance ─────────────────────────────────────────────────────────

    public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expired = 0;

        while (true)
        {
            // One at a time, each in its own transaction: a large backlog must
            // not hold a long transaction open, and a failure on one transfer
            // must not roll back the ones already released.
            // Two reasons a pending transfer is dead: its window elapsed, or its
            // SENDER was disabled. The second is a security rule, not a timeout
            // — a disabled account's pending operations must not be completable
            // — so the sweep releases those references too rather than leaving
            // bytes pinned by an offer that can never be accepted.
            var id = await _db.AlbumTransfers.AsNoTracking()
                .Where(t => t.State == AlbumTransferStates.Pending)
                .Where(t => t.ExpiresAt <= now
                    || _db.Users.Any(u => u.Id == t.SenderUserId && u.DisabledAt != null))
                .OrderBy(t => t.ExpiresAt)
                .Select(t => (Guid?)t.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (id is null)
            {
                break;
            }

            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

                var transfer = await _db.AlbumTransfers
                    .FirstOrDefaultAsync(
                        t => t.Id == id.Value && t.State == AlbumTransferStates.Pending,
                        cancellationToken);
                if (transfer is null)
                {
                    // Answered between the scan and the lock. Nothing to do.
                    return;
                }

                var claimed = await _db.AlbumTransfers
                    .Where(t => t.Id == transfer.Id && t.State == AlbumTransferStates.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.State, AlbumTransferStates.Expired)
                        .SetProperty(t => t.UpdatedAt, now),
                        cancellationToken);
                if (claimed != 1)
                {
                    // A recipient answered while we were claiming. Their
                    // decision wins; leave the references to their path.
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }
                _db.Entry(transfer).State = EntityState.Detached;

                await ReleaseManifestAsync(transfer.Id, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                // No actor: nobody decided this. Recording it as a decline would
                // put words in the recipient's mouth.
                await _audit.WriteAsync(
                    null, AuditActions.AlbumTransferExpire,
                    AuditEntityTypes.AlbumTransfer, transfer.Id, null,
                    new
                    {
                        senderUserId = transfer.SenderUserId,
                        recipientUserId = transfer.RecipientUserId,
                        itemCount = transfer.ItemCount,
                    },
                    cancellationToken);

                await tx.CommitAsync(cancellationToken);
                expired++;
            });
            _db.ChangeTracker.Clear();
        }

        return expired;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    // Releases the one reference each manifest row owns. Called whenever a
    // transfer leaves Pending, in the SAME transaction as the state change, so
    // "pending" and "owns references" can never disagree — which is exactly the
    // agreement BlobReferenceAuditService recomputes.
    private async Task ReleaseManifestAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var blobIds = await _db.AlbumTransferItems.AsNoTracking()
            .Where(i => i.AlbumTransferId == transferId)
            .Select(i => i.BlobObjectId)
            .ToListAsync(cancellationToken);

        // Per ROW, not per distinct blob: an album holding the same bytes twice
        // acquired two references at send time.
        foreach (var blobId in blobIds)
        {
            await _blobs.ReleaseAsync(blobId, cancellationToken);
        }
    }

    private sealed record EligibleItem(
        Guid FileItemId, Guid BlobObjectId, string Name, string MimeType,
        long SizeBytes, int? Width, int? Height, DateTime EffectiveDateTaken);

    // Splits the album into what CAN be copied and what BLOCKS the copy.
    //
    // IgnoreQueryFilters is essential: the global vault filter would otherwise
    // make vaulted items simply vanish from the join, which is precisely the
    // silent omission the slice contract forbids. We must SEE them to refuse.
    private async Task<(List<EligibleItem> Eligible, IReadOnlyList<AlbumTransferBlocker> Blockers)>
        ClassifyAsync(Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        // Two explicit queries matched in memory, rather than a
        // GroupJoin/DefaultIfEmpty LEFT JOIN. Not a bug fix — the join worked —
        // but IgnoreQueryFilters on the INNER sequence of a join is subtle
        // enough that it is worth not depending on, and getting it wrong would
        // make vaulted media vanish rather than be refused. An album is a
        // handful of rows, so the explicit form costs nothing.
        var albumRows = await _db.AlbumItems.AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Select(ai => new { ai.FileItemId, ai.SortOrder, ai.AddedAt })
            .ToListAsync(cancellationToken);

        var fileIds = albumRows.Select(r => r.FileItemId).Distinct().ToList();
        var files = await _db.FileItems.AsNoTracking()
            // Vaulted media MUST be visible to this query. The global filter
            // would make it vanish from the album entirely, and a send that
            // silently omitted it is exactly what the slice contract forbids —
            // we have to SEE it in order to refuse.
            .IgnoreQueryFilters()
            .Where(f => fileIds.Contains(f.Id))
            .ToListAsync(cancellationToken);
        var byId = files.ToDictionary(f => f.Id);

        var eligible = new List<EligibleItem>();
        var counts = new Dictionary<string, int>();
        void Block(string reason) =>
            counts[reason] = counts.TryGetValue(reason, out var c) ? c + 1 : 1;

        foreach (var row in albumRows.OrderBy(r => r.SortOrder).ThenBy(r => r.AddedAt))
        {
            if (!byId.TryGetValue(row.FileItemId, out var f))
            {
                Block(AlbumTransferBlockReasons.Unavailable);
                continue;
            }
            // Ownership first: a contributor's item is the most important refusal
            // to report, and it stays theirs even if it is also vaulted.
            if (f.OwnerUserId != ownerUserId)
            {
                Block(AlbumTransferBlockReasons.ContributedByAnotherUser);
                continue;
            }
            if (f.PrivateVaultId is not null)
            {
                Block(AlbumTransferBlockReasons.InPrivateVault);
                continue;
            }
            if (f.DeletedAt is not null)
            {
                Block(AlbumTransferBlockReasons.Trashed);
                continue;
            }

            eligible.Add(new EligibleItem(
                f.Id, f.BlobObjectId, f.Name, f.MimeType, f.SizeBytes,
                f.Width, f.Height, f.EffectiveDateTaken));
        }

        var blockers = counts
            .OrderBy(kv => kv.Key)
            .Select(kv => new AlbumTransferBlocker(kv.Key, kv.Value))
            .ToList();
        return (eligible, blockers);
    }

    private static SentAlbumTransferDto ToSentDto(AlbumTransfer t, string displayName, string? email) =>
        new(t.Id, t.SourceAlbumId, t.Title, displayName, RecipientEmailMask.Mask(email),
            t.ItemCount, t.TotalSizeBytes, t.State,
            t.CreatedAt, t.ExpiresAt, t.RespondedAt, t.CancelledAt);

    // Same shape as PrivateVaultService.UniqueName: append " (n)" before the
    // extension until the name is free among active siblings.
    private static string UniqueName(string desired, HashSet<string> taken)
    {
        if (!taken.Contains(desired))
        {
            return desired;
        }
        string stem = desired, ext = string.Empty;
        var dot = desired.LastIndexOf('.');
        if (dot > 0)
        {
            stem = desired[..dot];
            ext = desired[dot..];
        }
        for (var n = 1; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }
        var trimmed = email.Trim().ToLowerInvariant();
        return trimmed.Contains('@') ? trimmed : null;
    }
}
