namespace NubArca.Api.Plates;

// Configuration for the owner-private Plates surface. Bound from the "Plates"
// section (environment binding example: Plates__Pepper=...).
public sealed class PlatesOptions
{
    public const string SectionName = "Plates";

    // Server-side secret mixed into the owner-scoped logical container key
    // (HMAC key) so the stored LogicalContainerKey is not derivable from the
    // owner id alone: a DB leak cannot be used to link a container key back to a
    // user without also knowing the pepper. MUST be stable across restarts (a
    // changed pepper produces different container keys for the same owner). If
    // left blank a fixed built-in development fallback is used so dev/test work
    // out of the box; production SHOULD set a real secret via Plates__Pepper.
    public string Pepper { get; set; } = string.Empty;

    // Max bytes accepted for a single plate upload. Independent of the global
    // blob-store MaxUploadBytes; keeps plate uploads bounded. Default 25 MiB.
    public long MaxUploadBytes { get; set; } = 25L * 1024 * 1024;
}
