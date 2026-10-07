import { useCallback, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  addAestheticLabFromGallery,
  addPlateImagesFromGallery,
  type MediaGalleryScope,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { MediaWorkspace, type MediaPhotoDestination } from '../media/workspace/MediaWorkspace';
import {
  filtersToUrlParams,
  identityFromUrlParams,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '../media/workspace/mediaWorkspaceQuery';
import { readSharedAlbumAddContext } from '../albums/sharedAlbumAddContext';

// The unified library page. `scope` comes from the route (/media vs
// /media/excluded) and App gives each a distinct key so a scope switch remounts
// the page — a clean reset. `identity` is owned in state (the source of truth):
// only the shareable subset is mirrored to the URL, so the SESSION-ONLY filters
// (visual query, GPS, dates, favorite, rating, collapse — deliberately kept out
// of the URL for privacy) survive an Apply instead of being wiped by an
// URL round-trip.

const LIBRARY_SOURCE: MediaWorkspaceSource = { kind: 'library' };

export function MediaLibraryPage({ scope = 'active' }: { scope?: MediaGalleryScope } = {}) {
  const { state } = useAuth();
  const { t } = useI18n();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const location = useLocation();
  const effectiveScope = scope === 'excluded' ? 'excluded' : 'active';

  // "I arrived from a shared album to fill it." Read ONCE, on mount: router
  // state belongs to the history entry that carried it, and every later
  // navigation here (including this page's own replace-navigations for the
  // query string) carries none — so the target cannot survive into an unrelated
  // visit. Nothing about the Library changes because of it: it adds a notice and
  // a way back, and preselects a destination in the shared picker.
  const [addContext] = useState(() => readSharedAlbumAddContext(location.state));

  // Read the URL ONCE (on mount) to seed the applied identity; from then on the
  // in-memory identity is authoritative and the URL is written from it.
  const initialParamsRef = useRef(searchParams);
  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(() => {
    const base = identityFromUrlParams(LIBRARY_SOURCE, initialParamsRef.current);
    return { ...base, libraryScope: effectiveScope };
  });

  const onIdentityChange = useCallback((next: MediaWorkspaceIdentity) => {
    // A scope switch is a route change (distinct key → clean remount + reset).
    if (next.libraryScope !== effectiveScope) {
      const sp = filtersToUrlParams(next);
      sp.delete('scope');
      const path = next.libraryScope === 'excluded' ? '/media/excluded' : '/media';
      const qs = sp.toString();
      navigate(qs ? `${path}?${qs}` : path);
      return;
    }
    setIdentity(next);
    const sp = filtersToUrlParams(next);
    sp.delete('scope'); // scope is encoded in the route path, not the query
    setSearchParams(sp, { replace: true });
  }, [effectiveScope, navigate, setSearchParams]);

  // WHAT each photo-only destination does when chosen. Whether it is OFFERED is
  // not decided here: the workspace's action model gates both of these on an
  // all-photo selection plus the Laboratory permissions the server requires, so
  // this page cannot accidentally hand a user a door that answers 403.
  const photoDestinations = useMemo<MediaPhotoDestination[]>(() => [
    {
      id: 'beauty-lab',
      run: async (ids) => {
        const r = await addAestheticLabFromGallery(ids);
        return t('aesthetics.addedFromGallery', { added: r.added.length, skipped: r.skipped.length });
      },
    },
    {
      id: 'plates',
      run: async (ids) => {
        const r = await addPlateImagesFromGallery(ids);
        return r.added.length === 1
          ? t('gallery.ws.plates.added_one', { count: 1 })
          : t('gallery.ws.plates.added_other', { count: r.added.length });
      },
    },
  ], [t]);

  if (state.status !== 'authed') return null;

  return (
    <section className="ws-page-outer" data-testid="media-library-page">
      <header className="ws-page-header">
        <h1>{t('mediaLib.title')}</h1>
        {addContext && (
          <p className="library-add-context" role="status" data-testid="library-add-context">
            {t('mediaLib.addingToShared', { album: addContext.albumName })}
            {' '}
            <Link to={addContext.returnPath}>{t('mediaLib.backToSharedAlbum')}</Link>
          </p>
        )}
      </header>
      <MediaWorkspace
        source={LIBRARY_SOURCE}
        identity={identity}
        onIdentityChange={onIdentityChange}
        searchPlaceholder={t('mediaWs.searchLibrary')}
        photoDestinations={photoDestinations}
        preselectedAlbumId={addContext?.albumId}
      />
    </section>
  );
}
