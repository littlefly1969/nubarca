import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getPartyAlbum,
  getPartyItems,
  type PartyItem,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
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
      <nav className="party-hub-actions" aria-label={t('partyHub.actions')}>
        <a className="party-hub-action" href="#party-photos">
          <strong>{t('partyHub.photos')}</strong><span>{t('partyHub.photosHelp')}</span>
        </a>
        <a className="party-hub-action" href="#party-face">
          <strong>{t('partyHub.face')}</strong><span>{t('partyHub.faceHelp')}</span>
        </a>
        {gameEnabled && token && (
          <Link className="party-hub-action party-hub-action-game" to={`/party/${token}/challenges`}>
            <strong>{t('partyHub.vote')}</strong><span>{t('partyHub.voteHelp')}</span>
          </Link>
        )}
      </nav>

      <section id="party-face">
        {token && <PartyFaceSearch token={token} onFilterChange={setFaceFilter} />}
      </section>

      <section id="party-photos">
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
