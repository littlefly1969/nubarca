import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
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
import { PRODUCT_NAME } from '../brand/brand';
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
      contributionUrl: string | null; gameEnabled: boolean }
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

// One capability = one card in the "what would you like to do?" deck.
//
// This is a list, not a framework: adding the dedication, song-request or print
// capabilities later means appending an entry here plus the REAL condition that
// makes it available. Nothing is rendered speculatively — a capability the
// product does not offer yet has no placeholder and no disabled card.
type CapabilityTarget =
  | { kind: 'anchor'; href: string }   // somewhere on this page
  | { kind: 'route'; to: string }      // a real party route
  | { kind: 'action'; onSelect: () => void };  // opens something in place

interface Capability {
  id: string;
  titleKey: MessageKey;
  descriptionKey: MessageKey;
  icon: ReactNode;
  target: CapabilityTarget;
  /** 'signature' leads the deck; 'game' is the warm accent; 'neutral' recedes. */
  variant: 'signature' | 'game' | 'neutral';
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
      <LanguageSwitcher className="language-switcher language-switcher-public" />
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
  // Phone-only face filter from a completed face search. The full album stays
  // in state (polling continues untouched); only the visible grid is narrowed.
  // The TV is NEVER affected by this — activation is a separate explicit action
  // inside PartyFaceSearch.
  const [faceFilter, setFaceFilter] = useState<PartyFaceFilter | null>(null);
  // The face-search sheet is opened by its capability card and by nothing else.
  const [faceOpen, setFaceOpen] = useState(false);
  const galleryRef = useRef<HTMLElement>(null);

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
    galleryRef.current?.scrollIntoView?.({ block: 'start' });
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
          gameEnabled: album.gameEnabled,
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

  useEffect(() => {
    if (!lightbox) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setLightbox(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
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

  const { albumName, items, coverUrl, contributionUrl, gameEnabled } = state;
  // Rank-ordered filtered view: face-search matches first-to-last, restricted
  // to items still visible in the live album (a match hidden since the search
  // simply drops out on the next poll).
  const visibleItems = faceFilter
    ? faceFilter.itemIds
      .map((id) => items.find((it) => it.id === id))
      .filter((it): it is PartyItem => it !== undefined)
    : items;

  // What this party actually offers right now. "Share a moment" is deliberately
  // absent: it is the hero's primary CTA and must not be duplicated here.
  const capabilities: Capability[] = [
    // The most distinctive NubArca capability, so it leads the deck. The anchor
    // keeps the existing PartyFaceSearch launcher exactly as it is.
    {
      id: 'face',
      titleKey: 'partyHub.face',
      descriptionKey: 'partyHub.faceHelp',
      icon: <FaceFrameIcon />,
      target: { kind: 'action', onSelect: () => setFaceOpen(true) },
      variant: 'signature',
    },
    // Challenges exist only when the owner enabled the party game.
    ...(gameEnabled && token
      ? [{
        id: 'challenges',
        titleKey: 'partyHub.vote',
        descriptionKey: 'partyHub.voteHelp',
        icon: <TrophyIcon />,
        target: { kind: 'route', to: `/party/${token}/challenges` },
        variant: 'game',
        badgeKey: 'partyHub.live',
      } as const satisfies Capability]
      : []),
    {
      id: 'album',
      titleKey: 'partyHub.photos',
      descriptionKey: 'partyHub.photosHelp',
      icon: <PhotoStackIcon />,
      target: { kind: 'anchor', href: '#party-photos' },
      variant: 'neutral',
    },
  ];
  return (
    <main className="party-guest-hub">
      {/* The event cover IS the first viewport: full-bleed, cropped naturally
          and faded into the page, with the album name and the one action a
          guest needs sitting inside it. */}
      <header className="party-guest-hub-hero">
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
            <span>{tn(items.length, 'party.itemCount')}</span>
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
      <CapabilityDeck capabilities={capabilities} />

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

      <section id="party-photos" ref={galleryRef}>
      {visibleItems.length === 0 ? (
        <p className="party-status" data-testid="party-empty">
          {faceFilter ? t('partyFace.noMatches') : t('party.empty')}
        </p>
      ) : (
        <div className="party-grid" data-testid="party-grid">
          {visibleItems.map((item) => (
            <button
              key={item.id}
              type="button"
              className="party-tile"
              onClick={() => setLightbox(item)}
              aria-label={t('party.openPhoto')}
            >
              <img className="party-thumb" src={item.thumbnailUrl} alt="" loading="lazy" />
              {item.mediaType === 'video' && (
                <span className="party-tile-badge" aria-hidden="true">▶</span>
              )}
            </button>
          ))}
        </div>
      )}
      </section>
      </div>

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
          className="party-lightbox"
          role="dialog"
          aria-label={t('party.photoViewer')}
          onClick={() => setLightbox(null)}
        >
          <div className="party-lightbox-inner" onClick={(e) => e.stopPropagation()}>
            <img className="party-lightbox-img" src={lightbox.previewUrl} alt="" />
            <div className="party-lightbox-bar">
              {lightbox.downloadUrl && (
                <a
                  className="party-download"
                  href={lightbox.downloadUrl}
                  download
                >
                  {t('common.download')}
                </a>
              )}
              <button type="button" onClick={() => setLightbox(null)}>{t('common.close')}</button>
            </div>
          </div>
        </div>
      )}
    </main>
  );
}
