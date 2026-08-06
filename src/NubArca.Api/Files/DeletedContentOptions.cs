namespace NubArca.Api.Files;

// Configuration for the per-owner deleted-content tombstone ledger and the
// import skip checks that read it. Bound from the "DeletedContent" section.
//
// Environment binding example (production compose): DeletedContent__Pepper=...
public sealed class DeletedContentOptions
{
    public const string SectionName = "DeletedContent";

    // Server-side pepper mixed into the content fingerprint (HMAC key) so the
    // stored fingerprint is not a bare content hash: a DB leak alone cannot be
    // used to confirm "was file X deleted?" without also knowing the pepper.
    //
    // MUST be stable across restarts (a changed pepper silently invalidates all
    // existing tombstones — they simply stop matching). If left blank a fixed
    // built-in development fallback is used so dev/test work out of the box;
    // production SHOULD set a real secret via DeletedContent__Pepper.
    public string Pepper { get; set; } = string.Empty;
}
