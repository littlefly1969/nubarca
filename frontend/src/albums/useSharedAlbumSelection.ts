import { useEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import {
  addSharedAlbumSelectionItems,
  clearSharedAlbumSelection,
  getSharedAlbumSelection,
  listSharedAlbumSelectionIds,
  removeSharedAlbumSelectionItems,
} from '@nubarca/api-client';
import { MediaSelectionStore } from '../selection/mediaSelectionStore';
import { startSelectionSync, type SelectionSyncTransport } from '../selection/selectionSync';

// The member's own selection inside ONE shared album.
//
// A store of its own per album — never the account selection, so nothing picked
// here can reach a library action, and nothing picked in the library shows up
// here. It is mirrored to /api/shared-albums/{id}/selection, so the member
// finds it again from another browser or phone. The server only ever accepts
// what the album serves this member now; a revoked membership is a 404, which
// stops the sync and forgets the picks on screen.
export function sharedAlbumSelectionTransport(albumId: string): SelectionSyncTransport {
  return {
    summary: async (signal) => ({ id: albumId, ...(await getSharedAlbumSelection(albumId, signal)) }),
    ids: (_list, cursor, signal) => listSharedAlbumSelectionIds(albumId, cursor, 1000, signal),
    add: (_list, ids) => addSharedAlbumSelectionItems(albumId, ids),
    remove: (_list, ids) => removeSharedAlbumSelectionItems(albumId, ids),
    clear: () => clearSharedAlbumSelection(albumId),
  };
}

export function useSharedAlbumSelection(
  albumId: string,
  { sync = true, onGone, onUnauthorized }: { sync?: boolean; onGone(): void; onUnauthorized(): void },
) {
  const store = useMemo(() => {
    void albumId; // one store per album
    return new MediaSelectionStore();
  }, [albumId]);
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  // Read when an answer arrives, so a new callback never restarts the sync.
  const callbacks = useRef({ onGone, onUnauthorized });
  useEffect(() => { callbacks.current = { onGone, onUnauthorized }; });

  useEffect(() => {
    if (!sync) return undefined;
    const handle = startSelectionSync(store, sharedAlbumSelectionTransport(albumId), {
      onGone: () => { store.adopt([]); callbacks.current.onGone(); },
      onUnauthorized: () => callbacks.current.onUnauthorized(),
    });
    const onVisible = () => { if (document.visibilityState === 'visible') void handle.refresh(); };
    window.addEventListener('focus', onVisible);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener('focus', onVisible);
      document.removeEventListener('visibilitychange', onVisible);
      void handle.flush().finally(() => handle.stop());
    };
  }, [store, albumId, sync]);

  return { store, snapshot };
}
