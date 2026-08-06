namespace NubArca.Api.Vault;

public interface IPrivateVaultService
{
    Task<PrivateVaultStatus> GetStatusAsync(Guid ownerUserId, CancellationToken cancellationToken = default);

    Task<PrivateVaultSetupOutcome> SetupAsync(Guid ownerUserId, string password, CancellationToken cancellationToken = default);

    // Null on generic failure (missing vault OR wrong password — indistinguishable).
    Task<PrivateVaultUnlockResult?> UnlockAsync(Guid ownerUserId, string password, CancellationToken cancellationToken = default);

    Task LockAsync(Guid ownerUserId, string? rawToken, CancellationToken cancellationToken = default);

    // Resolve a presented raw token to the owner's vault id, or null if
    // missing/malformed/expired/revoked/foreign.
    Task<Guid?> ResolveVaultAsync(Guid ownerUserId, string? rawToken, CancellationToken cancellationToken = default);

    Task<VaultListing> ListRootAsync(Guid ownerUserId, Guid vaultId, CancellationToken cancellationToken = default);

    // Null when the folder is not part of this owner's vault.
    Task<VaultListing?> ListFolderAsync(Guid ownerUserId, Guid vaultId, Guid folderId, CancellationToken cancellationToken = default);

    // Sanitized detail for a single file currently inside this owner's vault.
    // Null (indistinguishably) when the file is missing / foreign / not in this
    // vault / soft-deleted. Never touches or generates any derivative.
    Task<VaultMediaInfo?> GetMediaInfoAsync(Guid ownerUserId, Guid vaultId, Guid fileId, CancellationToken cancellationToken = default);

    Task<VaultMoveResult> MoveInAsync(Guid ownerUserId, Guid vaultId, IReadOnlyList<Guid> fileIds, IReadOnlyList<Guid> folderIds, CancellationToken cancellationToken = default);

    Task<VaultMoveResult> MoveOutAsync(Guid ownerUserId, Guid vaultId, IReadOnlyList<Guid> fileIds, IReadOnlyList<Guid> folderIds, CancellationToken cancellationToken = default);
}
