import { useEffect, useSyncExternalStore } from 'react';
import { useLocation } from 'react-router';

// A PARTY ON THE HOME SCREEN.
//
// A party's page is the party's own app: its manifest (named after the party,
// its cover as the icon, opening this very link) stands in for the product's,
// and the iPhone's own tags follow. WHICH app is decided from the ADDRESS
// ALONE — at bootstrap by the script in index.html, which CREATES the manifest
// and icon links with their final addresses (there is no static one to be
// read first), and on in-app navigation by <PartyAppHead /> with the same
// rules — never after a fetch. Only the title waits for the party to load. Nothing is cached: the
// app opens the link, and the link asks the server for the party as it is now.
//
// Android's browsers offer to install a page whose manifest qualifies, through
// an event that comes once and early; it is caught here, at module load, so the
// button can use it whenever the guest asks. An iPhone has no such thing: the
// button there explains Share → Add to Home Screen.

export interface PartyAppHeadLinks {
  manifestUrl: string;
  iconUrl: string;
}

/** The product's own app: every page that is not a party's. */
export const PRODUCT_APP: PartyAppHeadLinks = {
  manifestUrl: '/manifest.webmanifest',
  iconUrl: '/brand/nubarca-apple-touch-icon-180.png',
};

/**
 * The party app a page's address belongs to, or null. Only a party's own page
 * (/party/<token>) and a personal invitation (/party/invite/<token>): the
 * party's other pages carry other capabilities' tokens. KEEP IN STEP with the
 * party app bootstrap in index.html — partyAppBootstrap.test.ts holds the two
 * to one table.
 */
export function partyAppForPath(pathname: string): PartyAppHeadLinks | null {
  const match = /^\/party\/(?:(invite)\/)?([A-Za-z0-9_-]+)\/?$/.exec(pathname);
  if (!match) return null;
  const [, invite, token] = match;
  if (!invite && (token === 'crew' || token === 'invite')) return null;
  const base = invite ? '/api/party-invitations/' : '/api/party/';
  const enc = encodeURIComponent(token);
  return { manifestUrl: `${base}${enc}/app-manifest`, iconUrl: `${base}${enc}/app-icon/192` };
}

/** Keeps the document's app in step with in-app navigation — from the address alone. */
export function PartyAppHead(): null {
  const { pathname } = useLocation();
  useEffect(() => {
    const app = partyAppForPath(pathname) ?? PRODUCT_APP;
    headLink('manifest', app.manifestUrl);
    headLink('apple-touch-icon', app.iconUrl);
  }, [pathname]);
  return null;
}

/** The ONE link of this rel in the head, pointed at `href` — created only if the bootstrap's is missing. */
function headLink(rel: string, href: string): void {
  const existing = document.head.querySelector(`link[rel="${rel}"]`);
  if (existing) {
    if (existing.getAttribute('href') !== href) existing.setAttribute('href', href);
    return;
  }
  const element = document.createElement('link');
  element.setAttribute('rel', rel);
  element.setAttribute('href', href);
  document.head.appendChild(element);
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
