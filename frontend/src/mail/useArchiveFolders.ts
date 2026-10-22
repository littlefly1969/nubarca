import { useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { listMailFolders } from './mailApi';
import type { MailFolder } from './types';

export type FolderLoad = { kind: 'loading' } | { kind: 'ready'; folders: MailFolder[] } | { kind: 'error' };

// The folder lists of the given archives, fetched once each and cached for the
// life of the page. An archive's tree is small (folders, not messages) and the
// endpoint returns it whole.
export function useArchiveFolders(archiveIds: readonly string[]): Record<string, FolderLoad> {
  const { invalidateAuth } = useAuth();
  const [byArchive, setByArchive] = useState<Record<string, FolderLoad>>({});
  const requested = useRef(new Set<string>());
  const key = [...new Set(archiveIds)].sort().join('|');

  useEffect(() => {
    for (const id of key.split('|')) {
      if (!id || requested.current.has(id)) continue;
      requested.current.add(id);
      setByArchive((prev) => ({ ...prev, [id]: { kind: 'loading' } }));
      listMailFolders(id)
        .then((folders) => setByArchive((prev) => ({ ...prev, [id]: { kind: 'ready', folders } })))
        .catch((err: unknown) => {
          if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
          // Allow a later render to try again.
          requested.current.delete(id);
          setByArchive((prev) => ({ ...prev, [id]: { kind: 'error' } }));
        });
    }
  }, [key, invalidateAuth]);

  return byArchive;
}
