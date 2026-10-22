using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain.Mail;
using NubArca.Api.Jobs;
using NubArca.Api.Storage;

namespace NubArca.Api.MailArchive;

/// <summary>
/// Gives searchable text to an owner's messages that have none: those
/// imported before mail_message_texts existed. The text is rebuilt from each
/// message's immutable document blob (never from the PST, never touching an
/// attachment), by an owner-scoped, keyset-paged, resumable job
/// (<see cref="JobTypes.MailTextsBackfill"/>). The owner's mail starts it on
/// demand (<see cref="EnsureAsync"/>); until it has finished, body search
/// says it is incomplete.
/// </summary>
public sealed class MailTextBackfillService
{
    private const int PageSize = 100;

    // Owners known to have a text row for every message. Coverage only ever
    // stays complete (imports write the text with the message, deletes remove
    // both), so a process checks each owner at most once after the fact.
    private static readonly ConcurrentDictionary<Guid, bool> Covered = new();

    private readonly AppDbContext _db;
    private readonly IBlobService _blobs;
    private readonly IJobQueue _jobs;
    private readonly ILogger<MailTextBackfillService> _logger;

    public MailTextBackfillService(AppDbContext db, IBlobService blobs, IJobQueue jobs, ILogger<MailTextBackfillService> logger)
    {
        _db = db;
        _blobs = blobs;
        _jobs = jobs;
        _logger = logger;
    }

    public static string IdempotencyKey(Guid owner) => $"mail.texts:{owner:N}";

    // True while some of the owner's messages have no searchable text yet;
    // makes sure a backfill job is queued for them (idempotent).
    public async Task<bool> EnsureAsync(Guid owner, CancellationToken cancellationToken)
    {
        if (Covered.ContainsKey(owner))
        {
            return false;
        }
        if (!await Missing(owner).AnyAsync(cancellationToken))
        {
            Covered[owner] = true;
            return false;
        }
        await _jobs.EnqueueAsync(
            JobTypes.MailTextsBackfill,
            new MailTextsBackfillJobPayload(owner),
            idempotencyKey: IdempotencyKey(owner),
            cancellationToken: cancellationToken);
        return true;
    }

    private IQueryable<MailMessage> Missing(Guid owner) => _db.MailMessages.AsNoTracking()
        .Where(m => m.OwnerUserId == owner && m.Status == MailMessageStatuses.Complete
            && !_db.MailMessageTexts.Any(t => t.MessageId == m.Id));

    // One slice of the backfill, from the checkpoint (the last message id
    // handled). Returns false when it stopped at a safe point with more left.
    public async Task<bool> RunAsync(Guid owner, JobContext context, CancellationToken cancellationToken)
    {
        var after = Guid.TryParse(context.Checkpoint, out var last) ? last : Guid.Empty;
        long done = 0;
        var unreadable = 0;
        while (true)
        {
            var page = await Missing(owner)
                .Where(m => m.Id.CompareTo(after) > 0)
                .OrderBy(m => m.Id)
                .Select(m => new { m.Id, m.BlobObjectId })
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                Covered[owner] = true;
                context.Log($"mail texts backfill: {done} messages indexed this slice, {unreadable} unreadable.");
                return true;
            }
            foreach (var message in page)
            {
                if (context.ShouldYield(done))
                {
                    if (context.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Mail text backfill cancelled.");
                    }
                    context.RequestContinuation(
                        context.HigherPriorityWaiting ? JobYieldReasons.HigherPriority : JobYieldReasons.SliceBudget,
                        after.ToString("N"));
                    return false;
                }
                var document = message.BlobObjectId is Guid blobId ? await ReadAsync(blobId, cancellationToken) : null;
                if (document is null)
                {
                    // Recorded as "nothing to search", so the owner's coverage
                    // completes; the message still shows and opens as before.
                    unreadable++;
                }
                await InsertAsync(owner, message.Id, document is null ? string.Empty : MailMessageTextBuilder.Build(document), cancellationToken);
                after = message.Id;
                done++;
            }
        }
    }

    // Only for a message still complete (a delete may have claimed it since
    // the page was read), and never over a row written meanwhile.
    private Task<int> InsertAsync(Guid owner, Guid messageId, string text, CancellationToken cancellationToken) =>
        _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mail_message_texts ("MessageId", "OwnerUserId", "SortDate", "Text")
            SELECT m."Id", m."OwnerUserId", m."SortDate", {text}
            FROM mail_messages m
            WHERE m."Id" = {messageId} AND m."OwnerUserId" = {owner} AND m."Status" = {MailMessageStatuses.Complete}
            ON CONFLICT ("MessageId") DO NOTHING
            """, cancellationToken);

    private async Task<PstMessageDocument?> ReadAsync(Guid blobId, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _blobs.OpenContentAsync(blobId, cancellationToken);
            return await JsonSerializer.DeserializeAsync<PstMessageDocument>(stream, PstProtocol.Json, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            // Never the content, never the key: only that it could not be read.
            _logger.LogWarning("MailArchive: a message document could not be read for search ({Error}).", ex.GetType().Name);
            return null;
        }
    }
}
