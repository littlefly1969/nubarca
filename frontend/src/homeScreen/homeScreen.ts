import { createContext, createElement, useContext, useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useLocation } from 'react-router';

// A PUBLIC PAGE ON THE HOME SCREEN: a party, or an album shared by link.
//
// ONE THING, ONE APP, ITS OWN SCOPE. Each one's app lives under its own
// canonical path — /party/app/<key>/ or /album/app/<key>/, the key a digest of
// the party or the album, never a token — which is its id and its scope, so no
// installed app ever claims another's links (Android hands a link to the app
// whose scope contains it, and judges "already installed" by it). Its pages are
// the ordinary pages under that path: the router runs with it as its basename,
// so every in-app navigation stays inside the scope. A party's page is
// /party/app/<key>/party/<token>; an album's is /album/app/<key>/open/<token>.
//
// The links visitors hold (/party/<token>, /party/invite/<token>,
// /album/<token>) belong to no app's scope. Opened in a browser, such an ENTRY
// page moves to the same page under its app's canonical path
// (<HomeScreenAppCanonical />), so the browser sees it inside that app:
// installable as it, "already installed" once it is — never as another's. An
// album's app also checks that its key is the album its token opens.
//
// WHICH manifest a page has is decided from the ADDRESS ALONE: the bootstrap
// script in index.html CREATES the manifest and icon links with their final
// addresses (there is no static one to be read first), and <HomeScreenAppHead />
// keeps them in step on navigation outside an app. An album's manifest is asked
// for WITH the visitor's cookie: a protected album answers only the device
// that verified. Only the title waits for the page to load. Nothing is cached:
// the app opens the link, and the link asks the server for it as it is now.
//
// Android's browsers offer to install a page whose manifest qualifies, through
// an event that comes once and early; it is caught here, at module load, so the
// button can use it whenever the visitor asks. An iPhone has no such thing: the
// button there explains Share → Add to Home Screen.

export interface HomeScreenAppLinks {
  /** Null inside an app on a page that is not its entry: no manifest, never another app's. */
  manifestUrl: string | null;
  /** The manifest is asked for with the visitor's cookie (crossorigin="use-credentials"). */
  manifestCredentials: boolean;
  iconUrl: string;
}

/** The product's own app: every page that is not a party's or a shared album's. */
export const PRODUCT_APP: HomeScreenAppLinks = {
  manifestUrl: '/manifest.webmanifest',
  manifestCredentials: false,
  iconUrl: '/brand/nubarca-apple-touch-icon-180.png',
};

type AppArea = 'party' | 'album';

const APP_ROOT = /^\/(?:party|album)\/app\/[0-9a-f]{32}(?=\/|$)/;
const APP_SCOPE: Record<AppArea, RegExp> = {
  party: /^\/party\/app\/[0-9a-f]{32}\/$/,
  album: /^\/album\/app\/[0-9a-f]{32}\/$/,
};

/** The canonical root of the app an address is in (/party/app/<key>, /album/app/<key>), or undefined. */
export function homeScreenAppBasename(pathname: string): string | undefined {
  return APP_ROOT.exec(pathname)?.[0];
}

interface Entry { area: AppArea; api: string; token: string }

/** A party's own page or a personal invitation, by its link or inside a party's app. */
function partyEntry(path: string): Entry | null {
  const match = /^\/party\/(?:(invite)\/)?([A-Za-z0-9_-]+)\/?$/.exec(path);
  if (!match) return null;
  const [, invite, token] = match;
  if (!invite && (token === 'crew' || token === 'invite' || token === 'app')) return null;
  return { area: 'party', token, api: `${invite ? '/api/party-invitations/' : '/api/party/'}${encodeURIComponent(token)}` };
}

const albumEntry = (token: string): Entry =>
  ({ area: 'album', token, api: `/api/album-share/${encodeURIComponent(token)}` });

/** An album's link: /album/<token>. */
function albumLinkEntry(path: string): Entry | null {
  const match = /^\/album\/([A-Za-z0-9_-]+)\/?$/.exec(path);
  return match && match[1] !== 'app' ? albumEntry(match[1]) : null;
}

/** An album's page inside its app, the part after the app's root: /open/<token>. */
function albumAppEntry(rest: string): Entry | null {
  const match = /^\/open\/([A-Za-z0-9_-]+)\/?$/.exec(rest);
  return match && match[1] !== 'app' ? albumEntry(match[1]) : null;
}

/** The entry a full address opens, and the app root it is under, if any. */
function entryAt(pathname: string): { base?: string; entry: Entry | null } {
  const base = homeScreenAppBasename(pathname);
  if (!base) return { entry: partyEntry(pathname) ?? albumLinkEntry(pathname) };
  const rest = pathname.slice(base.length) || '/';
  return { base, entry: base.startsWith('/album/') ? albumAppEntry(rest) : partyEntry(rest) };
}

/**
 * The links a page at `pathname` — the full address — gets. A party's own page,
 * a personal invitation and a shared album, by their link or under their
 * app's canonical path: that app. Another page of an app: no manifest.
 * Anything else: the product's. KEEP IN STEP with the home-screen app
 * bootstrap in index.html — homeScreenBootstrap.test.tsx holds the two to one
 * table.
 */
export function homeScreenAppForPath(pathname: string): HomeScreenAppLinks {
  const { base, entry } = entryAt(pathname);
  if (entry) {
    return {
      manifestUrl: `${entry.api}/app-manifest`,
      manifestCredentials: entry.area === 'album',
      iconUrl: `${entry.api}/app-icon/192`,
    };
  }
  return base ? { manifestUrl: null, manifestCredentials: false, iconUrl: PRODUCT_APP.iconUrl } : PRODUCT_APP;
}

/**
 * Keeps the document's app in step with in-app navigation — from the address
 * alone. Inside an app (a basename) there is nothing to do: the whole document
 * is that app, its manifest chosen at bootstrap.
 */
export function HomeScreenAppHead({ basename }: { basename?: string }): null {
  const { pathname } = useLocation();
  useEffect(() => {
    if (basename) return;
    const app = homeScreenAppForPath(pathname);
    headLink('manifest', app.manifestUrl, app.manifestCredentials ? 'use-credentials' : null);
    headLink('apple-touch-icon', app.iconUrl, null);
  }, [basename, pathname]);
  return null;
}

/** The ONE link of this rel in the head, pointed at `href`; none when `href` is null. */
function headLink(rel: string, href: string | null, crossOrigin: string | null): void {
  const existing = document.head.querySelector(`link[rel="${rel}"]`);
  if (href === null) {
    existing?.remove();
    return;
  }
  const element = existing ?? document.createElement('link');
  if (element.getAttribute('crossorigin') !== crossOrigin) {
    if (crossOrigin === null) element.removeAttribute('crossorigin');
    else element.setAttribute('crossorigin', crossOrigin);
  }
  if (element.getAttribute('href') !== href) element.setAttribute('href', href);
  if (!existing) {
    element.setAttribute('rel', rel);
    document.head.appendChild(element);
  }
}

/** How a page moves: a new document there. Tests watch it. */
export const homeScreenAppNavigation = {
  replace: (url: string) => window.location.replace(url),
  reload: () => window.location.reload(),
};

const AppMismatch = createContext(false);

/**
 * True on an album's app page whose key is not the album its token opens — a
 * made-up address. The page then shows the album as unavailable: it never
 * shows one album inside another's app, and never moves to the right one.
 */
export function useHomeScreenAppMismatch(): boolean {
  return useContext(AppMismatch);
}

/**
 * Before anything is drawn:
 *  - an entry page opened by its own link — outside every app's scope — moves
 *    to the same page under its app's canonical path, read from the manifest's
 *    scope;
 *  - an album's page inside an app checks that the app is the album's.
 * Anything that stops that (nothing behind the link, a protected album not yet
 * verified, the server not answering in time) leaves the page to say so itself.
 */
export function HomeScreenAppCanonical({ children }: { children: ReactNode }) {
  const [state, setState] = useState<'checking' | 'ready' | 'mismatch'>(() => {
    const { base, entry } = entryAt(window.location.pathname);
    if (!entry) return 'ready';
    // Inside a party's app there is nothing to check; inside an album's, its key.
    return base && entry.area === 'party' ? 'ready' : 'checking';
  });
  useEffect(() => {
    if (state !== 'checking') return undefined;
    const { pathname, search, hash } = window.location;
    const { base, entry } = entryAt(pathname);
    if (!entry) return undefined;
    const controller = new AbortController();
    const giveUp = window.setTimeout(() => controller.abort(), 3000);
    fetch(`${entry.api}/app-manifest`, { signal: controller.signal, credentials: 'same-origin' })
      .then((response) => (response.ok ? response.json() : null))
      .then((manifest: { id?: unknown; scope?: unknown } | null) => {
        if (base) {
          setState(manifest && manifest.id !== base ? 'mismatch' : 'ready');
          return;
        }
        const scope = manifest?.scope;
        if (typeof scope === 'string' && APP_SCOPE[entry.area].test(scope)) {
          homeScreenAppNavigation.replace(entry.area === 'album'
            ? `${scope}open/${entry.token}${search}${hash}`
            : `${scope}${pathname.slice(1)}${search}${hash}`);
          return;
        }
        setState('ready');
      })
      .catch(() => setState('ready'))
      .finally(() => window.clearTimeout(giveUp));
    return () => {
      window.clearTimeout(giveUp);
      controller.abort();
    };
  }, [state]);
  if (state === 'checking') return null;
  return createElement(AppMismatch.Provider, { value: state === 'mismatch' }, children);
}

/**
 * After a protected album's device has been verified the page starts again, so
 * its app is resolved WITH the grant: a link moves under its app, an app checks
 * its key. False — nothing done — anywhere but an album's page.
 */
export function restartAlbumShareApp(): boolean {
  const { entry } = entryAt(window.location.pathname);
  if (entry?.area !== 'album') return false;
  homeScreenAppNavigation.reload();
  return true;
}

/** The page's name as the document's and the home-screen app's title, once it has loaded. */
export function useHomeScreenTitle(title: string | null): void {
  useEffect(() => {
    if (!title) return undefined;
    const restore = setHead('meta[name="apple-mobile-web-app-title"]', 'content', title);
    const previousTitle = document.title;
    document.title = title;
    return () => {
      document.title = previousTitle;
      restore();
    };
  }, [title]);
}

function setHead(selector: string, attribute: string, value: string): () => void {
  const element = document.head.querySelector(selector);
  if (!element) return () => {};
  const previous = element.getAttribute(attribute);
  element.setAttribute(attribute, value);
  return () => {
    if (previous === null) element.removeAttribute(attribute);
    else element.setAttribute(attribute, previous);
  };
}

// --- installing -------------------------------------------------------------

interface InstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

let deferredPrompt: InstallPromptEvent | null = null;
let installed = false;
const listeners = new Set<() => void>();
const notify = () => listeners.forEach((listener) => listener());

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (event) => {
    // Kept for the guest's own tap rather than shown by the browser at once.
    event.preventDefault();
    deferredPrompt = event as InstallPromptEvent;
    notify();
  });
  window.addEventListener('appinstalled', () => {
    installed = true;
    deferredPrompt = null;
    notify();
  });
}

export type HomeScreenPlatform = 'ios' | 'android' | 'other';

export function homeScreenPlatform(
  userAgent = navigator.userAgent,
  maxTouchPoints = navigator.maxTouchPoints ?? 0,
): HomeScreenPlatform {
  if (/iPhone|iPad|iPod/i.test(userAgent)) return 'ios';
  // iPadOS asks for desktop sites and says it is a Mac — with a touch screen.
  if (/Macintosh/i.test(userAgent) && maxTouchPoints > 1) return 'ios';
  if (/Android/i.test(userAgent)) return 'android';
  return 'other';
}

/** Already opened from the home screen: there is nothing left to add. */
export function isStandalone(): boolean {
  return window.matchMedia?.('(display-mode: standalone)').matches === true
    || (navigator as Navigator & { standalone?: boolean }).standalone === true;
}

export interface HomeScreenState {
  /** Android offered to install this page: one tap does it. */
  canPrompt: boolean;
  /** Installed during this visit. */
  installed: boolean;
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

let snapshot: HomeScreenState = { canPrompt: false, installed: false };
function getSnapshot(): HomeScreenState {
  const canPrompt = deferredPrompt !== null;
  if (snapshot.canPrompt !== canPrompt || snapshot.installed !== installed) {
    snapshot = { canPrompt, installed };
  }
  return snapshot;
}

export function useHomeScreenState(): HomeScreenState {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

/** Shows Android's own install dialog; false when there is none to show. */
export async function promptInstall(): Promise<boolean> {
  const prompt = deferredPrompt;
  if (!prompt) return false;
  // The event can be used once.
  deferredPrompt = null;
  notify();
  await prompt.prompt();
  const choice = await prompt.userChoice;
  if (choice.outcome === 'accepted') {
    installed = true;
    notify();
  }
  return true;
}
