namespace NubArca.Api.Domain.Print;

/// <summary>
/// A guest's print submission, remembered so that repeating it cannot print
/// twice.
///
/// Printing has a physical effect: a double tap, a retried POST, a flaky mobile
/// network replaying a request — none of them may put a second sheet through the
/// printer. The guest's client mints an idempotency key per submission and
/// reuses it for retries of THAT submission; the unique index below is what
/// makes the promise, and the stored job id is what a repeat returns instead of
/// creating a second job, a second artifact and a second unit of budget.
///
/// The key is stored HASHED, like every other capability secret in this system:
/// a request is matched by hashing what arrives, never by keeping what a client
/// sent.
/// </summary>
public sealed class PartyPrintRequest
{
    public Guid Id { get; set; }
    public Guid PartyAlbumId { get; set; }

    /// <summary>SHA-256 of the client's Idempotency-Key, hex, lowercase.</summary>
    public string IdempotencyKeyHash { get; set; } = string.Empty;

    /// <summary>Which product this key claimed, so a key cannot cross products.</summary>
    public string Product { get; set; } = PartyPrintProducts.Photo;

    /// <summary>The job the first request produced; a repeat is answered with it.</summary>
    public Guid PrintJobId { get; set; }

    public DateTime CreatedAt { get; set; }
}
