import { useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useLocation } from 'react-router';

// A PARTY ON THE HOME SCREEN.
//
// ONE PARTY, ONE APP, ITS OWN SCOPE. A party's app lives under its own
// canonical path, /party/app/<key>/ — the key a digest of the party, never a
// token — which is its id and its scope, so no installed party ever claims
// another's links (Android hands a link to the app whose scope contains it,
// and judges "already installed" by it). Its pages are the ordinary party
// pages under that path: the router runs with it as its basename, so every
// in-app navigation, invitation into party included, stays inside the scope.
//
// The links guests hold (/party/<token>, /party/invite/<token>) belong to no
// app's scope. Opened in a browser, such an ENTRY page moves to the same page
// under its party's canonical path (<PartyAppCanonical />), so the browser sees
// it inside the party's app: installable as that party, "already installed"
// once it is — and never as another party's.
//
// WHICH manifest a page has is decided from the ADDRESS ALONE: the bootstrap
// script in index.html CREATES the manifest and icon links with their final
// addresses (there is no static one to be read first), and <PartyAppHead />
// keeps them in step on navigation outside a party's app. Only the title waits
// for the party to load. Nothing is cached: the app opens the link, and the
// link asks the server for the party as it is now.
//
// Android's browsers offer to install a page whose manifest qualifies, through
// an event that comes once and early; it is caught here, at module load, so the
// button can use it whenever the guest asks. An iPhone has no such thing: the
// button there explains Share → Add to Home Screen.

export interface PartyAppHeadLinks {
  /** Null inside a party's app on a page that is not its entry: no manifest, never another app's. */
  manifestUrl: string | null;
  iconUrl: string;
}

/** The product's own app: every page that is not a party's. */
export const PRODUCT_APP = {
  manifestUrl: '/manifest.webmanifest',
  iconUrl: '/brand/nubarca-apple-touch-icon-180.png',
} as const;

const PARTY_APP_ROOT = /^\/party\/app\/[0-9a-f]{32}(?=\/|$)/;
const PARTY_APP_SCOPE = /^\/party\/app\/[0-9a-f]{32}\/$/;

/** The canonical root of the party app an address is in (/party/app/<key>), or undefined. */
export function partyAppBasename(pathname: string): string | undefined {
  return PARTY_APP_ROOT.exec(pathname)?.[0];
}

/** The API base of the party or invitation an entry path opens, or null. */
function partyEntryApi(path: string): string | null {
  const match = /^\/party\/(?:(invite)\/)?([A-Za-z0-9_-]+)\/?$/.exec(path);
  if (!match) return null;
  const [, invite, token] = match;
  if (!invite && (token === 'crew' || token === 'invite' || token === 'app')) return null;
  return `${invite ? '/api/party-invitations/' : '/api/party/'}${encodeURIComponent(token)}`;
}

/**
 * The links a page at `pathname` — the full address — gets. A party's own page
 * (/party/<token>) and a personal invitation (/party/invite/<token>), by their
 * link or under their app's canonical path: that party's app. Another page of
 * a party's app: no manifest. Anything else: the product's. KEEP IN STEP with
 * the party app bootstrap in index.html — partyAppBootstrap.test.tsx holds the
 * two to one table.
 */
export function partyAppForPath(pathname: string): PartyAppHeadLinks {
  const base = partyAppBasename(pathname);
  const api = partyEntryApi(base ? pathname.slice(base.length) || '/' : pathname);
  if (api) return { manifestUrl: `${api}/app-manifest`, iconUrl: `${api}/app-icon/192` };
  return base ? { manifestUrl: null, iconUrl: PRODUCT_APP.iconUrl } : PRODUCT_APP;
}

/**
 * Keeps the document's app in step with in-app navigation — from the address
 * alone. Inside a party's app (a basename) there is nothing to do: the whole
 * document is that party's app, its manifest chosen at bootstrap.
 */
export function PartyAppHead({ basename }: { basename?: string }): null {
  const { pathname } = useLocation();
  useEffect(() => {
    if (basename) return;
    const app = partyAppForPath(pathname);
    headLink('manifest', app.manifestUrl);
    headLink('apple-touch-icon', app.iconUrl);
  }, [basename, pathname]);
  return null;
}

/** The ONE link of this rel in the head, pointed at `href`; none when `href` is null. */
function headLink(rel: string, href: string | null): void {
  const existing = document.head.querySelector(`link[rel="${rel}"]`);
  if (href === null) {
    existing?.remove();
    return;
  }
  if (existing) {
    if (existing.getAttribute('href') !== href) existing.setAttribute('href', href);
    return;
  }
  const element = document.createElement('link');
  element.setAttribute('rel', rel);
  element.setAttribute('href', href);
  document.head.appendChild(element);
}

/** How an entry page moves to its app's path: a new document there. Tests watch it. */
export const partyAppNavigation = {
  replace: (url: string) => window.location.replace(url),
};

/**
 * An entry page opened by its own link — outside every app's scope — moves to
 * the same page under its party's canonical path, read from the manifest's
 * scope, before anything is drawn. Anything that stops that (no party behind
 * the link, the server not answering in time) leaves the page where it is.
 */
export function PartyAppCanonical({ basename, children }: { basename?: string; children: ReactNode }) {
  const entry = basename ? null : partyEntryApi(window.location.pathname);
  const [settled, setSettled] = useState(entry === null);
  useEffect(() => {
    if (settled || entry === null) return undefined;
    const controller = new AbortController();
    const giveUp = window.setTimeout(() => controller.abort(), 3000);
    fetch(`${entry}/app-manifest`, { signal: controller.signal, credentials: 'same-origin' })
      .then((response) => (response.ok ? response.json() : null))
      .then((manifest: { scope?: unknown } | null) => {
        const scope = manifest?.scope;
        if (typeof scope === 'string' && PARTY_APP_SCOPE.test(scope)) {
          const { pathname, search, hash } = window.location;
          partyAppNavigation.replace(`${scope}${pathname.slice(1)}${search}${hash}`);
          return;
        }
        setSettled(true);
      })
      .catch(() => setSettled(true))
      .finally(() => window.clearTimeout(giveUp));
    return () => {
      window.clearTimeout(giveUp);
      controller.abort();
    };
  }, [entry, settled]);
  return settled ? children : null;
}

/** The party's name as the page's and the home-screen app's title, once the party has loaded. */
export function usePartyHomeScreen(title: string | null): void {
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
