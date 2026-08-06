namespace NubArca.Api.Albums.Sharing;

// A masked form of a member's account email, for the ALBUM OWNER's member list
// only.
//
// WHY THIS EXISTS: NubArca has no public handle, and `User.DisplayName` is not
// unique. Two members called "Mario Rossi" were previously indistinguishable in
// the owner's own list — so the owner could not tell which of them to revoke.
// That is a correctness problem for the owner, and it gets sharper in
// SHARE-ALBUM-02, where a contributor's items appear in someone else's album.
//
// WHY MASKED RATHER THAN FULL: returning the address would put a live email in a
// response body, a browser cache and any screenshot of the panel. The owner does
// not need the address — they need to TELL TWO PEOPLE APART. A mask that keeps
// the domain and the first/last character of the local part does that while
// disclosing materially less, and it is only ever served to the one person who
// already typed that address to create the invitation.
//
// It is deliberately NOT part of any recipient-facing shape: `SharedAlbumSummary`,
// `SharedAlbumDetail`, `SharedAlbumItem` and `AlbumInvitationDto` carry no email
// in any form. See the privacy notes in AlbumSharingDtos.
public static class RecipientEmailMask
{
    private const char Bullet = '•';

    /// <summary>
    /// "mario.rossi@nubarca.local" → "m•••i@nubarca.local".
    /// Returns an empty string for anything that is not a usable address, so a
    /// malformed stored value degrades to "no hint" rather than leaking itself.
    /// </summary>
    public static string Mask(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var trimmed = email.Trim();
        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1)
        {
            return string.Empty;
        }

        var local = trimmed[..at];
        var domain = trimmed[at..];

        // Short local parts cannot show a first AND a last character without
        // effectively showing the whole thing, so they show less, not more.
        var maskedLocal = local.Length switch
        {
            <= 2 => new string(Bullet, 2),
            3 => local[0] + new string(Bullet, 2),
            _ => local[0] + new string(Bullet, 3) + local[^1],
        };

        return maskedLocal + domain;
    }
}
