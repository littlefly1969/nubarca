namespace NubArca.Api.Domain;

// Slice 3 (media organization): per-file media-library membership.
//
// Distinct from BOTH the folder-level media-library RULES (Slice 94, which
// exclude whole folders per kind) AND the Private Vault (which hides files from
// EVERY surface including the file browser). This state answers one question:
// "should this individual file appear on the media surfaces (galleries, albums,
// search, similarity, people, TV, Party, AI discovery)?"
//
//   Active   — normal. The file participates in the media library.
//   Excluded — the owner has moved it out of the media library. It STILL exists
//              as a normal file (visible in the folder browser, downloadable,
//              keeps its metadata / album membership / blob / derivatives /
//              embeddings) — it is only suppressed from the media surfaces and
//              made non-eligible for NEW AI processing. Fully reversible.
//
// Stored as an int (Active = 0) so every pre-existing row defaults to Active
// via the column default; there is NO global EF query filter for this state
// (that would hide Excluded files from the file browser too) — media surfaces
// opt in through the shared MediaLibraryScope policy instead.
public enum MediaLibraryState
{
    Active = 0,
    Excluded = 1,
}
