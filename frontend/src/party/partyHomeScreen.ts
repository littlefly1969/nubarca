import { useEffect, useSyncExternalStore } from 'react';

// A PARTY ON THE HOME SCREEN.
//
// The page in front of the guest declares itself as the app of its own link:
// the party's manifest (named after the party, its cover as the icon, opening
// this very link) replaces the product's, and the iPhone's own tags follow.
// Leaving the page puts the product's back. Nothing is cached: the app opens
// the link, and the link asks the server for the party as it is now.
//
// Android's browsers offer to install a page whose manifest qualifies, through
// an event that comes once and early; it is caught here, at module load, so the
// button can use it whenever the guest asks. An iPhone has no such thing: the
// button there explains Share → Add to Home Screen.

/** The party's app, as the page in front of the guest declares it. */
export interface PartyHomeScreenApp {
  manifestUrl: string;
  iconUrl: string;
  title: string;
}

export function partyAppFor(token: string, title: string): PartyHomeScreenApp {
  const enc = encodeURIComponent(token);
  return {
    manifestUrl: `/api/party/${enc}/app-manifest`,
    iconUrl: `/api/party/${enc}/app-icon/192`,
    title,
  };
}

export function invitationAppFor(token: string, title: string): PartyHomeScreenApp {
  const enc = encodeURIComponent(token);
  return {
    manifestUrl: `/api/party-invitations/${enc}/app-manifest`,
    iconUrl: `/api/party-invitations/${enc}/app-icon/192`,
    title,
  };
}

/** The head of the document while `app` is the page: restored as it was afterwards. */
export function usePartyHomeScreen(app: PartyHomeScreenApp | null): void {
  const manifestUrl = app?.manifestUrl;
  const iconUrl = app?.iconUrl;
  const title = app?.title;
  useEffect(() => {
    if (!manifestUrl || !iconUrl || !title) return undefined;
    const restore = [
      setHead('link[rel="manifest"]', 'href', manifestUrl),
      setHead('link[rel="apple-touch-icon"]', 'href', iconUrl),
      setHead('meta[name="apple-mobile-web-app-title"]', 'content', title),
    ];
    const previousTitle = document.title;
    document.title = title;
    return () => {
      document.title = previousTitle;
      restore.forEach((undo) => undo());
    };
  }, [manifestUrl, iconUrl, title]);
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
