namespace NubArca.Api.Domain.Print;

/// <summary>
/// One printer lent by its owner to one other user.
///
/// The printer stays the owner's: the owner decides who, how many sheets, and
/// when it ends. What the share grants is PRINTING on that printer — a party's
/// sheets today, an album's photographs tomorrow — plus the two things the
/// person standing at it needs: telling NubArca which paper is loaded, and a
/// test page. The machine itself (its colours, its pause, the station) stays
/// the owner's alone.
///
/// A revoked share is kept, not deleted: it is the history of who was allowed
/// what, and a printer lent again is a NEW share with its own ceiling.
/// </summary>
public sealed class PrinterShare
{
    public Guid Id { get; set; }
    public Guid PrinterDeviceId { get; set; }

    /// <summary>Who lent it: the station's owner when it was shared.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Who may print on it.</summary>
    public Guid GranteeUserId { get; set; }

    /// <summary>
    /// The ceiling on sheets this share may take, or null for none. One sheet
    /// is one sheet whatever comes off it — a twin strip is one — and it does
    /// not know the paper; the owner reads the paper in the usage summary.
    /// </summary>
    public int? MaxSheets { get; set; }

    /// <summary>
    /// Sheets accepted under this share — the ceiling's counter, spent
    /// atomically with the job, like a party's budget. Never reset: raising the
    /// ceiling is how more is allowed.
    /// </summary>
    public int UsedSheets { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }
}

/// <summary>Bounds of a share's ceiling.</summary>
public static class PrinterShareLimits
{
    public const int MinSheets = 1;
    public const int MaxSheets = 5000;

    public static bool IsValidCeiling(int? value) => value is null or (>= MinSheets and <= MaxSheets);
}
