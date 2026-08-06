namespace NubArca.Api.Domain;

// Owner-scoped Private Vault (v0). Exclusion-first: content marked with this
// vault's Id (FileItem.PrivateVaultId / Folder.PrivateVaultId) is removed from
// every normal NubArca flow by a global EF query filter, and is only readable
// after a password unlock issues a short-lived access token.
//
// v0 stores ONLY a password hash (ASP.NET Core PasswordHasher / PBKDF2 — the
// hash string is self-describing and embeds its own salt + KDF parameters, so
// no separate salt column is needed). No blob encryption is implemented yet;
// EncryptionMode is a forward-looking policy field only. There is at most ONE
// vault per owner in v0 (enforced by a unique index on OwnerUserId).
public class PrivateVault
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }

    // User-facing label. Not secret. Defaults to "Private".
    public string DisplayName { get; set; } = "Private";

    // ASP.NET Core PasswordHasher output (PBKDF2). Never exposed through any
    // API/DTO/log/diagnostic.
    public string PasswordHash { get; set; } = string.Empty;

    // Forward-looking encryption policy. See PrivateVaultEncryptionModes.
    // No encrypted blobs exist in v0; this is "none" for every vault.
    public string EncryptionMode { get; set; } = PrivateVaultEncryptionModes.None;

    // Reserved for future encryption readiness (wrapped key material references,
    // KDF descriptors, etc.). NEVER contains plaintext keys and is never exposed
    // through API/UI/logs. Null in v0.
    public string? EncryptionMetadataJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public static class PrivateVaultEncryptionModes
{
    // v0: exclusion only, no encryption.
    public const string None = "none";

    // Future: server holds/derives the key (e.g. from the vault password).
    public const string ServerSideKeyProvided = "server_side_key_provided";

    // Future: client-side encryption; the server never sees plaintext.
    public const string ClientSideEncrypted = "client_side_encrypted";
}
