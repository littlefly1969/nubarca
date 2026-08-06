import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  getPartyAlbum,
  getPartyItems,
  type PartyItem,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyFaceSearch, type PartyFaceFilter } from '../components/PartyFaceSearch';

// PUBLIC, unauthenticated party album landing. Reached by scanning the QR shown
// on a paired TV (or opening the shared link). View-only: it renders party-safe
// DERIVED media (metadata-stripped thumbnails/previews) and offers a download of
// the same safe medium copy for photos. No login, no upload, no owner identity,
// no metadata, no face/person data — the backend guarantees all of that; this
// page simply shows what the token-scoped API returns. When party mode is
// disabled/revoked the API returns 404 and we show a friendly "unavailable".
type State =
  | { kind: 'loading' }
  | { kind: 'ready'; albumName: string; items: PartyItem[] }
  | { kind: 'unavailable' }
  | { kind: 'error' };

// Live-refresh interval for the public party view so guest uploads appear
// without a manual reload. Same 10-20s band as the TV surfaces; each poll
// re-checks the token server-side (revoked/disabled/expired → 404).
const PARTY_POLL_MS = 15_000;

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
        setState({ kind: 'ready', albumName: album.albumName, items: items.items });
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
            ? { kind: 'ready', albumName: fresh.albumName, items: fresh.items }
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
    return <main className="party-page"><p className="party-status">{t('common.loading')}</p></main>;
  }
  if (state.kind === 'unavailable') {
    return (
      <main className="party-page">
        <div className="party-card">
          <h1>{t('party.unavailableTitle')}</h1>
          <p role="alert">{t('party.unavailableBody')}</p>
        </div>
      </main>
    );
  }
  if (state.kind === 'error') {
    return (
      <main className="party-page">
        <div className="party-card">
          <h1>{t('party.errorTitle')}</h1>
          <p role="alert">{t('party.errorBody')}</p>
          <button type="button" onClick={() => load()}>{t('common.tryAgain')}</button>
        </div>
      </main>
    );
  }

  const { albumName, items } = state;
  // Rank-ordered filtered view: face-search matches first-to-last, restricted
  // to items still visible in the live album (a match hidden since the search
  // simply drops out on the next poll).
  const visibleItems = faceFilter
    ? faceFilter.itemIds
      .map((id) => items.find((it) => it.id === id))
      .filter((it): it is PartyItem => it !== undefined)
    : items;
  return (
    <main className="party-page">
      <header className="party-header">
        <div className="party-header-top">
          <h1>{albumName}</h1>
          <LanguageSwitcher className="language-switcher language-switcher-public" />
        </div>
        <p className="party-subtitle">
          {tn(items.length, 'party.itemCount')} · {t('party.subtitleSuffix')}
        </p>
      </header>

      {token && <PartyFaceSearch token={token} onFilterChange={setFaceFilter} />}

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
