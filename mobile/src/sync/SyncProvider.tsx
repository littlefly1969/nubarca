// Sync provider: binds ONE SyncEngine to the CURRENT authenticated identity.
//
// The whole sync subtree is remounted per user id (same rule as the viewer):
// an account switch or sign-out disposes the old engine — active uploads are
// really aborted, listeners released, the per-account ledger connection is
// closed — so nothing of account A can ever execute under account B. Each
// identity opens its OWN SQLite ledger file (hard namespace isolation).

import React, {
  createContext,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { AppState } from 'react-native';
import { SyncEngine } from './syncEngine.ts';
import type { EngineSnapshot } from './syncTypes.ts';
import { openAccountLedgerConnection } from './ledgerStorage.ts';
import { SyncLedger } from './syncLedger.ts';
import { mediaLibraryPort } from './mediaLibraryAdapter.ts';
import { connectivityPort } from './connectivityAdapter.ts';
import { uploadAssetViaOwnerEndpoint } from './uploader.ts';
import { newOperationId } from './operationIdAdapter.ts';
// The live SESSION GENERATION comes from the one owner-session store; it is
// what makes stale completions from a previous login detectably stale.
import { ownerSession } from '../api/session';

interface SyncContextValue {
  engine: SyncEngine | null;
  snapshot: EngineSnapshot | null;
}

const SyncContext = createContext<SyncContextValue>({
  engine: null,
  snapshot: null,
});

export function useSync(): SyncContextValue {
  return useContext(SyncContext);
}

export function SyncProvider({
  accountId,
  children,
}: {
  accountId: string;
  children: React.ReactNode;
}): React.JSX.Element {
  // The engine lives exactly as long as this identity does. Refs keep the
  // imperative pieces out of render; state exposes snapshots to observers.
  const engineRef = useRef<SyncEngine | null>(null);
  const [snapshot, setSnapshot] = useState<EngineSnapshot | null>(null);

  useEffect(() => {
    let disposed = false;
    const conn = openAccountLedgerConnection(accountId);
    if (disposed) {
      conn.close();
      return;
    }
    const ledger = new SyncLedger(conn, accountId);
    const engine = new SyncEngine({
      ledger,
      mediaLibrary: mediaLibraryPort,
      connectivity: connectivityPort,
      uploader: uploadAssetViaOwnerEndpoint,
      identity: () => ({
        accountId,
        generation: ownerSession.snapshot().generation,
      }),
      now: () => Date.now(),
      newOperationId,
    });
    const unsubscribe = engine.subscribe(setSnapshot);
    engineRef.current = engine;
    engine.attach();

    // Network changes wake a Waiting-for-Wi-Fi engine without polling.
    const unsubscribeNetwork = connectivityPort.onNetworkChange?.(() => {
      engine.notifyNetworkChanged();
    });

    // Foreground recovery is the honest guarantee: on 'active', re-check
    // permission + policy and continue. Backgrounding needs no action —
    // uploads may continue briefly at the OS's discretion, and foreground
    // recovery always converges.
    const appStateSubscription = AppState.addEventListener('change', (state) => {
      if (state === 'active') engine.resumeForeground();
    });

    return () => {
      disposed = true;
      appStateSubscription.remove();
      unsubscribeNetwork?.();
      unsubscribe();
      engine.detach();
      engineRef.current = null;
      setSnapshot(null);
      conn.close();
    };
  }, [accountId]);

  const value = useMemo<SyncContextValue>(
    () => ({ engine: engineRef.current, snapshot }),
    [snapshot],
  );

  return <SyncContext.Provider value={value}>{children}</SyncContext.Provider>;
}
