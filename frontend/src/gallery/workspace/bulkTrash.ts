import { deleteFile } from '@nubarca/api-client';

// Partial result of a bulk move-to-Trash. `moved` items are cleared from the
// selection; `failed` items stay selected so the user can retry.
export interface BulkTrashResult {
  moved: string[];
  failed: string[];
}

// There is no bulk soft-delete endpoint (soft delete → Trash → restore is
// single-item), so we drive the existing per-file DELETE with BOUNDED
// concurrency and collect a partial result — no large new bulk-job system. Each
// item is independent: one failure never aborts the batch. `deleteOne` is
// injectable for tests. 401s propagate so the caller can invalidate auth.
export async function moveFilesToTrash(
  ids: string[],
  options: {
    concurrency?: number;
    signal?: AbortSignal;
    deleteOne?: (id: string, signal?: AbortSignal) => Promise<void>;
    onAuthError?: () => void;
  } = {},
): Promise<BulkTrashResult> {
  const { concurrency = 4, signal, deleteOne = deleteFile, onAuthError } = options;
  const moved: string[] = [];
  const failed: string[] = [];
  let index = 0;
  let authFailed = false;

  async function worker() {
    while (index < ids.length && !authFailed) {
      const id = ids[index++];
      try {
        await deleteOne(id, signal);
        moved.push(id);
      } catch (err) {
        const status = (err as { status?: number } | null)?.status;
        if (status === 401) {
          authFailed = true;
          onAuthError?.();
        }
        failed.push(id);
      }
    }
  }

  const workers = Array.from({ length: Math.min(concurrency, ids.length) }, () => worker());
  await Promise.all(workers);

  // Any ids not reached because auth failed mid-batch count as failed.
  for (const id of ids) {
    if (!moved.includes(id) && !failed.includes(id)) failed.push(id);
  }
  return { moved, failed };
}
