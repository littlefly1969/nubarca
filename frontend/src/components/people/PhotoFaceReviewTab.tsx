import { useCallback, useEffect, useRef, useState } from 'react';
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
interface OpenPhoto {
  photo: PhotoWithUnassignedFaces;
  faceIds: string[];
  index: number;
}

export function PhotoFaceReviewTab() {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();

  const [photos, setPhotos] = useState<PhotoWithUnassignedFaces[]>([]);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error' | 'unavailable'>('loading');
  const [cursor, setCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);

  // The open photo, as a QUEUE the caller owns: FaceContextViewer is controlled,
  // so the face list and position live here and nowhere else.
  const [open, setOpen] = useState<OpenPhoto | null>(null);
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

  // The queue, readable synchronously.
  //
  // Both updates below have to be decided TOGETHER — which photo leaves the list
  // and which one opens — and a state updater is the wrong place to decide
  // anything: React may call it more than once or defer it, so a `setOpen` inside
  // one fires an unpredictable number of times at an unpredictable moment. That
  // was a real intermittent failure, not a flaky test.
  const photosRef = useRef<PhotoWithUnassignedFaces[]>([]);
  photosRef.current = photos;

  /** Open the next photo in the queue after `fileItemId`, or close the review. */
  const advancePhoto = useCallback((finishedFileItemId: string) => {
    const prev = photosRef.current;
    const at = prev.findIndex((p) => p.fileItemId === finishedFileItemId);
    const remaining = prev.filter((p) => p.fileItemId !== finishedFileItemId);
    // The photo that took this one's place — the next in the queue, or nothing
    // if it was the last.
    const next = at >= 0 && at < remaining.length ? remaining[at] : null;
    photosRef.current = remaining;
    setPhotos(remaining);
    setOpen(next ? { photo: next, faceIds: next.faceIds, index: 0 } : null);
  }, []);

  /**
   * A face stopped being undecided — assigned or ignored, it does not matter
   * which, because both are decisions and both remove it from this photo's work.
   * Stay on the same POSITION, which is now the next face; when the photo runs
   * out, the whole photo is done and the queue moves on.
   */
  const openRef = useRef<OpenPhoto | null>(null);
  openRef.current = open;

  const faceDecided = useCallback((faceId: string) => {
    const current = openRef.current;
    if (!current) return;

    const faceIds = current.faceIds.filter((id) => id !== faceId);
    if (faceIds.length === 0) {
      // The photo is finished: dropping it from the queue and opening the next
      // one is one decision, taken here rather than split across two updaters.
      advancePhoto(current.photo.fileItemId);
      return;
    }

    const next = { ...current, faceIds, index: Math.min(current.index, faceIds.length - 1) };
    openRef.current = next;
    setOpen(next);
    const remaining = photosRef.current.map((p) => (p.faceIds.includes(faceId)
      ? { ...p, faceIds: p.faceIds.filter((id) => id !== faceId), unassignedCount: p.unassignedCount - 1 }
      : p));
    photosRef.current = remaining;
    setPhotos(remaining);
  }, [advancePhoto]);

  /**
   * Open the next LOADED photo without finishing this one.
   *
   * Deliberately NOT advancePhoto: that removes a photo the reviewer is done
   * with. This is plain navigation — the current photo keeps every undecided
   * face, keeps its count, and keeps its place in the list, and no request is
   * made. It is how somebody parks a difficult photo and comes back to it.
   *
   * It never wraps: at the last loaded photo there is no next one and the
   * control is disabled rather than quietly returning to the top.
   */
  const openNextPhoto = useCallback(() => {
    const current = openRef.current;
    if (!current) return;
    const list = photosRef.current;
    const at = list.findIndex((p) => p.fileItemId === current.photo.fileItemId);
    const next = at >= 0 ? list[at + 1] : undefined;
    if (!next) return;
    const opened = { photo: next, faceIds: next.faceIds, index: 0 };
    openRef.current = opened;
    setOpen(opened);
  }, []);

  /** Leave the face undecided and move on — the third answer beside assign and ignore. */
  const skipFace = useCallback(() => {
    const current = openRef.current;
    if (!current || current.faceIds.length === 0) return;
    // Wraps, so skipping the last face returns to the first still-undecided one
    // instead of dead-ending on a photo that still has work in it.
    const next = { ...current, index: (current.index + 1) % current.faceIds.length };
    openRef.current = next;
    setOpen(next);
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

  // Is there a photo AFTER the open one in the loaded queue? Read from `photos`
  // (not the ref) so the control re-renders when the queue changes.
  const openAt = open ? photos.findIndex((p) => p.fileItemId === open.photo.fileItemId) : -1;
  const hasNextPhoto = openAt >= 0 && openAt + 1 < photos.length;

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
          reviewControls={{
            progressLabel: t('people.photoReviewProgress', {
              current: open.index + 1,
              total: open.faceIds.length,
            }),
            // Skipping is only an answer when there is somewhere else on this
            // photo to go.
            canSkipFace: open.faceIds.length > 1,
            onSkipFace: skipFace,
            canNextPhoto: hasNextPhoto,
            onNextPhoto: openNextPhoto,
            onIgnoreRemaining: () => { void ignoreWholePhoto(); },
            ignoreRemainingBusy: busy,
          }}
        />
      )}
    </section>
  );
}
