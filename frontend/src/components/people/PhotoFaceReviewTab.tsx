import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getPhotosWithUnassignedFaces,
  ignoreUnassignedFacesOnPhoto,
  type PhotoWithUnassignedFaces,
} from '@nubarca/api-client';
import { smallThumbnailUrl } from '../files/types';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { FaceContextViewer } from './FaceContextViewer';

// Reviewing undecided faces ONE PHOTO AT A TIME.
//
// The face-at-a-time pool beside this one answers "which faces are undecided",
// and it stays: it is the right surface when you are hunting for a particular
// face. It is the wrong one for clearing a backlog, because consecutive faces
// come from unrelated photos, and each jump costs the context that makes a face
// recognisable at all — you end up re-reading the same picture three times.
//
// Here the unit of work is the photo. Every action moves to the next undecided
// face of the SAME photo, and only when the photo has none left does the queue
// advance. That is also why "ignore everything still undecided here" exists: it
// is the fastest true statement about a photo full of strangers.
export function PhotoFaceReviewTab() {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();

  const [photos, setPhotos] = useState<PhotoWithUnassignedFaces[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error' | 'unavailable'>('loading');
  const [cursor, setCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);

  // The open photo, as a QUEUE the caller owns: FaceContextViewer is controlled,
  // so the face list and position live here and nowhere else.
  const [open, setOpen] = useState<{ photo: PhotoWithUnassignedFaces; faceIds: string[]; index: number } | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setStatus('loading');
    try {
      const page = await getPhotosWithUnassignedFaces({ limit: 40 });
      if (!page.profileAvailable) { setStatus('unavailable'); return; }
      setPhotos(page.items);
      setCursor(page.nextCursor);
      setStatus('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus('error');
    }
  }, [invalidateAuth]);

  useEffect(() => { void load(); }, [load]);

  const loadMore = useCallback(async () => {
    if (!cursor || loadingMore) return;
    setLoadingMore(true);
    try {
      const page = await getPhotosWithUnassignedFaces({ limit: 40, cursor });
      setPhotos((prev) => [...prev, ...page.items]);
      setCursor(page.nextCursor);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setLoadingMore(false);
    }
  }, [cursor, loadingMore, invalidateAuth]);

  /** Open the next photo in the queue after `fileItemId`, or close the review. */
  const advancePhoto = useCallback((finishedFileItemId: string) => {
    setPhotos((prev) => {
      const remaining = prev.filter((p) => p.fileItemId !== finishedFileItemId);
      const at = prev.findIndex((p) => p.fileItemId === finishedFileItemId);
      // The photo that took this one's place — which is the next one in the
      // queue, or nothing if it was the last.
      const next = at >= 0 && at < remaining.length ? remaining[at] : null;
      setOpen(next ? { photo: next, faceIds: next.faceIds, index: 0 } : null);
      return remaining;
    });
  }, []);

  /**
   * A face stopped being undecided — assigned or ignored, it does not matter
   * which, because both are decisions and both remove it from this photo's work.
   * Stay on the same POSITION, which is now the next face; when the photo runs
   * out, the whole photo is done and the queue moves on.
   */
  const faceDecided = useCallback((faceId: string) => {
    setOpen((current) => {
      if (!current) return null;
      const faceIds = current.faceIds.filter((id) => id !== faceId);
      if (faceIds.length === 0) {
        // Deferred so this state updater stays pure — advancePhoto sets state too.
        queueMicrotask(() => advancePhoto(current.photo.fileItemId));
        return current;
      }
      return { ...current, faceIds, index: Math.min(current.index, faceIds.length - 1) };
    });
    setPhotos((prev) => prev.map((p) => (p.faceIds.includes(faceId)
      ? { ...p, faceIds: p.faceIds.filter((id) => id !== faceId), unassignedCount: p.unassignedCount - 1 }
      : p)));
  }, [advancePhoto]);

  /** Leave the face undecided and move on — the third answer beside assign and ignore. */
  const skipFace = useCallback(() => {
    setOpen((current) => {
      if (!current || current.faceIds.length === 0) return current;
      // Wraps, so skipping the last face returns to the first still-undecided
      // one instead of dead-ending on a photo that still has work in it.
      return { ...current, index: (current.index + 1) % current.faceIds.length };
    });
  }, []);

  const ignoreWholePhoto = useCallback(async () => {
    if (!open || busy) return;
    const fileItemId = open.photo.fileItemId;
    setBusy(true);
    try {
      await ignoreUnassignedFacesOnPhoto(fileItemId);
      advancePhoto(fileItemId);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setBusy(false);
    }
  }, [open, busy, advancePhoto, invalidateAuth]);

  if (status === 'loading') return <p className="muted">{t('common.loading')}</p>;
  if (status === 'error') return <p className="error">{t('people.photoReviewError')}</p>;
  if (status === 'unavailable') return <p className="muted">{t('people.photoReviewUnavailable')}</p>;
  if (photos.length === 0) return <p className="muted">{t('people.photoReviewEmpty')}</p>;

  return (
    <section className="photo-face-review" aria-label={t('people.photoReviewAria')}>
      <p className="muted">{t('people.photoReviewIntro', { count: photos.length })}</p>

      <ul className="photo-face-review-list">
        {photos.map((photo) => (
          <li key={photo.fileItemId}>
            <button
              type="button"
              className="photo-face-review-item"
              onClick={() => setOpen({ photo, faceIds: photo.faceIds, index: 0 })}
            >
              <img src={smallThumbnailUrl(photo.fileItemId)} alt="" loading="lazy" />
              <span className="photo-face-review-name">{photo.name}</span>
              <span className="photo-face-review-count">
                {t('people.photoReviewFaceCount', { count: photo.unassignedCount })}
              </span>
            </button>
          </li>
        ))}
      </ul>

      {cursor && (
        <button type="button" disabled={loadingMore} onClick={() => { void loadMore(); }}>
          {t('common.loadMore')}
        </button>
      )}

      {open && open.faceIds.length > 0 && (
        <FaceContextViewer
          faceIds={open.faceIds}
          index={open.index}
          onIndexChange={(next) => setOpen((c) => (c ? { ...c, index: next } : c))}
          onClose={() => setOpen(null)}
          onFaceIgnored={faceDecided}
          onFaceRestored={faceDecided}
          onFaceAssigned={faceDecided}
          progressLabel={t('people.photoReviewProgress', {
            current: open.index + 1,
            total: open.faceIds.length,
          })}
          extraActions={(
            <>
              <button type="button" onClick={skipFace}>{t('people.photoReviewSkip')}</button>
              <button type="button" disabled={busy} onClick={() => { void ignoreWholePhoto(); }}>
                {t('people.photoReviewIgnoreAll')}
              </button>
            </>
          )}
        />
      )}
    </section>
  );
}
