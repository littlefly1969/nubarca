// Core vocabulary for device-media synchronization (mobile-sync-v1).
//
// Sync is an ingestion SOURCE for the existing NubArca media model: this file
// describes only sync-side concepts (ledger rows, engine phases, settings).
// Blob identity, deduplication and ownership stay exclusively server-side.

/** Persistent per-asset synchronization state (the ledger's item states). */
export type SyncItemState =
  | 'pending'
  | 'uploading'
  | 'completed'
  | 'failed-retryable'
  | 'failed-permanent'
  | 'skipped';

/** One durable ledger row: the minimum needed to resume safely after death. */
export interface SyncItem {
  /** Stable platform media identity (MediaStore id / PHAsset id). */
  assetId: string;
  /** Asset modification time (ms) at enqueue — the revision we know about. */
  revision: number;
  filename: string | null;
  isVideo: boolean;
  state: SyncItemState;
  attempts: number;
  /** Epoch ms when the next attempt may run; null = as soon as possible. */
  nextAttemptAt: number | null;
  /** Opaque OPERATION identity for replay-safe ingestion (never a hash). */
  operationKey: string;
  /** Server FileSummary id once ingestion committed. */
  fileId: string | null;
  updatedAt: number;
}

/** User-controlled sync configuration. Persisted per account, never secret. */
export interface SyncSettings {
  enabled: boolean;
  /** Privacy-conservative default: automatic uploads only on Wi-Fi. */
  wifiOnly: boolean;
  /**
   * false = "new photos and videos from now on" (the default enablement);
   * true = the SEPARATE explicit choice to also ingest historical media.
   */
  includeExisting: boolean;
}

export const DEFAULT_SETTINGS: SyncSettings = {
  enabled: false,
  wifiOnly: true,
  includeExisting: false,
};

export type NetworkKind = 'wifi' | 'cellular' | 'none' | 'unknown';

export interface NetworkState {
  kind: NetworkKind;
}

/**
 * Coarse engine activity phase. Derived from live work + policy, mirrored to
 * observers; the LEDGER remains the authoritative durable state.
 */
export type EnginePhase =
  | 'disabled'
  | 'permission-blocked'
  | 'paused'
  | 'waiting-network'
  | 'discovering'
  | 'working'
  | 'idle';

/** Everything an observer (UI) needs, in one immutable snapshot. */
export interface EngineSnapshot {
  readonly settings: SyncSettings;
  readonly phase: EnginePhase;
  readonly permission: PermissionState;
  /** True while paused BECAUSE the session died (distinct from user pause). */
  readonly authRequired: boolean;
  readonly pendingCount: number;
  readonly retryableCount: number;
  readonly permanentCount: number;
  readonly uploadingCount: number;
  readonly completedCount: number;
  readonly skippedCount: number;
  readonly lastSyncAt: number | null;
}

export type PermissionState = 'undetermined' | 'granted' | 'limited' | 'denied';

/** Minimal platform media-library surface the engine needs (see adapters). */
export interface PagedAsset {
  id: string;
  mediaType: 'photo' | 'video';
  filename: string;
  /** Modification time in ms — treated as the asset revision. */
  modificationTime: number;
}

export interface AssetPage {
  assets: PagedAsset[];
  hasNextPage: boolean;
  /** Opaque continuation cursor; null when exhausted. */
  endCursor: string | null;
}

export interface MediaLibraryPort {
  /** Current access level WITHOUT ever prompting. */
  getPermissions(): Promise<PermissionState>;
  /**
   * Prompt ONLY from an explicit user gesture (enablement). Never called by
   * the engine itself.
   */
  requestPermissions(): Promise<PermissionState>;
  /** One bounded page of photo/video assets, oldest-stable-order first. */
  getPage(cursor: string | null, pageSize: number): Promise<AssetPage>;
  /**
   * Re-read one asset right before upload. Null means the asset vanished or
   * the OS no longer exposes it — the queue item is then skipped, never
   * treated as a failure.
   */
  getLocalInfo(assetId: string): Promise<{ uri: string } | null>;
}

export interface ConnectivityPort {
  getNetworkState(): Promise<NetworkState>;
  /** Subscribe to changes; returns an unsubscribe function. Optional. */
  onNetworkChange?(listener: (state: NetworkState) => void): () => void;
}

/** What one logical upload needs from the transport. */
export interface UploadRequest {
  localUri: string;
  filename: string;
  mimeType: string;
  /** Opaque operation identity sent as `Idempotency-Key`. Never a hash. */
  operationKey: string;
  timeoutMs: number;
  signal: AbortSignal;
}

/** Mirrors the server FileSummary contract for POST /api/files. */
export interface UploadedFile {
  id: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  width: number | null;
  height: number | null;
}

export type UploadOutcome =
  | { kind: 'committed'; file: UploadedFile }
  | { kind: 'replayed'; file: UploadedFile };

export type UploaderPort = (request: UploadRequest) => Promise<UploadedFile>;

/** Why a single upload attempt did not succeed (engine-side classification). */
export type FailureClass =
  | 'network'
  | 'timeout'
  | 'retryable-status'
  | 'auth'
  | 'permanent-status'
  | 'cancelled';