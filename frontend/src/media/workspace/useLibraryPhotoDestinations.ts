import { useMemo } from 'react';
import { addPlateImagesFromGallery, addToLithophaneQueue } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import type { MediaPhotoDestination } from './MediaWorkspace';

// WHAT each photo-only destination does when chosen, for the owner's own walls
// (the library, the owner's lists and albums). Whether it is OFFERED is not
// decided here: the workspace's action model gates it on an all-photo selection
// plus the Laboratory permissions the server requires, so a page cannot
// accidentally hand a user a door that answers 403.
export function useLibraryPhotoDestinations(): MediaPhotoDestination[] {
  const { t } = useI18n();
  return useMemo<MediaPhotoDestination[]>(() => [
    {
      id: 'plates',
      run: async (ids) => {
        const r = await addPlateImagesFromGallery(ids);
        return r.added.length === 1
          ? t('gallery.ws.plates.added_one', { count: 1 })
          : t('gallery.ws.plates.added_other', { count: r.added.length });
      },
    },
    {
      // The photos wait in the lithophane studio until used or removed there.
      // Someone else's photo in a shared album is not the owner's to send: it
      // is skipped, and the notice says how many.
      id: 'lithophanes',
      run: async (ids) => {
        try {
          const r = await addToLithophaneQueue(ids);
          const added = r.added.length === 1
            ? t('gallery.ws.lithophanes.added_one', { count: 1 })
            : t('gallery.ws.lithophanes.added_other', { count: r.added.length });
          return r.skipped.length > 0 ? added + t('gallery.ws.lithophanes.skipped', { count: r.skipped.length }) : added;
        } catch {
          return t('gallery.ws.lithophanes.error');
        }
      },
    },
  ], [t]);
}
