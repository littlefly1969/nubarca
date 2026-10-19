import { createContext, useContext, useEffect, useMemo, type ReactNode } from 'react';
import {
  addMediaListItems,
  clearMediaList,
  getCurrentSelection,
  listMediaListIds,
  listMediaListItems,
  removeMediaListItems,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { MediaSelectionStore } from './mediaSelectionStore';
import { startSelectionSync, type SelectionSyncTransport } from './selectionSync';

// The owner's ONE account selection, above every screen.
//
// Keyed on the signed-in user: a different account (or signing out) gets a
// fresh, empty store, so nothing picked by one person can ever be offered to
// the next one on the same browser. Shared albums and parties do NOT use this
// store — their selections are personal to that album and never mix with the
// private library.
//
// The store is mirrored to the server's current selection, so the same picks
// follow the account to its other browsers, phones and televisions.
const AccountSelectionContext = createContext<MediaSelectionStore | null>(null);

export const webSelectionTransport: SelectionSyncTransport = {
  summary: (signal) => getCurrentSelection(signal),
  ids: (listId, cursor, signal) => listMediaListIds(listId, cursor, 1000, signal),
  // The first hundred, so the tray has thumbnails right away.
  items: async (listId, signal) => (await listMediaListItems(listId, null, 100, signal)).items,
  add: (listId, ids) => addMediaListItems(listId, ids),
  remove: (listId, ids) => removeMediaListItems(listId, ids),
  clear: (listId) => clearMediaList(listId),
};

export function AccountSelectionProvider(
  { children, sync = true }: { children: ReactNode; sync?: boolean },
) {
  const { state } = useAuth();
  const userId = state.status === 'authed' ? state.user.id : null;
  const store = useMemo(() => (userId === null ? null : new MediaSelectionStore()), [userId]);

  useEffect(() => {
    if (store === null || !sync) return undefined;
    const handle = startSelectionSync(store, webSelectionTransport);
    // Coming back to the tab is the moment another device most likely changed it.
    const onVisible = () => { if (document.visibilityState === 'visible') void handle.refresh(); };
    window.addEventListener('focus', onVisible);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener('focus', onVisible);
      document.removeEventListener('visibilitychange', onVisible);
      // Whatever is still queued is sent before letting go.
      void handle.flush().finally(() => handle.stop());
    };
  }, [store, sync]);

  return <AccountSelectionContext.Provider value={store}>{children}</AccountSelectionContext.Provider>;
}

// Null outside the authenticated app (and in isolated component tests): the
// caller then keeps a selection local to itself, as before.
export function useAccountSelectionStore(): MediaSelectionStore | null {
  return useContext(AccountSelectionContext);
}
