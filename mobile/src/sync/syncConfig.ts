// Tuning constants for device-media synchronization.
//
// Every number here exists to keep the feature bounded: bounded memory during
// discovery, bounded concurrency on the wire, bounded retry pressure on the
// server, and no upload that can hang forever.

/** Assets materialized per discovery page — the only large-library footprint. */
export const DISCOVERY_PAGE_SIZE = 500;

/** Rows written per ledger transaction during discovery. */
export const DISCOVERY_BATCH_SIZE = 200;

/**
 * Conservative parallelism for original media over mobile networks. Two keeps
 * a long-video pipe saturated while leaving radio/thermal headroom; more
 * buys little and hurts the very uploads it parallelizes.
 */
export const MAX_CONCURRENT_UPLOADS = 2;

/**
 * Hard ceiling for ONE original-media upload. Generous enough for very large
 * videos on slow links (the server accepts up to 10 GiB); still finite so a
 * wedged connection can never pin a concurrency slot forever. Real
 * cancellation (pause/logout/disable) aborts sooner regardless.
 */
export const UPLOAD_TIMEOUT_MS = 15 * 60_000;

/** Exponential backoff: base delay for the first retry of an item. */
export const RETRY_BASE_DELAY_MS = 30_000;
/** Backoff cap for one item's retry cadence (Retry-After may exceed it). */
export const RETRY_MAX_DELAY_MS = 6 * 60 * 60_000;
/** A permanently failing item stops being retried after this many attempts. */
export const MAX_ATTEMPTS_PER_ITEM = 12;

/**
 * Fallback poll while Waiting-for-Wi-Fi when the platform gives us no
 * connectivity listener. Deliberately lazy: policy blocks work, it does not
 * busy-loop.
 */
export const NETWORK_POLL_MS = 60_000;

/**
 * How often an enabled, idle engine re-reconciles with the platform library
 * while the app is in the foreground (new photos taken since the last pass).
 * Discovery is metadata-only paging, so this stays cheap.
 */
export const RESCAN_INTERVAL_MS = 5 * 60_000;

export interface SyncConfig {
  discoveryPageSize: number;
  discoveryBatchSize: number;
  maxConcurrentUploads: number;
  uploadTimeoutMs: number;
  retryBaseDelayMs: number;
  retryMaxDelayMs: number;
  maxAttemptsPerItem: number;
  networkPollMs: number;
  rescanIntervalMs: number;
}

export const DEFAULT_CONFIG: SyncConfig = {
  discoveryPageSize: DISCOVERY_PAGE_SIZE,
  discoveryBatchSize: DISCOVERY_BATCH_SIZE,
  maxConcurrentUploads: MAX_CONCURRENT_UPLOADS,
  uploadTimeoutMs: UPLOAD_TIMEOUT_MS,
  retryBaseDelayMs: RETRY_BASE_DELAY_MS,
  retryMaxDelayMs: RETRY_MAX_DELAY_MS,
  maxAttemptsPerItem: MAX_ATTEMPTS_PER_ITEM,
  networkPollMs: NETWORK_POLL_MS,
  rescanIntervalMs: RESCAN_INTERVAL_MS,
};