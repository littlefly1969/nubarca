using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Files;
using NubArca.Api.Jobs;

namespace NubArca.Api.MailArchive;

public enum MailStartOutcome { Started, NotFound, NotPst, AlreadyRunning, SourceUnavailable, Unavailable }

public sealed record MailStartResult(MailStartOutcome Outcome, MailArchiveDto? Archive = null, MailRunDto? Run = null, Guid? RunningRunId = null);

/// <summary>
/// Owner-private archive and run management: which PST files can be imported,
/// starting/resuming an import, run progress and cancellation. Every query is
/// scoped by the authenticated owner; a foreign id is indistinguishable from
/// a missing one.
/// </summary>
public sealed class MailArchiveService
{
    private const int MaxCandidates = 200;
    private static readonly byte[] PstSignature = "!BDN"u8.ToArray();

    private readonly AppDbContext _db;
    private readonly IJobQueue _jobs;
    private readonly IPstEngine _engine;
    private readonly MailWorkspace _workspace;
    private readonly IFileItemService _files;
    private readonly string? _pepper;
    private readonly TimeProvider _clock;

    public MailArchiveService(
        AppDbContext db,
        IJobQueue jobs,
        IPstEngine engine,
        MailWorkspace workspace,
        IFileItemService files,
        IOptions<DeletedContentOptions> deletedContent,
        TimeProvider clock)
    {
        _db = db;
        _jobs = jobs;
        _engine = engine;
        _workspace = workspace;
        _files = files;
        _pepper = deletedContent.Value.Pepper;
        _clock = clock;
    }

    public MailStatusDto Status(MailArchiveOptions options) =>
        !options.Enabled ? new MailStatusDto(false, "disabled")
        : !_engine.IsAvailable || !_workspace.IsConfigured ? new MailStatusDto(false, "importer_missing")
        : new MailStatusDto(true, "ok");

    public async Task<IReadOnlyList<MailSourceCandidateDto>> ListSourcesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var files = await _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == owner && f.DeletedAt == null && f.Name.ToLower().EndsWith(".pst"))
            .OrderByDescending(f => f.CreatedAt)
            .Take(MaxCandidates)
            .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id,
                (f, b) => new { f.Id, f.Name, f.SizeBytes, f.CreatedAt, b.Sha256 })
            .ToListAsync(cancellationToken);
        var fingerprints = files.ToDictionary(f => f.Id, f => ContentFingerprint.Compute(_pepper, f.Sha256));
        var values = fingerprints.Values.Distinct().ToList();
        var archives = await _db.MailArchives.AsNoTracking()
            .Where(a => a.OwnerUserId == owner && values.Contains(a.SourceFingerprint))
            .Select(a => new { a.Id, a.SourceFingerprint })
            .ToListAsync(cancellationToken);
        var byFingerprint = archives.ToDictionary(a => a.SourceFingerprint, a => a.Id);
        return files
            .Select(f => new MailSourceCandidateDto(
                f.Id, f.Name, f.SizeBytes, f.CreatedAt,
                byFingerprint.TryGetValue(fingerprints[f.Id], out var archiveId) ? archiveId : null))
            .ToList();
    }

    public async Task<MailStartResult> StartFromFileAsync(Guid owner, Guid fileItemId, CancellationToken cancellationToken)
    {
        if (!_engine.IsAvailable || !_workspace.IsConfigured)
        {
            return new MailStartResult(MailStartOutcome.Unavailable);
        }
        var file = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileItemId && f.OwnerUserId == owner && f.DeletedAt == null)
            .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id,
                (f, b) => new { f.Id, f.Name, f.SizeBytes, b.Sha256 })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null)
        {
            return new MailStartResult(MailStartOutcome.NotFound);
        }
        if (!await HasPstSignatureAsync(owner, file.Id, cancellationToken))
        {
            return new MailStartResult(MailStartOutcome.NotPst);
        }

        var fingerprint = ContentFingerprint.Compute(_pepper, file.Sha256);
        var archive = await _db.MailArchives
            .FirstOrDefaultAsync(a => a.OwnerUserId == owner && a.SourceFingerprint == fingerprint, cancellationToken);
        var now = Now;
        if (archive is null)
        {
            archive = new Domain.Mail.MailArchive
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                SourceFingerprint = fingerprint,
                FingerprintScheme = ContentFingerprint.Scheme,
                SourceFileItemId = file.Id,
                SourceFileName = Truncate(file.Name, 255),
                SourceSizeBytes = file.SizeBytes,
                DisplayName = Truncate(DisplayNameFor(file.Name), 255),
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.MailArchives.Add(archive);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent start of the same PST created it first.
                _db.ChangeTracker.Clear();
                archive = await _db.MailArchives.FirstAsync(
                    a => a.OwnerUserId == owner && a.SourceFingerprint == fingerprint, cancellationToken);
            }
        }
        else if (archive.SourceFileItemId != file.Id)
        {
            // Same bytes, another copy of the file: import from this one.
            archive.SourceFileItemId = file.Id;
            archive.SourceFileName = Truncate(file.Name, 255);
            archive.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }
        return await EnqueueRunAsync(archive, cancellationToken);
    }

    public async Task<MailStartResult> ReimportAsync(Guid owner, Guid archiveId, CancellationToken cancellationToken)
    {
        if (!_engine.IsAvailable || !_workspace.IsConfigured)
        {
            return new MailStartResult(MailStartOutcome.Unavailable);
        }
        var archive = await _db.MailArchives.FirstOrDefaultAsync(a => a.Id == archiveId && a.OwnerUserId == owner, cancellationToken);
        if (archive is null)
        {
            return new MailStartResult(MailStartOutcome.NotFound);
        }
        if (!await IsSourceAvailableAsync(archive, cancellationToken))
        {
            return new MailStartResult(MailStartOutcome.SourceUnavailable);
        }
        return await EnqueueRunAsync(archive, cancellationToken);
    }

    private async Task<MailStartResult> EnqueueRunAsync(Domain.Mail.MailArchive archive, CancellationToken cancellationToken)
    {
        var active = await ActiveRunAsync(archive.Id, cancellationToken);
        if (active is not null)
        {
            return new MailStartResult(MailStartOutcome.AlreadyRunning, RunningRunId: active.Id);
        }

        var now = Now;
        var run = new MailImportRun
        {
            Id = Guid.NewGuid(),
            ArchiveId = archive.Id,
            OwnerUserId = archive.OwnerUserId,
            Status = MailImportRunStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MailImportRuns.Add(run);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The one-active-run index arbitrated a concurrent start.
            _db.ChangeTracker.Clear();
            var winner = await ActiveRunAsync(archive.Id, cancellationToken);
            return new MailStartResult(MailStartOutcome.AlreadyRunning, RunningRunId: winner?.Id);
        }

        var job = await _jobs.EnqueueAsync(
            JobTypes.MailImport,
            new MailImportJobPayload(run.Id),
            idempotencyKey: $"mail.import:{run.Id:N}",
            cancellationToken: cancellationToken);
        run.JobId = job.Id;
        await _db.SaveChangesAsync(cancellationToken);

        var archiveDto = await ToDtoAsync(archive, run, cancellationToken);
        return new MailStartResult(MailStartOutcome.Started, archiveDto, await ToRunDtoAsync(run, cancellationToken));
    }

    public async Task<IReadOnlyList<MailArchiveDto>> ListArchivesAsync(Guid owner, CancellationToken cancellationToken)
    {
        var archives = await _db.MailArchives.AsNoTracking()
            .Where(a => a.OwnerUserId == owner)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        var result = new List<MailArchiveDto>(archives.Count);
        foreach (var archive in archives)
        {
            result.Add(await ToDtoAsync(archive, null, cancellationToken));
        }
        return result;
    }

    public async Task<MailArchiveDto?> GetArchiveAsync(Guid owner, Guid archiveId, CancellationToken cancellationToken)
    {
        var archive = await _db.MailArchives.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == archiveId && a.OwnerUserId == owner, cancellationToken);
        return archive is null ? null : await ToDtoAsync(archive, null, cancellationToken);
    }

    public async Task<IReadOnlyList<MailFolderDto>?> ListFoldersAsync(Guid owner, Guid archiveId, CancellationToken cancellationToken)
    {
        if (!await _db.MailArchives.AnyAsync(a => a.Id == archiveId && a.OwnerUserId == owner, cancellationToken))
        {
            return null;
        }
        return await _db.MailFolders.AsNoTracking()
            .Where(f => f.ArchiveId == archiveId && f.OwnerUserId == owner)
            .OrderBy(f => f.Ordinal)
            .Select(f => new MailFolderDto(f.Id, f.ParentId, f.Name, f.Depth, f.MessageCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<MailRunDto?> GetRunAsync(Guid owner, Guid runId, CancellationToken cancellationToken)
    {
        var run = await _db.MailImportRuns.FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == owner, cancellationToken);
        return run is null ? null : await ToRunDtoAsync(run, cancellationToken);
    }

    public async Task<(bool Found, bool Active, MailRunDto? Run)> CancelRunAsync(Guid owner, Guid runId, CancellationToken cancellationToken)
    {
        var run = await _db.MailImportRuns.FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == owner, cancellationToken);
        if (run is null)
        {
            return (false, false, null);
        }
        await ReconcileAsync(run, cancellationToken);
        if (!MailImportRunStatuses.IsActive(run.Status) || run.JobId is not Guid jobId)
        {
            return (true, false, await ToRunDtoAsync(run, cancellationToken));
        }
        await _jobs.RequestCancellationAsync(jobId, cancellationToken);
        return (true, true, await ToRunDtoAsync(run, cancellationToken));
    }

    public async Task<MailFailurePage?> ListFailuresAsync(Guid owner, Guid runId, string? cursor, CancellationToken cancellationToken)
    {
        var run = await _db.MailImportRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.OwnerUserId == owner, cancellationToken);
        if (run is null)
        {
            return null;
        }
        const int pageSize = 100;
        var after = int.TryParse(cursor, out var parsed) ? parsed : 0;
        var rows = await _db.MailImportItems.AsNoTracking()
            .Where(i => i.ArchiveId == run.ArchiveId && i.OwnerUserId == owner
                && i.Status == MailImportItemStatuses.Failed && i.Ordinal > after)
            .OrderBy(i => i.Ordinal)
            .Take(pageSize + 1)
            .Select(i => new { i.Ordinal, i.SourceFolderKey, i.FailureCode, i.FailureMessage })
            .ToListAsync(cancellationToken);
        var folderKeys = rows.Select(r => r.SourceFolderKey).Distinct().ToList();
        var paths = await _db.MailFolders.AsNoTracking()
            .Where(f => f.ArchiveId == run.ArchiveId && folderKeys.Contains(f.SourceFolderKey))
            .ToDictionaryAsync(f => f.SourceFolderKey, f => f.PathJson, cancellationToken);
        var items = rows.Take(pageSize)
            .Select(r => new MailFailureDto(
                paths.TryGetValue(r.SourceFolderKey, out var json) ? ParsePath(json) : [],
                r.Ordinal,
                r.FailureCode ?? "failed",
                r.FailureMessage))
            .ToList();
        return new MailFailurePage(items, rows.Count > pageSize ? items[^1].Position.ToString() : null);
    }

    // ---- helpers ----------------------------------------------------------------

    // A run whose job ended without the runner recording it (cancelled while
    // still queued, or failed past its retries) is brought in line with the
    // job, so it never blocks a new import nor shows "running" forever.
    private async Task ReconcileAsync(MailImportRun run, CancellationToken cancellationToken)
    {
        if (!MailImportRunStatuses.IsActive(run.Status) || run.JobId is not Guid jobId)
        {
            return;
        }
        var job = await _db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new { j.Status, j.LastErrorCode })
            .FirstOrDefaultAsync(cancellationToken);
        string? status = job?.Status switch
        {
            null => MailImportRunStatuses.Failed,
            JobStatuses.Cancelled => MailImportRunStatuses.Cancelled,
            JobStatuses.Failed => MailImportRunStatuses.Failed,
            JobStatuses.Succeeded => MailImportRunStatuses.Failed, // finished without finalizing
            _ => null,
        };
        if (status is null)
        {
            return;
        }
        // The runner writes the run's own terminal state BEFORE its job ends;
        // re-read so a run finalized a moment ago is never overwritten.
        var current = await _db.MailImportRuns.AsNoTracking()
            .Where(r => r.Id == run.Id)
            .Select(r => r.Status)
            .FirstAsync(cancellationToken);
        if (!MailImportRunStatuses.IsActive(current))
        {
            await _db.Entry(run).ReloadAsync(cancellationToken);
            return;
        }
        run.Status = status;
        run.Phase = null;
        if (status == MailImportRunStatuses.Failed)
        {
            run.ErrorCode ??= "job_failed";
            run.ErrorMessage ??= "L'importazione si è interrotta per un errore interno.";
        }
        run.CompletedAt ??= Now;
        run.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        try
        {
            _workspace.DeleteRun(run.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Swept on the next finished run.
        }
    }

    private async Task<MailImportRun?> ActiveRunAsync(Guid archiveId, CancellationToken cancellationToken)
    {
        var active = await _db.MailImportRuns
            .Where(r => r.ArchiveId == archiveId
                && (r.Status == MailImportRunStatuses.Queued || r.Status == MailImportRunStatuses.Running))
            .ToListAsync(cancellationToken);
        foreach (var run in active)
        {
            await ReconcileAsync(run, cancellationToken);
        }
        return active.FirstOrDefault(r => MailImportRunStatuses.IsActive(r.Status));
    }

    private async Task<bool> IsSourceAvailableAsync(Domain.Mail.MailArchive archive, CancellationToken cancellationToken)
    {
        if (archive.SourceFileItemId is not Guid fileId)
        {
            return false;
        }
        var sha = await _db.FileItems.AsNoTracking()
            .Where(f => f.Id == fileId && f.OwnerUserId == archive.OwnerUserId && f.DeletedAt == null)
            .Join(_db.BlobObjects, f => f.BlobObjectId, b => b.Id, (f, b) => b.Sha256)
            .FirstOrDefaultAsync(cancellationToken);
        return sha is not null && ContentFingerprint.Compute(_pepper, sha) == archive.SourceFingerprint;
    }

    private async Task<bool> HasPstSignatureAsync(Guid owner, Guid fileItemId, CancellationToken cancellationToken)
    {
        await using var content = await _files.OpenContentAsync(fileItemId, owner, cancellationToken);
        if (content is null)
        {
            return false;
        }
        var header = new byte[PstSignature.Length];
        var read = 0;
        while (read < header.Length)
        {
            var n = await content.Content.ReadAsync(header.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                break;
            }
            read += n;
        }
        return read == header.Length && header.AsSpan().SequenceEqual(PstSignature);
    }

    private async Task<MailArchiveDto> ToDtoAsync(Domain.Mail.MailArchive archive, MailImportRun? latest, CancellationToken cancellationToken)
    {
        latest ??= await _db.MailImportRuns
            .Where(r => r.ArchiveId == archive.Id)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new MailArchiveDto(
            archive.Id,
            archive.DisplayName,
            archive.Format,
            await IsSourceAvailableAsync(archive, cancellationToken),
            archive.SourceFileName,
            archive.MessageCount,
            archive.FolderCount,
            archive.CreatedAt,
            archive.LastImportedAt,
            latest is null ? null : await ToRunDtoAsync(latest, cancellationToken));
    }

    private async Task<MailRunDto> ToRunDtoAsync(MailImportRun run, CancellationToken cancellationToken)
    {
        if (_db.Entry(run).State != EntityState.Detached)
        {
            await ReconcileAsync(run, cancellationToken);
        }
        var cancellationRequested = run.JobId is Guid jobId
            && await _db.BackgroundJobs.AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => j.CancellationRequested)
                .FirstOrDefaultAsync(cancellationToken);
        return new MailRunDto(
            run.Id, run.ArchiveId, run.Status, run.Phase,
            run.TotalMessages, run.ProcessedMessages, run.ImportedMessages, run.AlreadyPresentMessages,
            run.SkippedDeletedMessages, run.FailedMessages,
            run.AttachmentsCreated, run.AttachmentsReused, run.AttachmentsSkipped,
            cancellationRequested && MailImportRunStatuses.IsActive(run.Status),
            run.ErrorCode, run.ErrorMessage, run.CreatedAt, run.StartedAt, run.CompletedAt,
            run.ScanErrors, MailScanIssues.Parse(run.ScanIssuesJson));
    }

    internal static IReadOnlyList<string> ParsePath(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string DisplayNameFor(string fileName)
    {
        var stem = fileName.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return MailNames.Segment(stem, "Archivio PST");
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
