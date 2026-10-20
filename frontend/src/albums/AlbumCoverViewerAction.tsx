import { useState } from 'react';
import { ApiError, setSharedAlbumCover, type AlbumEditConflict } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// «Set as album cover», the LAST action of the viewer when it is opened inside
// an album by somebody who may curate it (the owner, or an Editor whose
// membership the server says may edit).
//
// It reuses the album's own cover route (PUT /api/shared-albums/{id}/cover),
// the one the content manager uses, so the same rules hold: the server accepts
// only an item this album serves right now, and the version guards a choice
// formed against an album that has since changed. On success the page adopts
// the new cover and version at once; on a conflict it adopts the CURRENT state
// the server returned and says so — it never retries on its own, because the
// other change may have been a cover too.

export interface AlbumCoverState {
  version: number;
  coverFileItemId: string | null;
}

interface Props {
  albumId: string;
  fileItemId: string;
  cover: AlbumCoverState;
  onChange(next: AlbumCoverState): void;
  onNotice(message: string): void;
  onUnauthorized(): void;
}

export function AlbumCoverViewerAction({
  albumId, fileItemId, cover, onChange, onNotice, onUnauthorized,
}: Props) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  // A refusal is said HERE as well as to the page: the viewer covers the page,
  // so its notice would otherwise wait until the viewer closed. Tied to the
  // item it is about, so moving to the next photo does not carry it along.
  const [refusal, setRefusal] = useState<{ fileItemId: string; message: string } | null>(null);

  if (cover.coverFileItemId === fileItemId) {
    return (
      <span className="media-viewer-cover-current" data-testid="viewer-album-cover-current">
        {t('albumCover.current')}
      </span>
    );
  }

  function refuse(message: string) {
    setRefusal({ fileItemId, message });
    onNotice(message);
  }

  async function setCover() {
    if (busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      const result = await setSharedAlbumCover(albumId, cover.version, fileItemId);
      onChange({ version: result.version, coverFileItemId: result.coverFileItemId });
      onNotice(t('albumCover.set'));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { onUnauthorized(); return; }
      if (err instanceof ApiError && err.status === 409) {
        const current = err.body as Partial<AlbumEditConflict> | null;
        if (current && typeof current.version === 'number') {
          onChange({ version: current.version, coverFileItemId: current.coverFileItemId ?? null });
        }
        refuse(t('albumCover.conflict'));
        return;
      }
      // 400: this item is no longer something the album may show (trashed,
      // withdrawn, excluded). 403/404: the caller may no longer curate.
      if (err instanceof ApiError && err.status === 400) { refuse(t('albumCover.unavailable')); return; }
      if (err instanceof ApiError && (err.status === 403 || err.status === 404)) {
        refuse(t('albumCover.notAllowed'));
        return;
      }
      refuse(t('albumCover.error'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      {refusal?.fileItemId === fileItemId && (
        <span className="media-viewer-cover-current" role="status" data-testid="viewer-album-cover-refusal">
          {refusal.message}
        </span>
      )}
      <button
        type="button"
        className="media-viewer-cover-action"
        data-testid="viewer-set-album-cover"
        disabled={busy}
        onClick={() => void setCover()}
      >
        {t('albumCover.setAction')}
      </button>
    </>
  );
}
