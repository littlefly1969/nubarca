import {
  CAST_SENDER_SCRIPT,
  type CastFrameworkLike,
  type ChromeCastLike,
} from './castSdkTypes';

// The ONE place that touches the global Google Cast SDK.
//
// Two rules make this module worth existing. First, the SDK's readiness callback
// (`window.__onGCastApiAvailable`) is read SYNCHRONOUSLY when the script
// finishes loading — install it after appending the tag and it is never called,
// which presents as a Cast button that silently does nothing. So the callback
// goes on the window BEFORE the script element does. Second, appending the
// script twice re-registers the framework and produces duplicate session events,
// so the load is memoised into a single promise that every caller shares.
//
// No React component reaches for `window.cast` on its own; they go through
// CastProvider, which goes through here.

export type CastSdkStatus =
  /** Nothing has been asked of the SDK yet. */
  | 'idle'
  /** The script is in flight. */
  | 'loading'
  /** The framework is present and usable. */
  | 'ready'
  /** This browser cannot be a Cast sender at all (no Chrome Cast bridge). */
  | 'unsupported'
  /** The script was reachable but failed, or reported itself unavailable. */
  | 'failed';

export interface CastSdk {
  framework: CastFrameworkLike;
  chrome: ChromeCastLike;
}

export type CastSdkLoad =
  | { status: 'ready'; sdk: CastSdk }
  | { status: 'unsupported' }
  | { status: 'failed' };

let pending: Promise<CastSdkLoad> | null = null;

/**
 * True when this browser could possibly act as a Cast sender.
 *
 * The Web Sender is a Chromium feature exposed through `chrome.cast`. Firefox
 * and Safari never gain it, and — the case people are most surprised by —
 * neither does Chrome on iOS or iPadOS, because every iOS browser is WebKit
 * underneath. Detecting the bridge is the only reliable test; user-agent
 * sniffing gets iOS Chrome wrong.
 */
export function browserSupportsCastSender(): boolean {
  if (typeof window === 'undefined' || typeof document === 'undefined') return false;
  // The SDK bootstraps itself onto `window.chrome`, which only Chromium exposes.
  // Already-loaded framework counts too (a second mount, or a test double).
  if (window.cast?.framework !== undefined) return true;
  return typeof window.chrome === 'object' && window.chrome !== null;
}

/**
 * A secure origin is a hard requirement of the Web Sender, not a preference:
 * Chrome refuses to expose Cast on an insecure page. `localhost` is treated as
 * secure by the platform — but a receiver on the network still cannot resolve
 * it, which is a separate problem the UI explains rather than hides.
 */
export function isSecureCastOrigin(): boolean {
  if (typeof window === 'undefined') return false;
  return window.isSecureContext === true;
}

/**
 * True when this page's origin is one a Cast receiver on the local network can
 * actually reach. A loopback host is secure enough for the SDK and useless to a
 * television, so the UI has to be able to say so.
 */
export function isReceiverReachableOrigin(): boolean {
  if (typeof window === 'undefined') return false;
  const host = window.location.hostname;
  return host !== 'localhost' && host !== '127.0.0.1' && host !== '[::1]' && host !== '::1';
}

/**
 * Loads the Cast Sender framework at most once per document. Repeat calls share
 * the first call's promise, so mounting the provider twice (StrictMode, a route
 * remount) cannot append a second script tag.
 */
export function loadGoogleCastSdk(): Promise<CastSdkLoad> {
  if (pending !== null) return pending;

  pending = new Promise<CastSdkLoad>((resolve) => {
    if (!browserSupportsCastSender()) {
      resolve({ status: 'unsupported' });
      return;
    }

    const resolveWithGlobals = (): boolean => {
      const framework = window.cast?.framework;
      const chrome = window.chrome;
      if (framework === undefined || chrome?.cast === undefined) return false;
      resolve({ status: 'ready', sdk: { framework, chrome } });
      return true;
    };

    // Already present (a warm navigation, or a test double installed up front).
    if (resolveWithGlobals()) return;

    // MUST be installed before the script element is appended.
    window.__onGCastApiAvailable = (available: boolean) => {
      if (!available || !resolveWithGlobals()) {
        resolve({ status: 'failed' });
      }
    };

    const existing = document.querySelector<HTMLScriptElement>(
      `script[src="${CAST_SENDER_SCRIPT}"]`,
    );
    if (existing !== null) {
      // Somebody already appended it; the callback above will fire, or has.
      return;
    }

    const script = document.createElement('script');
    script.src = CAST_SENDER_SCRIPT;
    script.async = true;
    script.onerror = () => resolve({ status: 'failed' });
    document.head.appendChild(script);
  });

  return pending;
}

/** Test seam: forget the memoised load so a fresh document starts clean. */
export function resetGoogleCastSdkForTests(): void {
  pending = null;
  if (typeof window !== 'undefined') {
    delete window.__onGCastApiAvailable;
  }
}
