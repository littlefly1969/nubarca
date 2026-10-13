using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Storage;

namespace NubArca.Api.Uploads;

// The HTTP half of an owner upload, shared by every route that ingests into
// the caller's own library (the multipart POST and the resumable completion).
// Two things live here so the routes cannot drift apart:
//
//   * the optional Idempotency-Key contract (mobile-sync-v1): a retry with the
//     same key replays the original FileSummary instead of ingesting twice;
//   * the mapping from ingest failures to status codes and bodies the clients
//     already classify (409 name, 409 upload_in_progress, 413 size/quota…).
public static class OwnerUploadHttp
{
    public static IResult InvalidKey() =>
        Results.BadRequest(new { error = "Invalid 'Idempotency-Key' header." });

    public static IResult InProgress() => Results.Json(
        new { error = "Upload operation is being processed.", code = "upload_in_progress", retryable = true },
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// Runs <paramref name="ingest"/> as one replayable operation when a key is
    /// given, or plainly when it is not. <paramref name="ingest"/> receives the
    /// claim token to hand to <see cref="IFileItemService.CreateAsync"/> and
    /// returns the created file; a ready-made failure result may be returned
    /// instead through <see cref="IngestOutcome.Failed"/>.
    /// </summary>
    public static async Task<IResult> RunAsync(
        Guid ownerUserId,
        string? idempotencyKey,
        IUploadIdempotencyService operations,
        Func<Guid?, Task<IngestOutcome>> ingest,
        CancellationToken cancellationToken,
        Func<UploadAnswer, Task<IResult>>? respond = null)
    {
        respond ??= DefaultAnswer;
        Guid? claimToken = null;
        if (idempotencyKey is not null)
        {
            var replay = await operations.FindCompletedResultAsync(ownerUserId, idempotencyKey, cancellationToken);
            if (replay is not null)
            {
                return await respond(new UploadAnswer(replay, Replayed: true, LinkedExisting: false));
            }

            var claim = await operations.TryClaimAsync(
                ownerUserId, idempotencyKey, UploadIdempotencyService.DefaultLease, cancellationToken);
            switch (claim.Outcome)
            {
                case UploadClaimOutcome.AlreadyCompleted:
                    // Raced a completing twin between lookup and claim: its
                    // result is authoritative, read it back once more.
                    replay = await operations.FindCompletedResultAsync(ownerUserId, idempotencyKey, cancellationToken);
                    return replay is not null
                        ? await respond(new UploadAnswer(replay, Replayed: true, LinkedExisting: false))
                        : InProgress();
                case UploadClaimOutcome.InFlight:
                    return Results.Json(
                        new { error = "Upload operation is already in progress.", code = "upload_in_progress", retryable = true },
                        statusCode: StatusCodes.Status409Conflict);
                case UploadClaimOutcome.Claimed:
                    claimToken = claim.Token;
                    break;
            }
        }

        // `ingested` flips only after the file row AND its completion record are
        // durable (CreateAsync completes the claim inside the FileItem
        // transaction). Every other exit releases the claim, so a failed
        // upload never leaves its key cached as in progress.
        var ingested = false;
        try
        {
            var outcome = await ingest(claimToken);
            if (outcome.Failure is not null)
            {
                return outcome.Failure;
            }
            var created = outcome.Created!;
            var summary = new FileSummary(
                created.Id, created.Name, created.MimeType, created.SizeBytes, created.CreatedAt,
                created.Width, created.Height);
            ingested = true;
            return await respond(new UploadAnswer(summary, Replayed: false, outcome.LinkedExisting));
        }
        catch (Exception ex) when (TryMapFailure(ex, out var mapped))
        {
            return mapped;
        }
        finally
        {
            // Best-effort: a failed cleanup must never mask the real outcome,
            // and the lease bounds any damage a skipped release could cause.
            if (claimToken is not null && !ingested)
            {
                try
                {
                    await operations.ReleaseAsync(claimToken.Value, CancellationToken.None);
                }
                catch
                {
                    // Lease expiry is the safety net; see UploadOperation.
                }
            }
        }
    }

    // A fresh upload answers 201 with the new file; a replay answers 200 with
    // the file the first attempt stored.
    private static Task<IResult> DefaultAnswer(UploadAnswer answer) => Task.FromResult(
        answer.Replayed
            ? Results.Ok(answer.File)
            : Results.Created($"/api/files/{answer.File.Id}/content", answer.File));

    /// <summary>The answer every owner upload route gives for an ingest failure.</summary>
    public static bool TryMapFailure(Exception ex, out IResult result)
    {
        switch (ex)
        {
            case UploadOperationClaimLostException:
                // The claim disappeared mid-ingestion (expired-lease takeover or
                // a concurrent completion). Nothing was committed — the file row
                // and the completion live or die together.
                result = InProgress();
                return true;
            case DuplicateFileNameException:
                result = Results.Conflict();
                return true;
            case FolderNotFoundException:
                result = Results.NotFound();
                return true;
            case UploadTooLargeException:
                // App-level Storage:MaxUploadBytes ceiling.
                result = Results.Json(
                    new { error = "File exceeds the maximum allowed upload size.", code = "too_large" },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
                return true;
            case QuotaExceededException:
                // The user's own figures are available via GET /api/storage/me.
                result = Results.Json(
                    new { error = "Upload would exceed your storage quota.", code = "quota_exceeded" },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
                return true;
            case ArgumentException argument:
                result = Results.BadRequest(new { error = argument.Message });
                return true;
            case BadHttpRequestException bad when
                bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                || bad.Message.Contains("too large", StringComparison.OrdinalIgnoreCase):
                // Slice 78: Kestrel refused the body before our code ran
                // (MaxRequestBodySize / MultipartBodyLengthLimit). No bytes or
                // rows exist, so a clean 413 is the whole story.
                result = Results.Json(
                    new { error = "The uploaded file is too large. Check the server's maximum upload size.", code = "too_large" },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
                return true;
            default:
                result = Results.Empty;
                return false;
        }
    }
}

public sealed record IngestOutcome(Domain.FileItem? Created, IResult? Failure, bool LinkedExisting = false)
{
    public static IngestOutcome Success(Domain.FileItem created) => new(created, null);

    // Nothing was stored: the caller already owned these bytes, and that file
    // is the upload's result.
    public static IngestOutcome Existing(Domain.FileItem existing) => new(existing, null, LinkedExisting: true);

    public static IngestOutcome Failed(IResult failure) => new(null, failure);
}

/// <summary>The file an upload resolved to, and how: replayed from an earlier attempt, or an existing file reused.</summary>
public sealed record UploadAnswer(FileSummary File, bool Replayed, bool LinkedExisting);
