namespace NubArca.Api.Domain;

// Per-user Personal Area access secret for the TV surface. Created from the
// authenticated web account UI (never on the TV itself) and verified
// server-side on every TV unlock. Exactly one row per owner (unique index).
//
// Only the ASP.NET Core PasswordHasher output (PBKDF2 — self-describing string
// embedding its own salt + KDF parameters) is stored; the plaintext secret is
// never persisted, logged, audited, or returned by any API.
//
// `Scheme` names the secret's alphabet and length so ONE row can describe both
// credential generations without a second table or a second authorization
// system:
//   "pin-v1"  — the retired 6-digit numeric PIN. Still VERIFIABLE so an
//               already-paired television keeps working until its owner
//               configures the directional code, but no current client offers
//               it and nothing creates a new one.
//   "dpad-v1" — the current 9-symbol directional remote code (U/R/D/L/S). The
//               TV never renders the symbols, so a bystander cannot read the
//               secret off the screen.
// The hash is computed over the canonical secret string either way, so
// verification itself is scheme-agnostic; the scheme decides only which format
// is accepted and which client flow may present it.
//
// Generation is bumped whenever the secret is replaced; every
// TvPersonalUnlockGrant records the generation it was minted under, so a change
// immediately invalidates all outstanding grants.
public class TvPersonalPin
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string PinHash { get; set; } = string.Empty;

    // "pin-v1" | "dpad-v1". Existing rows migrate to "pin-v1"; the column is
    // never null, so a missing scheme cannot be read as "anything goes".
    public string Scheme { get; set; } = TvPersonalSecretSchemes.Dpad;

    public int Generation { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// The scheme vocabulary, shared by the domain, the service and the migration so
// the three cannot spell it differently.
public static class TvPersonalSecretSchemes
{
    // Retired numeric PIN. Verify-only: no endpoint creates one.
    public const string LegacyPin = "pin-v1";

    // Current directional remote code.
    public const string Dpad = "dpad-v1";

    public static bool IsKnown(string? scheme) =>
        scheme is LegacyPin or Dpad;
}
