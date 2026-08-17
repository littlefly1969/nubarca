namespace NubArca.Api.Party;

// Per-file outcome of an anonymous party upload. Only safe, coarse states — no
// storage internals, no stack traces.
public enum PartyUploadOutcome
{
    AcceptedPhoto,
    AcceptedVideo,
    RejectedType,      // declared type is neither an allowed image nor an allowed video
    RejectedTooLarge,  // over the party per-kind cap / upload ceiling
    RejectedNotMedia,  // server-side detection says it isn't a real image or video
    QuotaPhotoExhausted,
    QuotaVideoExhausted,
    Failed,            // quota / storage / album error
}

public static class PartyUploadOutcomeExtensions
{
    public static bool IsAccepted(this PartyUploadOutcome outcome)
        => outcome is PartyUploadOutcome.AcceptedPhoto or PartyUploadOutcome.AcceptedVideo;
}

// Ingests one anonymously-uploaded file into a party album on the album owner's
// behalf. The (ownerUserId, albumId) pair is already validated from the upload
// token by IPartyLinkService.ResolveUploadAsync. Reuses the normal upload
// pipeline (dedup-aware content-addressed storage, inline small thumbnail).
//
// MEDIA, not images: the same endpoint accepts photos and videos, and the
// SERVER decides which it got. The client-declared MIME only avoids obviously
// pointless work; the authoritative category comes from the ingested blob, so a
// script renamed .mp4 is rejected after ingest and never reaches the album.
// Storage/quota are charged to the owner.
public interface IPartyUploadService
{
    // `participantId` identifies the guest whose per-kind quota this upload
    // claims, and is stamped on the moderation row for provenance. The quota
    // maxima come from the resolved link (0 = unlimited).
    Task<PartyUploadOutcome> UploadAsync(
        Guid ownerUserId,
        Guid albumId,
        string fileName,
        string? declaredContentType,
        long declaredLength,
        Stream content,
        Guid? partyAlbumLinkId = null,
        bool requireApproval = false,
        Guid? participantId = null,
        int maxPhotos = 0,
        int maxVideos = 0,
        CancellationToken cancellationToken = default);
}
