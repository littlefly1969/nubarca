import { useCallback, useEffect, useState } from 'react';
import { ApiError, getIgnoredFaces, unignoreFace, type IgnoredFace } from '@nubarca/api-client';
import { FaceCrop } from './FaceCrop';
import { useI18n } from '../../i18n';

// Owner-private "Ignorati" view: faces the owner dismissed with "Ignora volto".
// Lets them be RESTORED (un-ignore) — so a mistaken ignore is recoverable. Once
// restored, a face returns to the unassigned pool and clustering candidates. Uses
// the server FaceCrop preview; shows no storage internals.
export function IgnoredFacesTab({
  onOpenFace,
  invalidateAuth,
}: {
  onOpenFace: (faceIds: string[], index: number) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [items, setItems] = useState<IgnoredFace[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [loadingMore, setLoadingMore] = useState(false);

  const load = useCallback(async () => {
    setStatus('loading');
    try {
      const page = await getIgnoredFaces({ limit: 60 });
      setItems(page.items);
      setCursor(page.nextCursor);
      setHasMore(page.nextCursor != null);
      setStatus('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus('error');
    }
  }, [invalidateAuth]);

  useEffect(() => { void load(); }, [load]);

  async function loadMore() {
    if (!cursor) return;
    setLoadingMore(true);
    try {
      const page = await getIgnoredFaces({ limit: 60, cursor });
      setItems((prev) => [...prev, ...page.items]);
      setCursor(page.nextCursor);
      setHasMore(page.nextCursor != null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setLoadingMore(false);
    }
  }

  async function restore(faceId: string) {
    try {
      await unignoreFace(faceId);
      setItems((prev) => prev.filter((i) => i.faceId !== faceId));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    }
  }

  if (status === 'error') {
    return (
      <div className="folder-error" role="alert">
        {t('people.loadError')} <button type="button" className="retry-button" onClick={() => void load()}>{t('common.tryAgain')}</button>
      </div>
    );
  }

  return (
    <div className="ignored-faces" aria-label={t('face.ignoredFacesAria')}>
      <p className="muted">
        {t('face.ignoredIntro')}
      </p>
      {status === 'loading' ? (
        <p className="muted" role="status">{t('common.loading')}</p>
      ) : items.length === 0 ? (
        <p className="muted">{t('face.noIgnored')}</p>
      ) : (
        <>
          <ul className="people-grid">
            {items.map((fc, idx) => (
              <li key={fc.faceId} className="people-card">
                <FaceCrop
                  faceId={fc.faceId}
                  fileItemId={fc.fileItemId}
                  box={fc.box}
                  alt={t('face.ignoredFaceAlt')}
                  onClick={() => onOpenFace(items.map((i) => i.faceId), idx)}
                />
                <button type="button" onClick={() => void restore(fc.faceId)}>{t('face.restore')}</button>
              </li>
            ))}
          </ul>
          {hasMore && (
            <div className="unassigned-more">
              <button type="button" onClick={() => void loadMore()} disabled={loadingMore}>
                {loadingMore ? t('common.loading') : t('common.loadMore')}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
