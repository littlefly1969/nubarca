namespace NubArca.Api.Domain.Print;

/// <summary>
/// An owner's direct print of one of their own photographs, remembered so that
/// repeating it cannot print twice.
///
/// Printing has a physical effect: a double tap or a retried POST must not put
/// a second sheet through the printer. The browser mints an idempotency key per
/// COMPOSITION and reuses it for retries of that composition; the unique index
/// on (owner, key) is what makes the promise even when two requests race, and
/// the stored job is what a repeat is answered with.
///
/// <see cref="RequestFingerprint"/> is a digest of the composition the key was
/// first used for — photograph, printer, paper, orientation, placement, date
/// on or off and its language — so the same key sent with a DIFFERENT
/// composition is a client bug, refused, never a second print. The key itself
/// is stored hashed, like every capability secret in this system.
/// </summary>
public sealed class OwnerPhotoPrintRequest
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }

    /// <summary>SHA-256 of the client's Idempotency-Key, hex, lowercase.</summary>
    public string IdempotencyKeyHash { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical composition, hex, lowercase.</summary>
    public string RequestFingerprint { get; set; } = string.Empty;

    /// <summary>The job the first request produced; a repeat is answered with it.</summary>
    public Guid PrintJobId { get; set; }

    public DateTime CreatedAt { get; set; }
}
