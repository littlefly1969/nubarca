import { createContext, useContext, useMemo, type ReactNode } from 'react';
import { useAuth } from '../auth/useAuth';
import { MediaSelectionStore } from './mediaSelectionStore';

// The owner's ONE account selection, above every screen.
//
// Keyed on the signed-in user: a different account (or signing out) gets a
// fresh, empty store, so nothing picked by one person can ever be offered to
// the next one on the same browser. Shared albums and parties do NOT use this
// store — their selections are personal to that album and never mix with the
// private library (see albumSelectionStores).
const AccountSelectionContext = createContext<MediaSelectionStore | null>(null);

export function AccountSelectionProvider({ children }: { children: ReactNode }) {
  const { state } = useAuth();
  const userId = state.status === 'authed' ? state.user.id : null;
  const store = useMemo(() => (userId === null ? null : new MediaSelectionStore()), [userId]);
  return <AccountSelectionContext.Provider value={store}>{children}</AccountSelectionContext.Provider>;
}

// Null outside the authenticated app (and in isolated component tests): the
// caller then keeps a selection local to itself, as before.
export function useAccountSelectionStore(): MediaSelectionStore | null {
  return useContext(AccountSelectionContext);
}
