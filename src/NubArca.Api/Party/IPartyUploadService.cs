namespace NubArca.Api.Party;

// Per-file outcome of an anonymous party upload. Only safe, coarse states — no
// storage internals, no stack traces.
public enum PartyUploadOutcome
{
    Accepted,
    RejectedType,      // declared type not an allowed image
    RejectedTooLarge,  // over the party per-file cap / upload ceiling
    RejectedNotImage,  // server-side detection says it isn't a real image
    Failed,            // quota / storage / album error
}

// Ingests one anonymously-uploaded file into a party album on the album owner's
// behalf. The (ownerUserId, albumId) pair is already validated from the upload
// token by IPartyLinkService.ResolveUploadAsync. Reuses the normal upload
// pipeline (dedup-aware content-addressed storage, inline small thumbnail) and
// enforces an IMAGE-only gate (client MIME is untrusted → server detection is
// authoritative). Storage/quota are charged to the owner.
public interface IPartyUploadService
{
    // `partyAlbumLinkId`/`requireApproval` come from the resolved upload token. A
    // moderation record (PartyUploadItem) is created for every accepted upload:
    // pending (invisible until approved) when requireApproval is true, else
    // approved (immediately visible — the low-friction default).
    Task<PartyUploadOutcome> UploadAsync(
        Guid ownerUserId,
        Guid albumId,
        string fileName,
        string? declaredContentType,
        long declaredLength,
        Stream content,
        Guid? partyAlbumLinkId = null,
        bool requireApproval = false,
        CancellationToken cancellationToken = default);
}
