import { useCallback, useMemo, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
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
  const effectiveScope = scope === 'excluded' ? 'excluded' : 'active';

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

  const photoDestinations = useMemo<MediaPhotoDestination[]>(() => [
    {
      id: 'beauty-lab',
      label: t('gallerySel.addToAestheticsLab'),
      run: async (ids) => {
        const r = await addAestheticLabFromGallery(ids);
        return t('aesthetics.addedFromGallery', { added: r.added.length, skipped: r.skipped.length });
      },
    },
    {
      id: 'plates',
      label: t('gallery.ws.destPlates'),
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
      </header>
      <MediaWorkspace
        source={LIBRARY_SOURCE}
        identity={identity}
        onIdentityChange={onIdentityChange}
        searchPlaceholder={t('mediaWs.searchLibrary')}
        photoDestinations={photoDestinations}
      />
    </section>
  );
}
