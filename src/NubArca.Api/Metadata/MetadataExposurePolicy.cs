namespace NubArca.Api.Metadata;

// Audiences that can see metadata. Stable enum used by exposure rules,
// no-leak scans, and docs. The principle is default-deny: only fields
// explicitly classified as exposed for an audience may leave the process
// through that audience's DTO surface.
public enum MetadataAudience
{
    // Never leaves the process. Storage internals (StorageKey, physical
    // paths, BlobObjectId, TokenHash, raw embedded JSON, etc.).
    Internal,

    // Authenticated owner of a file, via /api/files/{id}/metadata and the
    // file's own DTOs. Sees curated blob-derived fields + their own
    // user-metadata. Never sees GPS coordinates, serial numbers, software,
    // raw documents, or storage internals.
    Owner,

    // Unauthenticated /s/{token} consumer and the public download response.
    // Sees only the file bytes (which may themselves contain embedded
    // metadata — see ShareLinkBytesIncludeEmbeddedMetadata) and a download
    // filename. Never sees metadata DTOs, file ids, owner ids, or storage
    // internals.
    ShareLinkPublic,

    // /api/admin/* operator endpoints. Aggregate counters only. Never sees
    // per-file ids, names, paths, raw metadata, GPS, serials, tokens, or
    // storage internals.
    AdminAggregate,
}

// Classification of a metadata field on BlobMetadata or its envelope.
public enum MetadataFieldSensitivity
{
    // Never crosses the process boundary regardless of audience.
    InternalOnly,

    // Stored internally but only exposed under explicit, opt-in slices.
    // For slice 57 these are NOT exposed by any normal DTO.
    Sensitive,

    // Exposed to the owner of the file, alongside their own user metadata.
    OwnerCurated,
}

// Centralized metadata-privacy policy (slice 57). Captures, in code, the
// classifications that other slices have been enforcing in comments and
// hand-rolled DTOs. Used by:
//   * The metadata service to decide which embedded fields to project into
//     the owner DTO (it already does this; the policy just names the rule).
//   * Endpoint no-leak scans, which assert that no response body or header
//     contains any field name in ForbiddenInResponses.
//
// The contract this policy enforces:
//
//   1. Internal-only field names (StorageKey, TokenHash, RawMetadataJson,
//      BlobObjectId, physical paths, other-user OwnerUserId, sha256 of
//      blobs) MUST NEVER appear in any HTTP response body or header.
//
//   2. Sensitive embedded fields (GPS coordinates, body / lens serial
//      numbers, owner/artist/copyright, software tag, local path tags) are
//      stored internally for completeness but MUST NOT appear in any
//      Owner, ShareLinkPublic, or AdminAggregate response. Their *presence*
//      may be signalled by a derived boolean (HasGps).
//
//   3. Share-link DTOs MUST NOT include any embedded-metadata fields or
//      raw metadata JSON. The download bytes are unchanged from upload, so
//      embedded metadata inside the original file MAY be present in the
//      downloaded bytes — that is a deliberate, documented behaviour, not
//      a leak through this policy boundary.
//
//   4. Admin endpoints MUST be aggregate-only (counts, sizes, durations).
//      They MUST NOT include per-file identifiers, names, paths, metadata,
//      GPS, serials, or storage internals.
//
// "MUST NOT" rules are enforced by tests under tests/NubArca.Api.Tests/
// (see MetadataPrivacyPolicyTests + per-endpoint no-leak scans).
public static class MetadataExposurePolicy
{
    // Field-name needles that MUST NOT appear in any response body or
    // header from any endpoint (regardless of audience). The list is
    // case-sensitive but covers PascalCase, camelCase, and snake_case
    // variants because System.Text.Json's default policy is camelCase but
    // a future serializer change should not silently defeat the scan.
    public static readonly IReadOnlyList<string> InternalOnlyNeedles = new[]
    {
        // Storage layout.
        "StorageKey", "storageKey", "storage_key",
        "BlobObjectId", "blobObjectId", "blob_object_id",
        "objects/",                                  // physical-path fragment
        "/storage/objects/",                         // physical-path prefix

        // Identity of other users / cross-owner leakage.
        "OwnerUserId", "ownerUserId", "owner_user_id",
        "PasswordHash", "passwordHash", "password_hash",

        // Share-link secrets.
        "TokenHash", "tokenHash", "token_hash",

        // Internal raw embedded document.
        "RawMetadataJson", "rawMetadataJson", "raw_metadata_json",

        // Blob content addressing — never exposed by design (kept opaque
        // so a future blob layout change isn't an exposed contract).
        "Sha256", "sha256", "sha_256",
    };

    // Embedded-metadata field names that ARE stored internally on
    // BlobMetadata but MUST NOT appear in any Owner, ShareLinkPublic, or
    // AdminAggregate response. A future privacy slice could opt-in to
    // expose any of these per-user; today they are all withheld.
    public static readonly IReadOnlyList<string> SensitiveEmbeddedNeedles = new[]
    {
        // GPS coordinates. HasGps boolean is allowed; coordinates are not.
        "GpsLatitude", "gpsLatitude", "gps_latitude",
        "GpsLongitude", "gpsLongitude", "gps_longitude",
        "GpsAltitude", "gpsAltitude", "gps_altitude",

        // Hardware unique identifiers.
        "BodySerialNumber", "bodySerialNumber", "body_serial_number",
        "LensSerialNumber", "lensSerialNumber", "lens_serial_number",

        // Software / agent fingerprint (often identifying).
        "Software", "software",
        "LensMake", "lensMake", "lens_make",

        // Date offset string (records an exact timezone — privacy-leaky in
        // a way that DateTaken's UTC-nominal value is not).
        "DateTakenOffset", "dateTakenOffset", "date_taken_offset",
    };

    // Combined needle list for an exhaustive no-leak scan over any
    // authenticated endpoint response. Owner-facing DTOs are allowed to
    // expose owner-curated values (camera make/model, ISO, etc.) — those
    // are NOT in this list. Test classes import this and add per-endpoint
    // extras (e.g. literal serial strings from their fixtures).
    public static readonly IReadOnlyList<string> ForbiddenInResponses =
        InternalOnlyNeedles
            .Concat(SensitiveEmbeddedNeedles)
            .ToArray();

    // True if a field with the given sensitivity may be exposed through
    // the given audience's DTO surface. Default-deny: everything is closed
    // unless this method explicitly opens it.
    public static bool IsAllowed(MetadataFieldSensitivity sensitivity, MetadataAudience audience)
        => (sensitivity, audience) switch
        {
            (MetadataFieldSensitivity.OwnerCurated, MetadataAudience.Owner) => true,
            // OwnerCurated may be exposed to admin if and only if the admin
            // DTO is per-file — today it is not. AdminAggregate is counts
            // only, so even owner-curated fields stay closed.
            _ => false,
        };

    // Documented behaviour, not a runtime gate: public share-link
    // downloads serve the ORIGINAL file bytes (slice 12). Those bytes may
    // contain embedded metadata such as EXIF, IPTC, XMP, GPS, ICC.
    // NubArca does NOT strip or redact this metadata before serving;
    // stripping/redaction is future work and would require producing a
    // derived blob with a new SHA-256. The UI surfaces this on the
    // share-link creation form so the owner makes an informed decision.
    public const bool ShareLinkBytesIncludeEmbeddedMetadata = true;
}
