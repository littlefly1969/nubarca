import { useMemo } from 'react';
import { addPlateImagesFromGallery } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import type { MediaPhotoDestination } from './MediaWorkspace';

// WHAT each photo-only destination does when chosen, for the owner's own walls
// (the library and the owner's lists). Whether it is OFFERED is not decided
// here: the workspace's action model gates it on an all-photo selection plus
// the Laboratory permissions the server requires, so a page cannot accidentally
// hand a user a door that answers 403.
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
  ], [t]);
}
