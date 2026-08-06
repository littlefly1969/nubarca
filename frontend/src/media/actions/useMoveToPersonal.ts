import { useCallback, useState } from 'react';
import { vaultMoveIn, type VaultMoveResult } from '@nubarca/api-client';

// Shared "Move selection to Personal" state, used identically by the photo and
// video galleries. Owns: whether the dialog is open, an IMMUTABLE snapshot of
// the ids captured at open time (the live selection may keep changing behind
// a modal; the request must not), and the move-in execution + the small
// full-vs-partial-success decision that both galleries need.
//
// Reconciling the gallery's own item list/total after a full success, or
// triggering its refetch after a partial one, is caller-specific (different
// state shapes for photos vs videos) — that part stays in each page via the
// two callbacks below. Everything else about "what happened" is decided once,
// here, so the two galleries can never drift apart on the semantics.
export interface MoveToPersonalPartialOutcome {
  moved: number;
  total: number;
}

export interface UseMoveToPersonalOptions {
  // Every requested id was moved: the caller can prune its local list.
  onFullSuccess(ids: string[]): void;
  // Counts didn't match: the caller cannot know which ids moved, so it must
  // clear the selection and reload the current query from the server.
  onPartialSuccess(outcome: MoveToPersonalPartialOutcome): void;
}

export interface UseMoveToPersonal {
  isOpen: boolean;
  ids: string[];
  open(ids: string[]): void;
  close(): void;
  // Performs the move-in call with the given (already unlocked) token and
  // dispatches onFullSuccess/onPartialSuccess. Rejects on any API error —
  // the caller (the dialog) interprets 401 vs. other failures.
  execute(token: string): Promise<VaultMoveResult>;
}

export function useMoveToPersonal(options: UseMoveToPersonalOptions): UseMoveToPersonal {
  const [ids, setIds] = useState<string[]>([]);
  const [isOpen, setIsOpen] = useState(false);

  const open = useCallback((snapshot: string[]) => {
    setIds(snapshot);
    setIsOpen(true);
  }, []);

  const close = useCallback(() => setIsOpen(false), []);

  const execute = useCallback(async (token: string): Promise<VaultMoveResult> => {
    const result = await vaultMoveIn(token, { fileIds: ids, folderIds: [] });
    if (result.movedFiles === ids.length) {
      options.onFullSuccess(ids);
    } else {
      options.onPartialSuccess({ moved: result.movedFiles, total: ids.length });
    }
    return result;
  }, [ids, options]);

  return { isOpen, ids, open, close, execute };
}
