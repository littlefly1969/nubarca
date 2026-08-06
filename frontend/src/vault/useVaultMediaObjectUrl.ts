import { useEffect, useRef, useState } from 'react';
import {
  ApiError,
  fetchVaultPoster,
  fetchVaultPreview,
  fetchVaultThumbnail,
} from '@nubarca/api-client';

// Shared object-URL lifecycle for authenticated vault derived media (slice 4).
// The Vault token can only travel in the X-Vault-Token header, so an <img src>
// cannot fetch it directly. This hook fetches the bytes with the header, wraps
// them in an object URL, and — critically — REVOKES that URL and ABORTS any
// in-flight request whenever the file changes, the token changes, the variant
// changes, or the component unmounts (which is what Lock / expiry / navigation
// all trigger, since they unmount the grid). No persistent cache: the simplest
// correct thing, so no vault bytes can outlive the unlocked session.

export type VaultMediaVariant = 'thumbnail-small' | 'thumbnail-medium' | 'preview' | 'poster';

export type VaultMediaStatus = 'idle' | 'loading' | 'ready' | 'error';

function fetchFor(
  variant: VaultMediaVariant,
  token: string,
  fileId: string,
  signal: AbortSignal,
): Promise<Blob> {
  switch (variant) {
    case 'thumbnail-small':
      return fetchVaultThumbnail(token, fileId, 'small', signal);
    case 'thumbnail-medium':
      return fetchVaultThumbnail(token, fileId, 'medium', signal);
    case 'preview':
      return fetchVaultPreview(token, fileId, signal);
    case 'poster':
      return fetchVaultPoster(token, fileId, signal);
  }
}

export interface UseVaultMediaOptions {
  token: string;
  fileId: string;
  variant: VaultMediaVariant;
  // Lazy gate: while false, nothing is fetched (used by the grid's
  // IntersectionObserver so only near-viewport cards load).
  enabled?: boolean;
  // Called once when a fetch returns 401 (the Vault token expired). The page
  // uses this to tear down locally and return to the unlock form — never a
  // global session invalidation.
  onExpired?: () => void;
}

export interface VaultMediaResult {
  url: string | null;
  status: VaultMediaStatus;
}

export function useVaultMediaObjectUrl({
  token,
  fileId,
  variant,
  enabled = true,
  onExpired,
}: UseVaultMediaOptions): VaultMediaResult {
  const [url, setUrl] = useState<string | null>(null);
  const [status, setStatus] = useState<VaultMediaStatus>('idle');
  const onExpiredRef = useRef(onExpired);
  onExpiredRef.current = onExpired;

  useEffect(() => {
    if (!enabled) {
      setStatus('idle');
      setUrl(null);
      return;
    }

    let objectUrl: string | null = null;
    let cancelled = false;
    const controller = new AbortController();

    setStatus('loading');
    setUrl(null);

    fetchFor(variant, token, fileId, controller.signal)
      .then((blob) => {
        if (cancelled) {
          return;
        }
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
        setStatus('ready');
      })
      .catch((err: unknown) => {
        if (cancelled || controller.signal.aborted) {
          return;
        }
        if (err instanceof ApiError && err.status === 401) {
          onExpiredRef.current?.();
        }
        setStatus('error');
      });

    return () => {
      cancelled = true;
      controller.abort();
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [token, fileId, variant, enabled]);

  return { url, status };
}
