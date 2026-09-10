namespace NubArca.Api.Party;

public interface IPartyChallengeService
{
    Task<PartyChallengeListDto?> ListOwnerAsync(Guid ownerId, Guid albumId, CancellationToken ct = default);
    Task<PartyChallengeDto?> CreateAsync(Guid ownerId, Guid albumId, PartyChallengeWriteRequest request, CancellationToken ct = default);
    Task<PartyChallengeDto?> UpdateAsync(Guid ownerId, Guid albumId, Guid challengeId, PartyChallengeWriteRequest request, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid ownerId, Guid albumId, Guid challengeId, CancellationToken ct = default);
    Task<bool> ReorderAsync(Guid ownerId, Guid albumId, IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task<PartyGuestChallengesDto?> ListGuestAsync(PartyAccess access, Guid participantId, CancellationToken ct = default);
    Task<PartyVoteResultDto?> VoteAsync(PartyAccess access, Guid participantId, Guid challengeId, bool voted, CancellationToken ct = default);
    Task<PartyPlaybackSnapshotDto?> GetSnapshotAsync(Guid ownerId, Guid albumId, CancellationToken ct = default);
    Task<PartyPlaybackSnapshotDto?> OnMediaBoundaryAsync(Guid ownerId, Guid albumId, CancellationToken ct = default);
    Task<PartyPlaybackSnapshotDto?> CompleteActiveAsync(Guid ownerId, Guid albumId, CancellationToken ct = default);

    // The file behind an activity's picture, reached THROUGH the activity: the
    // party's game is on, the activity is in its deck and enabled, and the
    // picture is still an eligible Party reference. Album membership is not
    // asked — an activity may use any of the owner's images. Null for everything
    // else; the route answers every null with the same 404.
    Task<Guid?> GuestMediaFileAsync(PartyAccess access, Guid challengeId, CancellationToken ct = default);

    // The same question for the owner's own paired television, which reaches
    // the held activity through its album's game rather than through a token.
    Task<Guid?> TvMediaFileAsync(Guid ownerId, Guid albumId, Guid challengeId, CancellationToken ct = default);
}
