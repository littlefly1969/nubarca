// Orchestrator for the shared-album ORIGINAL download (file-native, with
// guaranteed artifact cleanup).
//
// PRIVACY INVARIANT: a shared-album original downloaded for external sharing
// must not persist on the device after the operation finishes — successful,
// cancelled by the share sheet, or failed at ANY step. Shared media belongs to
// another account; leaving bytes behind in cacheDirectory means private media
// from account A can outlive logout until the OS eventually evicts the cache.
//
// The bytes NEVER pass through the JS heap or a base64 expansion: expo's
// downloader writes them straight to disk under the session cookie, so even a
// multi-hundred-MB original cannot OOM the app. Every artifact of ONE share
// attempt lives inside a UNIQUE per-operation directory:
//
//   cacheDirectory/nubarca-share-<unique-id>/original      (download target)
//   cacheDirectory/nubarca-share-<unique-id>/<final name>  (shared path)
//
// so a single recursive idempotent delete in `finally` removes them all, and
// two downloads that resolve the SAME server filename can never collide.
//
// Filesystem and sharing APIs are injected through SharedDownloadIo (the same
// seam pattern as videoProbe) so this module stays importable by node --test;
// app/shared-album/[id].tsx binds the real expo implementation.

import { buildDownloadName, pickHeader, type HeaderBag } from './downloadName.ts';

/** Every directory this module creates starts with this prefix. */
export const SHARE_DIR_PREFIX = 'nubarca-share-';

/**
 * The filesystem/sharing surface the orchestrator needs. One object binds the
 * whole operation so tests can record every call and fail any step.
 */
export interface SharedDownloadIo {
  /** App cache root (expo `FileSystem.cacheDirectory`). */
  cacheDirectory: string;
  /** Unique-per-invocation id for the operation directory. */
  makeOperationId(): string;
  makeDirectoryAsync(path: string, options?: { intermediates?: boolean }): Promise<void>;
  downloadAsync(
    uri: string,
    targetUri: string,
    options?: { headers?: Record<string, string> },
  ): Promise<{ status: number; headers: HeaderBag }>;
  moveAsync(options: { from: string; to: string }): Promise<void>;
  /**
   * Must be recursive and tolerate an ALREADY-ABSENT path when called with
   * `{ idempotent: true }` (expo's contract), so repeated cleanup is safe.
   */
  deleteAsync(path: string, options?: { idempotent?: boolean }): Promise<void>;
  shareAsync(
    uri: string,
    options?: { mimeType?: string; dialogTitle?: string },
  ): Promise<unknown>;
}

export interface SharedAlbumOriginalDownloadRequest {
  /** Authenticated, album-scoped source exactly as provided by the API. */
  source: { uri: string; headers: Record<string, string> };
  /** Extension used only when neither Content-Disposition nor MIME names one. */
  kindFallbackExtension: string;
  /** Title handed to the share sheet. */
  dialogTitle: string;
}

/**
 * Operation id: timestamp + randomness, unique per invocation. Two downloads
 * of identically-named originals therefore get distinct operation directories
 * instead of racing over one deterministic path.
 */
export function makeSharedDownloadOperationId(): string {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * Download one shared-album original natively, offer it through the share
 * sheet, then remove EVERY file this invocation created — including when the
 * download, move or share fails, and when the user merely dismisses the sheet.
 *
 * Cleanup semantics:
 *   operation FAILS (+ cleanup fails) -> the ORIGINAL operation error is
 *   preserved: cleanup problems never mask what actually went wrong;
 *   operation SUCCEEDS (+ cleanup fails) -> the cleanup failure is SURFACED:
 *   a share that leaves private bytes on disk must never report success.
 */
export async function runSharedAlbumOriginalDownload(
  io: SharedDownloadIo,
  request: SharedAlbumOriginalDownloadRequest,
): Promise<void> {
  const root = io.cacheDirectory;
  if (!root) throw new Error('cache directory unavailable');
  const separator = root.endsWith('/') ? '' : '/';
  const operationDirectory = `${root}${separator}${SHARE_DIR_PREFIX}${io.makeOperationId()}`;
  let operationFailed = false;
  try {
    await io.makeDirectoryAsync(operationDirectory, { intermediates: true });
    // The downloader needs SOME name before the server tells us the real one:
    // land the bytes inside the operation directory first, then move them to
    // the server-derived final name once the response headers are known.
    const tempUri = `${operationDirectory}/original`;
    const result = await io.downloadAsync(request.source.uri, tempUri, {
      headers: request.source.headers,
    });
    if (result.status < 200 || result.status >= 300) {
      throw new Error(`download failed with status ${result.status}`);
    }
    // Name and extension come from what the SERVER declared about its own
    // original (Content-Disposition / Content-Type) — never guessed from the
    // media kind. encodeURIComponent preserves the route's existing on-disk
    // naming rule; sanitize() inside buildDownloadName keeps path traversal out.
    const disposition = pickHeader(result.headers, 'content-disposition');
    const mimeType = pickHeader(result.headers, 'content-type');
    const fileName = encodeURIComponent(
      buildDownloadName({
        disposition,
        mimeType,
        kindFallbackExtension: request.kindFallbackExtension,
      }),
    );
    const finalUri = `${operationDirectory}/${fileName}`;
    await io.moveAsync({ from: tempUri, to: finalUri });
    await io.shareAsync(finalUri, {
      mimeType: mimeType ?? undefined,
      dialogTitle: request.dialogTitle,
    });
  } catch (error) {
    operationFailed = true;
    throw error;
  } finally {
    // One recursive delete owns every artifact of THIS invocation: the temp
    // download target, the moved final file and the directory itself. Runs on
    // every exit path; idempotent so an absent directory is not a failure.
    try {
      await io.deleteAsync(operationDirectory, { idempotent: true });
    } catch (cleanupError) {
      if (!operationFailed) {
        // The operation SUCCEEDED: a failed cleanup means private bytes may
        // remain on disk, which must never be reported as success.
        throw cleanupError;
      }
      // The operation already failed: preserve ITS error — a best-effort
      // cleanup problem must not mask what actually went wrong.
    }
  }
}

