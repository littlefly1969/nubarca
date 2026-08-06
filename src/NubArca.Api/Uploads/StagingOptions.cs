namespace NubArca.Api.Uploads;

// Slice 93: web remote-staging upload configuration. OFF by default — the
// feature reports unavailable until Enabled=true AND RootPath is configured.
// Staging is temporary acquisition space (resumable browser chunk uploads),
// NEVER NubArca blob storage: bytes become NubArca files only after the
// verified session is imported through the admin-import pipeline.
//
// Wired from configuration (double-underscore env keys):
//   Staging__Enabled=true
//   Staging__RootPath=/var/lib/nubarca/staging
public sealed class StagingOptions
{
    public const string SectionName = "Staging";

    public bool Enabled { get; set; } = false;

    // Filesystem root for staged sessions. Each session lives in an isolated
    // subdirectory ({sessionId:N}/files/...). Must not overlap blob storage.
    public string RootPath { get; set; } = string.Empty;

    // ── Limits (defaults sized for a small personal server) ────────────────

    // Per-session byte ceiling. Default 64 GiB.
    public long MaxSessionBytes { get; set; } = 64L * 1024 * 1024 * 1024;

    // Per-file byte ceiling. The EFFECTIVE limit is min(this,
    // Storage:MaxUploadBytes when set) so staging never accepts a file the
    // import would later reject as too large. Default 2 GiB (the Storage
    // default).
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // Manifest file-count ceiling per session. Default 50k.
    public int MaxFilesPerSession { get; set; } = 50_000;

    // ── Chunking ────────────────────────────────────────────────────────────
    // The server picks the chunk size at manifest time (clamped Default into
    // [Min, Max]) and the client must slice accordingly. 8 MiB default keeps
    // each request well under typical proxy/Kestrel body limits while not
    // flooding the API with requests.
    public int MinChunkSizeBytes { get; set; } = 1 * 1024 * 1024;
    public int MaxChunkSizeBytes { get; set; } = 64 * 1024 * 1024;
    public int DefaultChunkSizeBytes { get; set; } = 8 * 1024 * 1024;

    // ── Lifecycle ───────────────────────────────────────────────────────────

    // Sessions expire this long after creation; expired sessions are reclaimed
    // by the cleanup sweeper. Default 72h.
    public int SessionTtlHours { get; set; } = 72;

    // Enables (a) the background sweeper that expires overdue sessions and
    // deletes their staging directories, and (b) automatic staging-directory
    // cleanup after a FULLY successful import. OFF by default, consistent
    // with the janitor/sweeper convention; the DELETE endpoint always allows
    // manual discard.
    public bool CleanupEnabled { get; set; } = false;

    // Sweeper poll interval (when CleanupEnabled).
    public int CleanupIntervalMinutes { get; set; } = 60;

    public int EffectiveChunkSizeBytes =>
        Math.Clamp(DefaultChunkSizeBytes, Math.Max(1, MinChunkSizeBytes), Math.Max(1, MaxChunkSizeBytes));
}
