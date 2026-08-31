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
}
