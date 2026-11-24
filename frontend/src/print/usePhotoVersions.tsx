import { useMemo, useState } from 'react';
import { useI18n } from '../i18n';
import type { StudioPhoto } from './studio/PrintStudio';

/** Selection changes only this print's source, never the shared photograph. */
export function usePhotoVersions(photos: readonly StudioPhoto[]) {
  const { t } = useI18n();
  const [originals, setOriginals] = useState<Record<string, boolean>>({});
  const selected = useMemo(() => photos.map((photo) => {
    const raw = (url: string) => `${url}${url.includes('?') ? '&' : '?'}original=true`;
    return originals[photo.id] && photo.hasPhotoText
      ? { ...photo, thumbnailUrl: raw(photo.thumbnailUrl), previewUrl: raw(photo.previewUrl) }
      : photo;
  }), [photos, originals]);
  return {
    photos: selected,
    key: JSON.stringify([originals, photos.map((p) => [p.id, p.photoTextVersion])]),
    source: (id: string) => {
      const photo = photos.find((p) => p.id === id);
      return photo?.photoTextVersion === undefined ? {} : {
        useOriginal: !!originals[id], photoTextVersion: photo.photoTextVersion,
      };
    },
    controls: (id: string) => photos.find((p) => p.id === id)?.hasPhotoText ? (
      <fieldset className="party-print-choice" data-testid="print-photo-version">
        <legend>{t('photoEdit.printVersion')}</legend>
        <label><input type="radio" name={`photo-version-${id}`} checked={!originals[id]}
          onChange={() => setOriginals((prev) => ({ ...prev, [id]: false }))} />{t('photoEdit.decorated')}</label>
        <label><input type="radio" name={`photo-version-${id}`} checked={!!originals[id]}
          onChange={() => setOriginals((prev) => ({ ...prev, [id]: true }))} />{t('photoEdit.original')}</label>
      </fieldset>
    ) : null,
  };
}
