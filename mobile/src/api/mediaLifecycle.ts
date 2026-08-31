// Mobile TRANSPORT for the media lifecycle actions a selection can perform.
//
// THE DISTINCTION THIS FILE EXISTS TO KEEP (§24): moving to Trash and removing
// from an album are different verbs on different things.
//
//   moveToTrash(fileItemId)          -> the FILE leaves the library (restorable)
//   bulkRemoveAlbumItems(albumId, …) -> only MEMBERSHIP changes; the file stays
//
// Confirmation wording on the phone must reflect that, and a bulk album
// removal must never be routed through here by mistake.
//
// Bulk is deliberately a loop of single-item requests: the backend exposes no
// bulk trash route, and inventing a client-side "bulk" that hides partial
// failure would report success for items that were never deleted. The caller
// gets the counts and decides what to say.

import { apiDelete, apiPost } from './client.ts';
import { fileItemPath, fileRestorePath } from '@nubarca/contracts';

/** Soft-delete one file: it leaves the library listings and can be restored. */
export function moveToTrash(fileItemId: string, signal?: AbortSignal): Promise<void> {
  return apiDelete<void>(fileItemPath(fileItemId), undefined, { signal });
}

/** Bring one soft-deleted file back into the library. */
export function restoreFromTrash(fileItemId: string, signal?: AbortSignal): Promise<void> {
  return apiPost<void>(fileRestorePath(fileItemId), undefined, { signal });
}

export interface BulkLifecycleResult {
  requested: number;
  succeeded: number;
  failed: number;
}

/**
 * Apply a per-item lifecycle action across a selection.
 *
 * Runs sequentially and counts outcomes rather than failing on the first
 * error: a half-applied action the user is not told about is worse than a
 * slower one they are. A cancelled signal stops the loop and reports what had
 * already been done, so the caller never claims more than happened.
 */
export async function applyToSelection(
  fileItemIds: readonly string[],
  action: (id: string, signal?: AbortSignal) => Promise<void>,
  signal?: AbortSignal,
): Promise<BulkLifecycleResult> {
  let succeeded = 0;
  let failed = 0;
  for (const id of fileItemIds) {
    if (signal?.aborted) break;
    try {
      await action(id, signal);
      succeeded += 1;
    } catch {
      failed += 1;
    }
  }
  return { requested: fileItemIds.length, succeeded, failed };
}
