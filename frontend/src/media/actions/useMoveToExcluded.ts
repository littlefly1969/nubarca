import { useCallback, useState } from 'react';
import { excludeFromMediaLibrary, type MediaLibraryBulkResult } from '@nubarca/api-client';

// Slice 3: "Move selection to Excluded" state, used identically by the photo and
// video galleries. Mirrors useMoveToPersonal's shape (immutable id snapshot at
// open time; full-vs-partial success decided once) but is deliberately
// DECOUPLED from the Private-Vault token flow — excluding needs no password.
export interface MoveToExcludedPartialOutcome {
  moved: number;
  total: number;
}

export interface UseMoveToExcludedOptions {
  onFullSuccess(ids: string[]): void;
  onPartialSuccess(outcome: MoveToExcludedPartialOutcome): void;
}

export interface UseMoveToExcluded {
  isOpen: boolean;
  ids: string[];
  open(ids: string[]): void;
  close(): void;
  // Performs the exclude call and dispatches onFullSuccess/onPartialSuccess.
  // Rejects on any API error (the dialog interprets it).
  execute(): Promise<MediaLibraryBulkResult>;
}

export function useMoveToExcluded(options: UseMoveToExcludedOptions): UseMoveToExcluded {
  const [ids, setIds] = useState<string[]>([]);
  const [isOpen, setIsOpen] = useState(false);

  const open = useCallback((snapshot: string[]) => {
    setIds(snapshot);
    setIsOpen(true);
  }, []);

  const close = useCallback(() => setIsOpen(false), []);

  const execute = useCallback(async (): Promise<MediaLibraryBulkResult> => {
    const result = await excludeFromMediaLibrary(ids);
    // Every selected file was Active, so a full move changes all of them.
    if (result.changed === ids.length) {
      options.onFullSuccess(ids);
    } else {
      options.onPartialSuccess({ moved: result.changed, total: ids.length });
    }
    return result;
  }, [ids, options]);

  return { isOpen, ids, open, close, execute };
}
