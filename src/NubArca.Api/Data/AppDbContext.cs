using Microsoft.EntityFrameworkCore;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<FileItem> FileItems => Set<FileItem>();
    public DbSet<BlobObject> BlobObjects => Set<BlobObject>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<FileThumbnail> FileThumbnails => Set<FileThumbnail>();
    public DbSet<BlobHlsDerivative> BlobHlsDerivatives => Set<BlobHlsDerivative>();
    public DbSet<DerivativeDiagnostic> DerivativeDiagnostics => Set<DerivativeDiagnostic>();
    public DbSet<BlobMetadata> BlobMetadata => Set<BlobMetadata>();
    public DbSet<FileItemUserMetadata> FileItemUserMetadata => Set<FileItemUserMetadata>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<AlbumItem> AlbumItems => Set<AlbumItem>();

    // SHARE-ALBUM-01: live album shares between authenticated users. One row per
    // (album, invited user); the album OWNER never has a row here and stays
    // authoritative from the Album table alone. See AlbumMembership.
    public DbSet<AlbumMembership> AlbumMemberships => Set<AlbumMembership>();

    // SHARE-COPY-01: one-time DETACHED album copies. Unrelated to memberships —
    // an accepted transfer produces an independent album owned by the recipient,
    // with no live link back to the sender. While pending, album_transfer_items
    // OWNS a blob reference per distinct blob, which is why it is registered in
    // BlobReferenceAuditService. See AlbumTransfer.
    public DbSet<AlbumTransfer> AlbumTransfers => Set<AlbumTransfer>();
    public DbSet<AlbumTransferItem> AlbumTransferItems => Set<AlbumTransferItem>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<AdminImportRun> AdminImportRuns => Set<AdminImportRun>();
    public DbSet<AdminImportItem> AdminImportItems => Set<AdminImportItem>();
    public DbSet<RemoteUploadSession> RemoteUploadSessions => Set<RemoteUploadSession>();
    public DbSet<RemoteUploadItem> RemoteUploadItems => Set<RemoteUploadItem>();
    public DbSet<RemoteUploadChunk> RemoteUploadChunks => Set<RemoteUploadChunk>();
    public DbSet<MediaLibraryRule> MediaLibraryRules => Set<MediaLibraryRule>();
    public DbSet<FileItemLocation> FileItemLocations => Set<FileItemLocation>();
    public DbSet<PhotoOrganizerRun> PhotoOrganizerRuns => Set<PhotoOrganizerRun>();
    public DbSet<PhotoOrganizerMove> PhotoOrganizerMoves => Set<PhotoOrganizerMove>();
    public DbSet<PhotoExportSession> PhotoExportSessions => Set<PhotoExportSession>();
    public DbSet<PhotoExportEntry> PhotoExportEntries => Set<PhotoExportEntry>();
    public DbSet<PrivateVault> PrivateVaults => Set<PrivateVault>();
    public DbSet<PrivateVaultAccessToken> PrivateVaultAccessTokens => Set<PrivateVaultAccessToken>();
    public DbSet<OwnerDeletedContentTombstone> OwnerDeletedContentTombstones => Set<OwnerDeletedContentTombstone>();
    public DbSet<TvPairingRequest> TvPairingRequests => Set<TvPairingRequest>();
    public DbSet<TvSession> TvSessions => Set<TvSession>();

    // TV Personal Area: per-user PIN (hash only) and per-session unlock grants
    // (token hash only). See TvPersonalPin / TvPersonalUnlockGrant.
    public DbSet<TvPersonalPin> TvPersonalPins => Set<TvPersonalPin>();
    public DbSet<TvPersonalUnlockGrant> TvPersonalUnlockGrants => Set<TvPersonalUnlockGrant>();

    // Owner + album scoped PUBLIC read-only party access links (token hash only).
    public DbSet<PartyAlbumLink> PartyAlbumLinks => Set<PartyAlbumLink>();

    // Owner-side moderation state for anonymous party uploads (visibility only).
    public DbSet<PartyUploadItem> PartyUploadItems => Set<PartyUploadItem>();

    // Short-lived anonymous "find your face" searches within a party album, and
    // their ranked (visibility-re-derived) matches. No selfie/query vector stored.
    public DbSet<PartyFaceSearchSession> PartyFaceSearchSessions => Set<PartyFaceSearchSession>();
    public DbSet<PartyFaceSearchResult> PartyFaceSearchResults => Set<PartyFaceSearchResult>();

    // AI substrate (Phase 0A): schema only. No inference, no vectors search, no
    // jobs yet. Embeddings are stored as provider-agnostic byte[] (no pgvector).
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<AiProfile> AiProfiles => Set<AiProfile>();
    public DbSet<BlobAiArtifactStatus> BlobAiArtifactStatuses => Set<BlobAiArtifactStatus>();
    public DbSet<BlobEmbedding> BlobEmbeddings => Set<BlobEmbedding>();
    public DbSet<DocumentText> DocumentTexts => Set<DocumentText>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<DocumentChunkEmbedding> DocumentChunkEmbeddings => Set<DocumentChunkEmbedding>();
    public DbSet<FaceDetection> FaceDetections => Set<FaceDetection>();
    public DbSet<FaceEmbedding> FaceEmbeddings => Set<FaceEmbedding>();
    public DbSet<PersonGroup> PersonGroups => Set<PersonGroup>();
    public DbSet<FaceAssignment> FaceAssignments => Set<FaceAssignment>();
    public DbSet<AiAnnotation> AiAnnotations => Set<AiAnnotation>();
    public DbSet<AiIndexDiagnostic> AiIndexDiagnostics => Set<AiIndexDiagnostic>();

    // Face Substrate v0 → People clustering v0 (owner-level): named people +
    // owner/profile-scoped clusters + memberships + confirmed assignments, plus a
    // small non-secret settings store for admin-tuned face thresholds.
    public DbSet<Person> People => Set<Person>();
    public DbSet<FaceCluster> FaceClusters => Set<FaceCluster>();
    public DbSet<FaceClusterMember> FaceClusterMembers => Set<FaceClusterMember>();
    public DbSet<PersonFaceAssignment> PersonFaceAssignments => Set<PersonFaceAssignment>();
    public DbSet<IgnoredFace> IgnoredFaces => Set<IgnoredFace>();
    public DbSet<AiSetting> AiSettings => Set<AiSetting>();

    // UI-only derived face crops (cache; regenerable). Never an embedding source.
    public DbSet<FacePreview> FacePreviews => Set<FacePreview>();

    // VSEM-01: canonical, versioned TEMPORAL manifest of a video blob —
    // segments and their representative sample timestamps. Blob-level and
    // owner-free (one manifest is shared by every FileItem on the blob); no
    // frames are persisted, only integral millisecond positions.
    public DbSet<VideoSemanticIndex> VideoSemanticIndexes => Set<VideoSemanticIndex>();
    public DbSet<VideoSemanticSegment> VideoSemanticSegments => Set<VideoSemanticSegment>();
    public DbSet<VideoSemanticSample> VideoSemanticSamples => Set<VideoSemanticSample>();

    // VSEM-02: canonical SigLIP2 embeddings of the temporal samples, keyed
    // (sample, profile), plus the aggregate per-(manifest, profile) embedding
    // readiness. Blob-level and owner-free like the manifest itself.
    public DbSet<VideoSemanticSampleEmbedding> VideoSemanticSampleEmbeddings
        => Set<VideoSemanticSampleEmbedding>();
    public DbSet<VideoSemanticEmbeddingStatus> VideoSemanticEmbeddingStatuses
        => Set<VideoSemanticEmbeddingStatus>();

    // VFACE-01: canonical, versioned face TRACKS of a video blob plus the
    // aggregate per-(manifest, analysis version, detection profile, embedding
    // profile) readiness. Blob-level and owner-free like the manifest itself:
    // pure evidence, never identity — no OwnerUserId, FileItemId, PersonId or
    // person name is stored here.
    public DbSet<VideoFaceAnalysisStatus> VideoFaceAnalysisStatuses
        => Set<VideoFaceAnalysisStatus>();
    public DbSet<VideoFaceTrack> VideoFaceTracks => Set<VideoFaceTrack>();

    // VFACE-02: OWNER-LEVEL identity decisions about those canonical tracks.
    // The tracks stay evidence; this is where "who is this" lives, one row per
    // (owner, track), pointing at the ordinary owner-level Person the photo
    // People path already manages. A missing row means undecided.
    public DbSet<VideoFaceTrackPersonDecision> VideoFaceTrackPersonDecisions
        => Set<VideoFaceTrackPersonDecision>();

    // Owner-private Plates (Targhe) surface. Segregated from Files/Gallery/
    // People/Party/TV/Private Vault: a standalone table no library query joins.
    public DbSet<PlateImage> PlateImages => Set<PlateImage>();

    // Owner-private ALPR analysis records + detected plate metadata for a
    // PlateImage. Never People/Face identity; never public/Party/TV/Gallery/Files.
    public DbSet<PlateAnalysisJob> PlateAnalysisJobs => Set<PlateAnalysisJob>();
    public DbSet<PlateDetection> PlateDetections => Set<PlateDetection>();
    public DbSet<PlateAnalysisModelRun> PlateAnalysisModelRuns => Set<PlateAnalysisModelRun>();

    // Owner-private, PRIVACY-ONLY face redaction metadata for a PlateImage and
    // the derived redacted-media cache. NOT identity: no embedding/cluster/
    // Person; never People/Party/TV/Gallery/Files/public. See
    // PlateFaceRedactionBox / PlateRedactedMedia.
    public DbSet<PlateFaceRedactionBox> PlateFaceRedactionBoxes => Set<PlateFaceRedactionBox>();
    public DbSet<PlateRedactedMedia> PlateRedactedMedia => Set<PlateRedactedMedia>();

    // Owner-private, opt-in, experimental Aesthetics Lab (Laboratorio estetico):
    // isolated image membership + immutable versioned HumanAesExpert analysis
    // runs, normalized metrics, optional text, and derived renditions. NEVER a
    // FileItem; never Gallery/Files/search/Party/TV/public. See
    // AestheticLabItem / AestheticAnalysisRun / AestheticMetric /
    // AestheticTextResult / AestheticLabDerivative.
    public DbSet<AestheticLabItem> AestheticLabItems => Set<AestheticLabItem>();
    public DbSet<AestheticAnalysisRun> AestheticAnalysisRuns => Set<AestheticAnalysisRun>();
    public DbSet<AestheticMetric> AestheticMetrics => Set<AestheticMetric>();
    public DbSet<AestheticTextResult> AestheticTextResults => Set<AestheticTextResult>();
    public DbSet<AestheticLabDerivative> AestheticLabDerivatives => Set<AestheticLabDerivative>();

    // Short-lived, owner-scoped QR upload capabilities for the TV "Beauty Lab"
    // mobile-handoff flow (hash-only token; upload-into-lab authority only).
    public DbSet<AestheticUploadSession> AestheticUploadSessions => Set<AestheticUploadSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
