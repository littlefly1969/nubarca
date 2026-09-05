import {
  useCallback, useEffect, useRef, useState,
  type KeyboardEvent as ReactKeyboardEvent, type ReactNode,
} from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  deletePartyFaceSearch,
  getPartyAlbum,
  getPartyItems,
  type PartyItem,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyFaceSearch, type PartyFaceFilter } from '../components/PartyFaceSearch';
import { PartyGuestDock } from '../components/PartyGuestDock';
import { withContributionMode } from './partyContributionMode';
import { PRODUCT_NAME } from '../brand/brand';
import { rememberFaceFilter, rememberPartyHome } from './partyGuestMemo';
import './PartyGuestHub.css';

// PUBLIC, unauthenticated party album landing. Reached by scanning the QR shown
// on a paired TV (or opening the shared link). View-only: it renders party-safe
// DERIVED media (metadata-stripped thumbnails/previews) and offers a download of
// the same safe medium copy for photos. No login, no upload, no owner identity,
// no metadata, no face/person data — the backend guarantees all of that; this
// page simply shows what the token-scoped API returns. When party mode is
// disabled/revoked the API returns 404 and we show a friendly "unavailable".
type State =
  | { kind: 'loading' }
  | { kind: 'ready'; albumName: string; items: PartyItem[]; coverUrl: string | null;
      contributionUrl: string | null; gameEnabled: boolean; printUrl: string | null }
  | { kind: 'unavailable' }
  | { kind: 'error' };

// Live-refresh interval for the public party view so guest uploads appear
// without a manual reload. Same 10-20s band as the TV surfaces; each poll
// re-checks the token server-side (revoked/disabled/expired → 404).
const PARTY_POLL_MS = 15_000;

// The guest hub is a FIXED dark surface — a party cover, not a themed app page —
// so the approved ON-DARK wordmark is pinned here instead of resolved from the
// visitor's theme (which is what <BrandMark> does, and would put the Midnight
// Navy artwork on a Midnight Navy hero). Byte-exact approved asset, rendered at
// its own proportions, unfiltered and unrecoloured; CSS only sets its width.
const PARTY_WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

// Brand names are not translated: the eyebrow is the product name, and the
// stylesheet is what renders it in caps.
const PARTY_EYEBROW = `${PRODUCT_NAME} Party`;

function CameraIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M3.5 8.75A2.25 2.25 0 0 1 5.75 6.5h1.6l1.1-1.85A1.5 1.5 0 0 1 9.74 4h4.52a1.5 1.5 0 0 1 1.29.73l1.1 1.87h1.6a2.25 2.25 0 0 1 2.25 2.25v8A2.25 2.25 0 0 1 18.25 19H5.75a2.25 2.25 0 0 1-2.25-2.25Z" />
      <circle cx="12" cy="12.5" r="3.4" />
    </svg>
  );
}

function ChevronIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="m9.5 5.5 6.5 6.5-6.5 6.5" />
    </svg>
  );
}

// Deck iconography: one stroke language for the whole surface — same viewBox,
// same weight, no fills, no emoji, no icon library. Decorative: the card's own
// title is what a screen reader announces.
function FaceFrameIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M4 8.5V6a2 2 0 0 1 2-2h2.5M15.5 4H18a2 2 0 0 1 2 2v2.5M20 15.5V18a2 2 0 0 1-2 2h-2.5M8.5 20H6a2 2 0 0 1-2-2v-2.5" />
      <circle cx="12" cy="10.6" r="2.6" />
      <path d="M8.2 16.4a4.2 4.2 0 0 1 7.6 0" />
    </svg>
  );
}

function TrophyIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M7.5 4h9v5a4.5 4.5 0 0 1-9 0Z" />
      <path d="M7.5 5.5H5.2a.7.7 0 0 0-.7.8c.2 2 1.4 3.4 3 3.6M16.5 5.5h2.3a.7.7 0 0 1 .7.8c-.2 2-1.4 3.4-3 3.6" />
      <path d="M12 13.5V17M9 20h6M10 17h4" />
    </svg>
  );
}

function PrinterIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M7 8.5V4.5h10v4" />
      <path d="M7 17.5H5.5A1.5 1.5 0 0 1 4 16v-4.5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2V16a1.5 1.5 0 0 1-1.5 1.5H17" />
      <rect x="7" y="14" width="10" height="6" rx="1.2" />
      <path d="M16.8 12.2h.01" />
    </svg>
  );
}

function PhotoStackIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect x="7.5" y="3.5" width="13" height="13" rx="2.5" />
      <path d="m9.5 13.2 3.1-3.1a1.4 1.4 0 0 1 2 0l3.4 3.4" />
      <circle cx="16.3" cy="7.6" r="1.3" />
      <path d="M16.5 20.5H6a2.5 2.5 0 0 1-2.5-2.5V7.5" />
    </svg>
  );
}

/* Gallery composition.

   An editorial 2-column grid rather than a uniform contact sheet, but a
   DETERMINISTIC one: a tile's shape comes from its index, never from the
   image's real dimensions, so nothing reflows once the photos load and the
   visual order is exactly the DOM order (no masonry, no `columns`, no dense
   packing).

   Two rules keep the composition whole at any album size:

     * a wide tile every FEATURE_EVERY items, which always starts a fresh row
       because the six tiles between two of them fill exactly three rows —
       so a wide tile can never leave a gap beside it;
     * the two tiles sharing a row always share a shape, so a row never has one
       short tile and one tall one with dead space under the short one.

   The one thing that does depend on the total is the LAST tile: if it would sit
   alone it widens to fill its row. That is a single tile at the very end, so a
   photo arriving from the poll changes that row and nothing above it. */
export type GalleryShape = 'featured' | 'portrait' | 'square';

const FEATURE_EVERY = 7;

export function galleryShapes(count: number): GalleryShape[] {
  const shapes: GalleryShape[] = [];
  let row = 0;
  let col = 0;
  let cells = 0;
  for (let i = 0; i < count; i += 1) {
    if (i % FEATURE_EVERY === 0) {
      shapes.push('featured');
      cells += 2;
      row += 1;
      col = 0;
      continue;
    }
    shapes.push(row % 2 === 0 ? 'portrait' : 'square');
    cells += 1;
    col += 1;
    if (col === 2) {
      col = 0;
      row += 1;
    }
  }
  // An odd number of cells means the last row holds one tile: widen it.
  if (cells % 2 === 1 && shapes.length > 0) shapes[shapes.length - 1] = 'featured';
  return shapes;
}

function HeartIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M12 19.6C7.9 16.9 4.5 14.2 4.5 10.6A3.9 3.9 0 0 1 12 8.6a3.9 3.9 0 0 1 7.5 2c0 3.6-3.4 6.3-7.5 9Z" />
    </svg>
  );
}

function PlayIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M9.5 7.8 16.8 12l-7.3 4.2Z" />
    </svg>
  );
}

/* One capability = one card in the "what would you like to do?" deck.
 *
 * THE RULE, and it is the whole point of this file's shape:
 *
 *     a capability is rendered only when it is IMPLEMENTED and ENABLED
 *     for THIS party — `available` says so, once, next to the definition.
 *
 * So there is no `{gameEnabled && …}` scattered through the JSX, and no
 * placeholder, "coming soon", or disabled card anywhere: a capability the
 * product does not offer, or that this party has switched off, has no entry in
 * the rendered list at all.
 *
 * ADDING ONE LATER. Dedications, song requests and printing are planned. Each
 * becomes one entry here plus the REAL signal that says it is on for this
 * party — a field the backend adds when the feature ships, never a boolean
 * invented in the frontend to hold its place. And each needs the pair of tests
 * that keeps this rule honest:
 *
 *     enabled  → the card is present
 *     disabled → the card is absent
 *
 * This is a list, deliberately: three cards do not need a registry service, a
 * plugin system or a schema.
 */
type CapabilityTarget =
  | { kind: 'anchor'; href: string }   // somewhere on this page
  | { kind: 'route'; to: string }      // a real party route
  | { kind: 'action'; onSelect: () => void };  // opens something in place

/**
 * How loudly a capability speaks, stated per entry rather than derived from its
 * position: 'signature' leads the deck, 'activity' is something to take part in,
 * 'utility' recedes. A future capability declares its own tier — nothing is
 * promoted to signature just for arriving first.
 */
type CapabilityVariant = 'signature' | 'activity' | 'utility';

interface Capability {
  id: string;
  titleKey: MessageKey;
  descriptionKey: MessageKey;
  icon: ReactNode;
  target: CapabilityTarget;
  variant: CapabilityVariant;
  /** Implemented AND on for this party. Nothing else gets rendered. */
  available: boolean;
  badgeKey?: MessageKey;
}

// The capability deck. Every card is a real destination, so every card is a
// real link — an anchor for somewhere on this page, a router Link for a route.
function CapabilityDeck({ capabilities }: { capabilities: Capability[] }) {
  const { t } = useI18n();
  return (
    <nav className="party-guest-hub-deck" aria-labelledby="party-deck-title">
      <h2 className="party-guest-hub-deck-title" id="party-deck-title">
        {t('partyHub.actions')}
      </h2>
      <ul className="party-guest-hub-deck-grid">
        {capabilities.map((cap) => {
          const body = (
            <>
              <span className="party-guest-hub-capability-icon" aria-hidden="true">
                {cap.icon}
              </span>
              <span className="party-guest-hub-capability-text">
                <strong className="party-guest-hub-capability-title">{t(cap.titleKey)}</strong>
                <span className="party-guest-hub-capability-desc">{t(cap.descriptionKey)}</span>
              </span>
              {cap.badgeKey && (
                <span className="party-guest-hub-capability-badge">{t(cap.badgeKey)}</span>
              )}
            </>
          );
          return (
            <li
              key={cap.id}
              className="party-guest-hub-capability"
              data-variant={cap.variant}
              data-testid={`party-capability-${cap.id}`}
            >
              {cap.target.kind === 'route' && (
                <Link className="party-guest-hub-capability-link" to={cap.target.to}>{body}</Link>
              )}
              {cap.target.kind === 'anchor' && (
                <a className="party-guest-hub-capability-link" href={cap.target.href}>{body}</a>
              )}
              {cap.target.kind === 'action' && (
                <button
                  type="button"
                  className="party-guest-hub-capability-link"
                  onClick={cap.target.onSelect}
                >
                  {body}
                </button>
              )}
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

// Wordmark + language switcher: the same top row on the hero and on the
// unavailable/error states, so a guest always knows where they are.
function PartyHubTopBar() {
  return (
    <div className="party-guest-hub-topbar">
      <img
        className="party-guest-hub-logo"
        src={PARTY_WORDMARK.src}
        alt={PRODUCT_NAME}
        width={PARTY_WORDMARK.width}
        height={PARTY_WORDMARK.height}
      />
      <LanguageSwitcher className="language-switcher language-switcher-public" compact />
    </div>
  );
}

function sameItemIds(a: PartyItem[], b: PartyItem[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].id !== b[i].id) return false;
  }
  return true;
}

export function PartyPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [lightbox, setLightbox] = useState<PartyItem | null>(null);
  // The tile the viewer was opened from, so focus goes back to it on close.
  const viewerOpenerRef = useRef<HTMLElement | null>(null);
  const viewerRef = useRef<HTMLDivElement>(null);
  // Phone-only face filter from a completed face search. The full album stays
  // in state (polling continues untouched); only the visible grid is narrowed.
  // The TV is NEVER affected by this — activation is a separate explicit action
  // inside PartyFaceSearch.
  const [faceFilter, setFaceFilter] = useState<PartyFaceFilter | null>(null);
  // The face-search sheet is opened by its capability card and by nothing else.
  const [faceOpen, setFaceOpen] = useState(false);
  // The print studio opens on its own token and cannot see this state, so the
  // guest's search result is left where that page can pick it up. Forgotten
  // again the moment the filter is cleared.
  useEffect(() => {
    rememberFaceFilter(faceFilter ? faceFilter.itemIds : null);
  }, [faceFilter]);
  // The studio holds a print token and cannot address the album, so the hub
  // leaves its own path behind for the back link there.
  useEffect(() => {
    if (token) rememberPartyHome(`/party/${token}`);
  }, [token]);
  const galleryRef = useRef<HTMLElement>(null);
  const heroRef = useRef<HTMLElement>(null);
  // The dock appears once the cover is behind the guest, and says which of the
  // two places they are in. Both come from IntersectionObserver rather than a
  // scroll listener: the browser reports the crossings it already computes,
  // instead of this page recomputing geometry on every frame of a scroll.
  const [heroOnScreen, setHeroOnScreen] = useState(true);
  const [galleryOnScreen, setGalleryOnScreen] = useState(false);

  useEffect(() => {
    const hero = heroRef.current;
    const gallery = galleryRef.current;
    if (!hero && !gallery) return;
    const observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (entry.target === hero) setHeroOnScreen(entry.isIntersecting);
        if (entry.target === gallery) setGalleryOnScreen(entry.isIntersecting);
      }
    });
    if (hero) observer.observe(hero);
    if (gallery) observer.observe(gallery);
    return () => observer.disconnect();
  }, [state.kind]);

  const scrollTo = useCallback((el: HTMLElement | null) => {
    if (!el) return;
    const still = typeof window.matchMedia === 'function'
      && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView?.({ block: 'start', behavior: still ? 'auto' : 'smooth' });
  }, []);

  // Discarding a search belongs to the page, which owns the filter: it drops the
  // local filter and the short-lived server-side search (and its stored face
  // crop) together, from the sheet or from the banner over the gallery.
  const cancelFaceSearch = useCallback((searchId: string | null) => {
    if (token && searchId) {
      void deletePartyFaceSearch(token, searchId).catch(() => { /* best effort */ });
    }
    setFaceFilter(null);
  }, [token]);

  const showFaceResults = useCallback(() => {
    scrollTo(galleryRef.current);
  }, [scrollTo]);

  // Which moments the guest has already been shown. Items arrive appended
  // (the server orders by AddedAt), so a new one lands at the BOTTOM of the
  // gallery, where nobody standing at the top would notice it — hence the
  // count. Tracked by id rather than by length: an owner hiding one photo while
  // a guest uploads another leaves the length unchanged, and that is still a new
  // moment.
  const seenIdsRef = useRef<Set<string> | null>(null);
  const [newMoments, setNewMoments] = useState(0);

  const openViewer = useCallback((item: PartyItem, trigger: HTMLElement) => {
    viewerOpenerRef.current = trigger;
    setLightbox(item);
  }, []);

  // `aria-modal` claims the rest of the page is inert, so Tab must not walk out
  // of the viewer into it. The surface holds at most two controls — close, and
  // download for a photo — so the trap is this, rather than a reason to move a
  // full-bleed photo viewer onto a primitive built around a titled header.
  // A video has no download, so Tab simply keeps close focused.
  const trapViewerFocus = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'Tab') return;
    const focusable = Array.from(
      viewerRef.current?.querySelectorAll<HTMLElement>('button, a[href]') ?? [],
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === first || !viewerRef.current?.contains(active))) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  }, []);

  const load = useCallback((signal?: AbortSignal) => {
    if (!token) {
      setState({ kind: 'unavailable' });
      return;
    }
    setState({ kind: 'loading' });
    Promise.all([getPartyAlbum(token, signal), getPartyItems(token, signal)])
      .then(([album, items]) => {
        setState({
          kind: 'ready', albumName: album.albumName, items: items.items,
          coverUrl: album.coverUrl, contributionUrl: album.contributionUrl,
          gameEnabled: album.gameEnabled, printUrl: album.printUrl,
        });
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 404) {
          setState({ kind: 'unavailable' });
          return;
        }
        setState({ kind: 'error' });
      });
  }, [token]);

  useEffect(() => {
    const ctrl = new AbortController();
    load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  // Live refresh: once the album is showing, poll items so newly uploaded photos
  // appear on the public landing page automatically. Adopts the server list
  // (stable append order); a revoked/disabled token → 404 → "unavailable"; an
  // open lightbox whose photo vanished is closed. Transient errors keep the
  // current view.
  useEffect(() => {
    if (state.kind !== 'ready' || !token) return;
    const timer = window.setInterval(() => {
      getPartyItems(token)
        .then((fresh) => {
          setState((cur) => (cur.kind === 'ready' && !sameItemIds(cur.items, fresh.items)
            ? { ...cur, albumName: fresh.albumName, items: fresh.items }
            : cur));
          setLightbox((lb) => (lb && !fresh.items.some((it) => it.id === lb.id) ? null : lb));
        })
        .catch((err: unknown) => {
          if (err instanceof ApiError && err.status === 404) setState({ kind: 'unavailable' });
          // transient error: keep showing what we have
        });
    }, PARTY_POLL_MS);
    return () => window.clearInterval(timer);
  }, [state.kind, token]);

  const readyItems = state.kind === 'ready' ? state.items : null;
  useEffect(() => {
    if (!readyItems) return;
    // First sight of the album: everything in it is already "seen".
    if (!seenIdsRef.current) {
      seenIdsRef.current = new Set(readyItems.map((i) => i.id));
      return;
    }
    const seen = seenIdsRef.current;
    setNewMoments(readyItems.reduce((n, i) => (seen.has(i.id) ? n : n + 1), 0));
  }, [readyItems]);

  const acknowledgeMoments = useCallback(() => {
    if (readyItems) seenIdsRef.current = new Set(readyItems.map((i) => i.id));
    setNewMoments(0);
  }, [readyItems]);

  // The viewer owns the screen while it is open: Escape closes it, the page
  // behind it does not scroll, and focus goes back to the tile it came from.
  useEffect(() => {
    if (!lightbox) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setLightbox(null); };
    window.addEventListener('keydown', onKey);
    const body = document.body;
    const previousOverflow = body.style.overflow;
    body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      body.style.overflow = previousOverflow;
      const opener = viewerOpenerRef.current;
      viewerOpenerRef.current = null;
      opener?.focus?.();
    };
  }, [lightbox]);

  if (state.kind === 'loading') {
    // Shaped like the finished hero — brand bar, title lines, CTA — so the real
    // content lands where the skeleton already reserved room.
    return (
      <main className="party-guest-hub" aria-busy="true">
        <span className="visually-hidden">{t('common.loading')}</span>
        <div className="party-guest-hub-skeleton-hero" data-testid="party-hub-skeleton">
          <div className="party-guest-hub-skeleton-top">
            <div className="party-guest-hub-shape party-guest-hub-shape-logo" />
            <div className="party-guest-hub-shape party-guest-hub-shape-lang" />
          </div>
          <div className="party-guest-hub-skeleton-lines">
            <div className="party-guest-hub-shape party-guest-hub-shape-eyebrow" />
            <div className="party-guest-hub-shape party-guest-hub-shape-title" />
            <div className="party-guest-hub-shape party-guest-hub-shape-meta" />
            <div className="party-guest-hub-shape party-guest-hub-shape-cta" />
          </div>
        </div>
      </main>
    );
  }
  if (state.kind === 'unavailable') {
    return (
      <main className="party-guest-hub">
        <div className="party-guest-hub-state-page">
          <PartyHubTopBar />
          <div className="party-guest-hub-state">
            <h1>{t('party.unavailableTitle')}</h1>
            <p role="alert">{t('party.unavailableBody')}</p>
          </div>
        </div>
      </main>
    );
  }
  if (state.kind === 'error') {
    return (
      <main className="party-guest-hub">
        <div className="party-guest-hub-state-page">
          <PartyHubTopBar />
          <div className="party-guest-hub-state">
            <h1>{t('party.errorTitle')}</h1>
            <p role="alert">{t('party.errorBody')}</p>
            <button className="party-guest-hub-retry" type="button" onClick={() => load()}>
              {t('common.tryAgain')}
            </button>
          </div>
        </div>
      </main>
    );
  }

  const { albumName, items, coverUrl, contributionUrl, gameEnabled, printUrl } = state;
  // Rank-ordered filtered view: face-search matches first-to-last, restricted
  // to items still visible in the live album (a match hidden since the search
  // simply drops out on the next poll).
  const visibleItems = faceFilter
    ? faceFilter.itemIds
      .map((id) => items.find((it) => it.id === id))
      .filter((it): it is PartyItem => it !== undefined)
    : items;

  const shapes = galleryShapes(visibleItems.length);

  // What this party actually offers right now. "Share a moment" is deliberately
  // absent: it is the hero's primary CTA and must not be duplicated here.
  // What this party actually offers. Each entry states its own availability;
  // the deck renders the ones that pass and never learns why.
  const capabilities: Capability[] = [
    {
      // The most distinctive NubArca capability, so it leads the deck. The
      // party surface publishes no availability flag for face search — it
      // reports an `unavailable` status at search time instead — so the card is
      // offered whenever the party itself is, exactly as before. When a real
      // public signal exists, it belongs here and nowhere else.
      id: 'face',
      titleKey: 'partyHub.face',
      descriptionKey: 'partyHub.faceHelp',
      icon: <FaceFrameIcon />,
      target: { kind: 'action', onSelect: () => setFaceOpen(true) },
      variant: 'signature',
      available: true,
    },
    {
      // A written contribution, and the SAME enablement as any other: the
      // backend ties messages to the upload token and to the one UploadEnabled
      // switch, so a party that accepts contributions accepts dedications. That
      // is the real signal — there is no separate flag to consult, and none was
      // invented. The link is the backend's contribution URL with the composer
      // asked for, never a second route or a rebuilt token.
      id: 'dedication',
      titleKey: 'partyHub.dedication',
      descriptionKey: 'partyHub.dedicationHelp',
      icon: <HeartIcon />,
      target: {
        kind: 'anchor',
        href: contributionUrl ? withContributionMode(contributionUrl, 'message') : '',
      },
      variant: 'activity',
      available: Boolean(contributionUrl),
    },
    {
      // Only when the owner turned the party game on.
      id: 'challenges',
      titleKey: 'partyHub.vote',
      descriptionKey: 'partyHub.voteHelp',
      icon: <TrophyIcon />,
      target: { kind: 'route', to: `/party/${token ?? ''}/challenges` },
      variant: 'activity',
      badgeKey: 'partyHub.live',
      available: gameEnabled && Boolean(token),
    },
    {
      // Printing is PHYSICAL, so this card appears only when a sheet would
      // really come out: the server hands back a print URL exclusively while a
      // live station, a 10x15 printer and a remaining budget all hold at once.
      // There is nothing to derive here and nothing to guess — a null url is
      // the whole answer.
      id: 'print',
      titleKey: 'partyHub.print',
      descriptionKey: 'partyHub.printHelp',
      icon: <PrinterIcon />,
      target: { kind: 'route', to: printUrl ?? '' },
      variant: 'activity',
      available: Boolean(printUrl),
    },
    {
      // The album is what a party landing IS: available whenever the page is.
      id: 'album',
      titleKey: 'partyHub.photos',
      descriptionKey: 'partyHub.photosHelp',
      icon: <PhotoStackIcon />,
      target: { kind: 'anchor', href: '#party-photos' },
      variant: 'utility',
      available: true,
    },
  ];
  const visibleCapabilities = capabilities.filter((cap) => cap.available);
  return (
    <main className="party-guest-hub">
      {/* The event cover IS the first viewport: full-bleed, cropped naturally
          and faded into the page, with the album name and the one action a
          guest needs sitting inside it. */}
      <header className="party-guest-hub-hero" ref={heroRef}>
        <div
          className="party-guest-hub-hero-cover"
          data-testid="party-hub-cover"
          data-cover={coverUrl ? 'photo' : 'fallback'}
          style={coverUrl ? { backgroundImage: `url("${coverUrl}")` } : undefined}
          aria-hidden="true"
        />
        <PartyHubTopBar />
        <div className="party-guest-hub-headline">
          <p className="party-guest-hub-eyebrow">{PARTY_EYEBROW}</p>
          <h1 className="party-guest-hub-title">{albumName}</h1>
          <p className="party-guest-hub-meta">
            <span>{tn(items.length, 'party.momentCount')}</span>
            <span className="party-guest-hub-meta-sep" aria-hidden="true">·</span>
            <span className="party-guest-hub-live">
              <span className="party-guest-hub-live-dot" aria-hidden="true" />
              {t('partyHub.live')}
            </span>
          </p>
          {/* Only rendered when the backend actually returned a contribution
              URL — an album with guest uploads closed shows no dead action. */}
          {contributionUrl && (
            <a
              className="party-guest-hub-cta"
              href={contributionUrl}
              data-testid="party-hub-cta"
            >
              <span className="party-guest-hub-cta-icon">
                <CameraIcon />
              </span>
              <span className="party-guest-hub-cta-text">
                <strong>{t('partyHub.shareMoment')}</strong>
                <span>{t('partyHub.shareMomentHelp')}</span>
              </span>
              <ChevronIcon className="party-guest-hub-cta-chevron" />
            </a>
          )}
        </div>
      </header>

      <div className="party-guest-hub-body">
      <CapabilityDeck capabilities={visibleCapabilities} />

      {/* While a search is applied the album is NOT the whole album, so the page
          says so and offers the way back — otherwise closing the sheet would
          leave a filtered gallery with nothing explaining it. */}
      {faceFilter && (
        <div className="party-guest-hub-filter" data-testid="party-face-filter">
          <p className="party-guest-hub-filter-text">
            <strong>{t('partyFace.filterActive')}</strong>
            <span>{tn(visibleItems.length, 'partyFace.resultsTitle')}</span>
          </p>
          <button
            type="button"
            className="party-guest-hub-filter-clear"
            data-testid="party-face-filter-clear"
            onClick={() => cancelFaceSearch(faceFilter.searchId)}
          >
            {t('partyFace.showAll')}
          </button>
        </div>
      )}

      <section
        id="party-photos"
        className="party-guest-hub-gallery"
        ref={galleryRef}
        aria-labelledby="party-gallery-title"
      >
        <header className="party-guest-hub-gallery-head">
          <div className="party-guest-hub-gallery-heading">
            <h2 className="party-guest-hub-gallery-title" id="party-gallery-title">
              {t('party.galleryTitle')}
            </h2>
            <p className="party-guest-hub-gallery-help">{t('party.galleryLiveHelp')}</p>
          </div>
          {/* Filtered, this is a match count and says so — never the album's
              total dressed up as one. */}
          <p className="party-guest-hub-gallery-count" data-testid="party-gallery-count">
            {faceFilter
              ? tn(visibleItems.length, 'partyFace.resultsTitle')
              : tn(items.length, 'party.momentCount')}
          </p>
        </header>

        {/* The live region is the WRAPPER, and it is always in the DOM: a status
            role that appears together with its own content is announced
            unreliably. The pill inside it stays an ordinary button — a control
            is not a status — so the arrival is announced exactly once, by the
            region, and the text is not duplicated anywhere. */}
        <div className="party-guest-hub-gallery-live" role="status">
          {newMoments > 0 && !faceFilter && (
            <button
              type="button"
              className="party-guest-hub-gallery-new"
              data-testid="party-new-moments"
              onClick={acknowledgeMoments}
            >
              {tn(newMoments, 'party.newMoments')}
            </button>
          )}
        </div>

      {visibleItems.length === 0 ? (
        <div className="party-guest-hub-empty" data-testid="party-empty">
          <span className="party-guest-hub-empty-icon" aria-hidden="true"><PhotoStackIcon /></span>
          <p className="party-guest-hub-empty-title">
            {faceFilter ? t('partyFace.noMatches') : t('party.empty')}
          </p>
          {/* A nudge, not a second primary action: the hero's CTA is the one
              way to contribute, and it is a thumb away above this. */}
          {!faceFilter && contributionUrl && (
            <p className="party-guest-hub-empty-help">{t('party.emptyBeFirst')}</p>
          )}
        </div>
      ) : (
        <div className="party-guest-hub-tiles" data-testid="party-grid">
          {visibleItems.map((item, index) => (
            <button
              key={item.id}
              type="button"
              className="party-guest-hub-tile"
              data-shape={shapes[index]}
              onClick={(e) => openViewer(item, e.currentTarget)}
              aria-label={item.mediaType === 'video' ? t('party.openVideo') : t('party.openPhoto')}
            >
              <img
                className="party-guest-hub-tile-img"
                src={item.thumbnailUrl}
                alt=""
                loading="lazy"
              />
              {/* The party surface serves a POSTER for a video and no duration,
                  so the tile says "video" and invents nothing else. */}
              {item.mediaType === 'video' && (
                <span className="party-guest-hub-tile-play" aria-hidden="true"><PlayIcon /></span>
              )}
            </button>
          ))}
        </div>
      )}
      </section>
      </div>

      {/* Navigation, not a second deck. Hidden while the cover is still on
          screen, so the first viewport stays the cover — and hidden means NOT
          RENDERED, so nothing is reachable by Tab behind it. */}
      <PartyGuestDock
        visible={!heroOnScreen}
        section={galleryOnScreen ? 'album' : 'home'}
        contributionUrl={contributionUrl}
        onHome={() => scrollTo(heroRef.current)}
        onAlbum={() => scrollTo(galleryRef.current)}
      />

      {token && (
        <PartyFaceSearch
          token={token}
          open={faceOpen}
          onOpenChange={setFaceOpen}
          onFilterChange={setFaceFilter}
          onCancelSearch={cancelFaceSearch}
          onShowResults={showFaceResults}
        />
      )}

      {lightbox && (
        <div
          className="party-guest-hub-viewer"
          role="dialog"
          aria-modal="true"
          aria-label={lightbox.mediaType === 'video'
            ? t('party.videoViewer')
            : t('party.photoViewer')}
          ref={viewerRef}
          onClick={() => setLightbox(null)}
          onKeyDown={trapViewerFocus}
        >
          <div className="party-guest-hub-viewer-inner" onClick={(e) => e.stopPropagation()}>
            {/* The medium PREVIEW, whole and uncropped — for a video this is the
                poster the party surface serves; there is no playback here. */}
            <img className="party-guest-hub-viewer-img" src={lightbox.previewUrl} alt="" />
            <div className="party-guest-hub-viewer-bar">
              <button
                type="button"
                className="party-guest-hub-viewer-close"
                data-testid="party-viewer-close"
                autoFocus
                onClick={() => setLightbox(null)}
              >
                {t('common.close')}
              </button>
              {lightbox.downloadUrl && (
                <a
                  className="party-guest-hub-viewer-download"
                  href={lightbox.downloadUrl}
                  download
                >
                  {t('common.download')}
                </a>
              )}
            </div>
          </div>
        </div>
      )}
    </main>
  );
}
