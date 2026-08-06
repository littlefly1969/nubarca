namespace NubArca.Api.Audit;

public static class AuditActions
{
    public const string LoginSuccess = "auth.login.success";
    public const string LoginFailure = "auth.login.failure";
    public const string Logout = "auth.logout";
    public const string FolderCreate = "folder.create";
    public const string FolderRename = "folder.rename";
    public const string FolderMove = "folder.move";
    public const string FolderDelete = "folder.delete";
    public const string FolderDeleteRecursive = "folder.delete_recursive";
    public const string FolderRestore = "folder.restore";
    public const string FolderPermanentDelete = "folder.permanent_delete";
    public const string FileUpload = "file.upload";
    public const string FileDownload = "file.download";
    public const string FileRename = "file.rename";
    public const string FileMove = "file.move";
    public const string FileDelete = "file.delete";
    public const string FileRestore = "file.restore";
    public const string FilePermanentDelete = "file.permanent_delete";
    public const string FilePurge = "file.purge";
    public const string FileMetadataUpdate = "file.metadata_update";
    public const string FileMetadataStripEmbedded = "file.metadata_strip_embedded";
    public const string FileMetadataWriteDateTaken = "file.metadata_write_datetaken";
    public const string FileDownloadPrivacySafe = "file.download_privacy_safe";
    public const string ShareCreate = "share.create";
    public const string ShareRevoke = "share.revoke";
    public const string SharePublicDownload = "share.public_download";
    public const string BlobPurge = "blob.purge";
    public const string TrashEmpty = "trash.empty";
    public const string AlbumCreate = "album.create";
    public const string AlbumUpdate = "album.update";
    public const string AlbumDelete = "album.delete";
    public const string AlbumItemAdd = "album.item_add";
    public const string AlbumItemRemove = "album.item_remove";
    // Bulk add/remove of many gallery-selected items to/from an album. Metadata
    // carries safe counts only (requested/succeeded/skipped), never file ids en masse.
    public const string AlbumItemsBulkAdd = "album.items_bulk_add";
    public const string AlbumItemsBulkRemove = "album.items_bulk_remove";

    // SHARE-ALBUM-01: live album sharing between authenticated users. The ACTOR
    // recorded on each event is whoever performed it — the album owner for
    // invite/update/revoke, the RECIPIENT for accept/decline/download — so the
    // trail never conflates the two. Metadata carries the album id, the
    // membership id and the role only: never the recipient's email, display
    // name or user id, never a file name, and never storage internals.
    public const string AlbumShareInvite = "album.share_invite";
    public const string AlbumShareUpdate = "album.share_update";
    public const string AlbumShareRevoke = "album.share_revoke";
    public const string AlbumShareAccept = "album.share_accept";
    public const string AlbumShareDecline = "album.share_decline";
    // A member downloading an ORIGINAL from a shared album. Distinct from
    // file.download so an owner's own download and a share download are
    // distinguishable in the trail.
    public const string AlbumShareDownload = "album.share_download";

    // SHARE-ALBUM-02: the owner promoting Viewer → Contributor or demoting
    // Contributor → Viewer. Metadata: membership id + the new role.
    public const string AlbumShareRoleChange = "album.share_role_change";

    // SHARE-ALBUM-02: linked, revocable contributions. Three actors are
    // genuinely distinct here and the trail must keep them apart — the ACTOR
    // (AuditLog.UserId), the ALBUM OWNER, and the SOURCE-FILE OWNER — because
    // "who removed whose media from whose album" is the question these events
    // exist to answer. Metadata therefore carries albumOwnerUserId and
    // sourceOwnerUserId alongside the album and file ids, plus a removal
    // reason. Never a file name, person name, storage path or blob identity.
    public const string AlbumContributionAdd = "album.contribution_add";
    // The source owner taking their own contribution back.
    public const string AlbumContributionWithdraw = "album.contribution_withdraw";
    // The album owner removing an item — their own or a contribution.
    public const string AlbumContributionRemove = "album.contribution_remove";
    // Automatic withdrawal because the contributor's membership was revoked.
    // One event per withdrawn item, all inside the revocation's transaction.
    public const string AlbumContributionAutoWithdraw = "album.contribution_auto_withdraw";

    // SHARE-ALBUM-03: COLLABORATIVE curation. The actor is whoever performed it
    // — an Editor or the Owner — and is never inferred from album ownership,
    // because on this surface the two are genuinely different people.
    // Metadata carries the album id and the resulting version; never a file
    // name, person name, storage path or blob identity.
    public const string AlbumEditDetails = "album.edit_details";
    public const string AlbumEditCover = "album.edit_cover";
    public const string AlbumEditReorder = "album.edit_reorder";
    // Editorial removal of ANY item. Distinct from album.contribution_withdraw
    // (the source owner taking their own item back) because they are different
    // ACTIONS: which one is recorded follows the endpoint invoked, not the
    // actor's identity.
    public const string AlbumEditRemoveItem = "album.edit_remove_item";

    // SHARE-COPY-01: one-time DETACHED album copy. Deliberately a separate
    // action family from album.share_* — a transfer is not a membership, and
    // conflating the two would make "who was ever given a copy of this album"
    // unanswerable from the audit log. Metadata carries counts, the transfer id
    // and the two user ids only: never a storage key, blob id, SHA, file name
    // or path.
    //
    // Recorded against the SENDER's action. The snapshot itself is immutable, so
    // there is no "transfer update".
    public const string AlbumTransferSend = "album.transfer_send";
    // The sender withdrew a pending offer before the recipient answered.
    public const string AlbumTransferCancel = "album.transfer_cancel";
    // The recipient accepted. Metadata records the destination album id, which
    // is the recipient's own from that moment on.
    public const string AlbumTransferAccept = "album.transfer_accept";
    public const string AlbumTransferDecline = "album.transfer_decline";
    // The pending window elapsed. Written by the cleanup path with no actor,
    // distinct from a decline so an unanswered offer is never recorded as a
    // decision the recipient made.
    public const string AlbumTransferExpire = "album.transfer_expire";

    // Slice 81: admin server-side import started (metadata: target user + run id).
    public const string AdminImportStart = "admin.import_start";
    // Slice 82: admin requested cancellation of an import run.
    public const string AdminImportCancel = "admin.import_cancel";
    // Slice 90: admin requested cancellation of a background job.
    public const string AdminJobCancel = "admin.job_cancel";
    // Admin console: admin enqueued a background job from the UI. Metadata
    // carries only the command key — never the submitted parameters.
    public const string AdminJobEnqueue = "admin.job_enqueue";
    // Slice 92: admin enqueued a derivatives backfill for an import run.
    public const string AdminImportEnqueueDerivatives = "admin.import_enqueue_derivatives";
    public const string AdminMediumPreviewRebuild = "admin.media_medium_preview_rebuild";
    // Slice 93: web remote-staging upload lifecycle.
    public const string StagingSessionCreate = "staging.session_create";
    public const string StagingManifestAccept = "staging.manifest_accept";
    public const string StagingVerifyComplete = "staging.verify_complete";
    public const string StagingImportStart = "staging.import_start";
    public const string StagingSessionCancel = "staging.session_cancel";
    public const string StagingSessionDelete = "staging.session_delete";

    // Phase 2: photo date-taken organizer run lifecycle (aggregate counts only).
    public const string OrganizerRunStart = "organizer.run_start";
    public const string OrganizerRunComplete = "organizer.run_complete";
    public const string OrganizerRunCancel = "organizer.run_cancel";
    public const string OrganizerRunFail = "organizer.run_fail";

    // Photo archive export session lifecycle (aggregate counts only; never the
    // token). Per-file streaming reuses FileDownload.
    public const string PhotoExportCreate = "photo_export.create";
    public const string PhotoExportRevoke = "photo_export.revoke";

    // Private Vault lifecycle (aggregate counts only; NEVER the password or
    // token). Unlock FAILURES are not audited per-attempt to avoid any
    // vault-existence signal in the audit trail.
    public const string PrivateVaultSetup = "private_vault.setup";
    public const string PrivateVaultUnlock = "private_vault.unlock";
    public const string PrivateVaultLock = "private_vault.lock";
    public const string PrivateVaultMoveIn = "private_vault.move_in";
    public const string PrivateVaultMoveOut = "private_vault.move_out";

    // Slice 3 (media organization): per-file media-library exclusion. Metadata
    // carries counts only (requested/changed) — never file names, titles, tags,
    // or paths.
    public const string MediaLibraryExclude = "media_library.exclude";
    public const string MediaLibraryRestore = "media_library.restore";

    // Owner-side TV device/session management. Revoke terminates a paired TV
    // session immediately (no token/hash recorded).
    public const string TvSessionRevoke = "tv_session.revoke";

    // Owner approved a TV pairing (atomic with first PIN creation when the
    // owner had none — metadata carries only the pinCreated flag).
    public const string TvPairingApprove = "tv_pairing.approve";

    // TV Personal Area lifecycle. pin_create is the owner's FIRST PIN (from the
    // pairing flow or settings); pin_change is an owner-side change/reset
    // (metadata: count of unlock grants it revoked); unlock/unlock_failure/lock
    // are TV-session actions. NEVER records the PIN, its hash, or the grant
    // token — ids + safe aggregate metadata only.
    public const string TvPersonalPinCreate = "tv_personal.pin_create";
    public const string TvPersonalPinChange = "tv_personal.pin_change";
    public const string TvPersonalUnlock = "tv_personal.unlock";
    public const string TvPersonalUnlockFailure = "tv_personal.unlock_failure";
    public const string TvPersonalLock = "tv_personal.lock";
    // TV Personal Gallery mutations (grant-gated). favorite_set carries the file
    // id + new state; album_bulk_add the album id + requested/succeeded/skipped
    // counts — never names, paths, or storage internals.
    public const string TvPersonalFavoriteSet = "tv_personal.favorite_set";
    public const string TvPersonalAlbumBulkAdd = "tv_personal.album_bulk_add";
    // Natural-language command interpretation. Metadata carries SAFE FACTS ONLY
    // (outcome bucket + interpreter key) — never the command text, names or dates.
    public const string TvPersonalInterpretCommand = "tv_personal.interpret_command";

    // Public read-only party album links. Enable/revoke are owner actions;
    // public_view is logged on an anonymous album open (no token/hash recorded).
    public const string PartyEnable = "party.enable";
    public const string PartyRevoke = "party.revoke";
    public const string PartyPublicView = "party.public_view";
    // Anonymous party upload batch (aggregate counts only; never the token/hash,
    // storage keys, paths, or raw metadata).
    public const string PartyUpload = "party.upload";
    // Owner-side moderation of guest party uploads (album + file id only; never
    // the token/hash, storage keys, paths, or raw metadata).
    public const string PartyUploadHide = "party.upload.hide";
    public const string PartyUploadApprove = "party.upload.approve";
    public const string PartyUploadReject = "party.upload.reject";
    public const string PartyUploadRestore = "party.upload.restore";
    public const string PartyApprovalModeEnable = "party.upload.approval_mode.enable";
    public const string PartyApprovalModeDisable = "party.upload.approval_mode.disable";
    // Anonymous party "find your face" search (aggregate: album id + safe status +
    // result count only; never the uploaded selfie, token/hash, query vector,
    // face/person ids, or similarity scores).
    public const string PartyFaceSearch = "party.face_search";
    // Explicit "show these photos on TV" activation of a face search (aggregate:
    // album id + accepted/reason only) and its deletion/cancellation (from the
    // guest's phone or the TV; aggregate metadata only).
    public const string PartyFaceSearchActivateTv = "party.face_search.activate_tv";
    public const string PartyFaceSearchDelete = "party.face_search.delete";

    // Owner-private Plates (Targhe) surface. Upload/download/delete of a plate
    // image reference (metadata: safe display fields / counts only; never blob
    // ids, storage keys, hashes, paths, or the logical container key).
    public const string PlateUpload = "plate.upload";
    // Owner added existing gallery image(s) into Plates (metadata: safe counts only).
    public const string PlateAddFromGallery = "plate.add_from_gallery";
    public const string PlateDownload = "plate.download";
    public const string PlateDelete = "plate.delete";
    // Owner requested ALPR analysis of a plate image (metadata: job id + status).
    public const string PlateAnalyzeRequest = "plate.analysis_request";

    // Owner-private Aesthetics Lab (Laboratorio estetico). Safe COUNTS/status
    // only — never image content, metrics, text, blob ids, storage keys, hashes,
    // paths, or the logical container key.
    public const string AestheticLabAdd = "aesthetics.item_add";
    public const string AestheticLabRemove = "aesthetics.item_remove";
    // Owner requested analysis of a batch (metadata: enqueued/skipped counts).
    public const string AestheticAnalyzeRequest = "aesthetics.analysis_request";
    public const string AestheticAnalyzeCancel = "aesthetics.analysis_cancel";
    public const string AestheticAnalyzeRetry = "aesthetics.analysis_retry";

    // TV "Beauty Lab" QR mobile-upload session. Safe COUNTS/lifecycle only —
    // never the token, filename, image content, or storage internals.
    public const string AestheticUploadSessionCreate = "aesthetics.upload_session_create";
    public const string AestheticUploadSessionRevoke = "aesthetics.upload_session_revoke";
    public const string AestheticUploadSessionExpire = "aesthetics.upload_session_expire";
    // A mobile upload attempt against a session (metadata: accepted/rejected).
    public const string AestheticUploadSessionUpload = "aesthetics.upload_session_upload";

    // Admin user management (metadata: target user id via entityId only —
    // never password/hash, and email only where the existing login/admin
    // audit trail already records it).
    public const string AdminUserCreate = "admin.user.create";
    public const string AdminUserUpdate = "admin.user.update";
    public const string AdminUserPasswordReset = "admin.user.password.reset";
    public const string AdminUserAdminGrant = "admin.user.admin.grant";
    public const string AdminUserAdminRevoke = "admin.user.admin.revoke";
    public const string AdminUserDisable = "admin.user.disable";
    public const string AdminUserEnable = "admin.user.enable";
    // Self-service password change (authenticated user, own account only).
    public const string AuthPasswordChange = "auth.password.change";
}

public static class AuditEntityTypes
{
    public const string PrivateVault = "private_vault";
    public const string MediaLibrary = "media_library";
    public const string User = "user";
    public const string Folder = "folder";
    public const string File = "file";
    public const string ShareLink = "share_link";
    public const string Blob = "blob";
    public const string Trash = "trash";
    public const string Album = "album";
    // SHARE-ALBUM-01: a recipient's accept/decline is recorded against the
    // MEMBERSHIP, not the album — the recipient does not own the album and the
    // entity they acted on is their own grant.
    public const string AlbumMembership = "album_membership";
    // SHARE-ALBUM-02: contribution events are recorded against the FILE, since
    // the media is what moved in or out of the album; the album id travels in
    // the metadata.
    public const string AlbumContribution = "album_contribution";

    // SHARE-COPY-01: a one-time detached copy offer. Separate from
    // album_membership because it grants no ongoing access to anything — it
    // either becomes the recipient's own independent album or it becomes
    // nothing.
    public const string AlbumTransfer = "album_transfer";
    public const string AdminImport = "admin_import";
    public const string BackgroundJob = "background_job";
    public const string StagingSession = "staging_session";
    public const string OrganizerRun = "organizer_run";
    public const string PhotoExportSession = "photo_export_session";
    public const string TvSession = "tv_session";
    public const string TvPairing = "tv_pairing";
    public const string PartyAlbum = "party_album";
    public const string Plate = "plate";
    public const string AestheticLabItem = "aesthetic_lab_item";
    public const string AestheticRun = "aesthetic_run";
}
