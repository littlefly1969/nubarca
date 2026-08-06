import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getUnassignedFaces,
  listPeople,
  type Person,
  type UnassignedFace,
} from '@nubarca/api-client';
import { FaceCrop } from './FaceCrop';
import { AssignToPersonMenu } from './AssignToPersonMenu';
import { useI18n } from '../../i18n';

// Owner-private "Volti non assegnati" view: the pool of detected faces not yet
// assigned to any person (and not ignored). Each face can be assigned to a person,
// used to create a new person, ignored, or opened in the context viewer. Uses the
// server FaceCrop preview; shows no storage internals.
export function UnassignedFacesTab({
  onOpenFace,
  invalidateAuth,
}: {
  onOpenFace: (faceIds: string[], index: number) => void;
  invalidateAuth: () => void;
}) {
  const { t } = useI18n();
  const [items, setItems] = useState<UnassignedFace[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [onlyWithEmbedding, setOnlyWithEmbedding] = useState(false);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [loadingMore, setLoadingMore] = useState(false);

  const load = useCallback(async () => {
    setStatus('loading');
    try {
      const [pageRes, peopleRes] = await Promise.all([
        getUnassignedFaces({ limit: 60, hasEmbedding: onlyWithEmbedding ? true : undefined, sort: 'recent' }),
        listPeople(),
      ]);
      setItems(pageRes.items);
      setCursor(pageRes.nextCursor);
      setHasMore(pageRes.nextCursor != null);
      setPeople(peopleRes);
      setStatus('ready');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setStatus('error');
    }
  }, [onlyWithEmbedding, invalidateAuth]);

  useEffect(() => { void load(); }, [load]);

  async function loadMore() {
    if (!cursor) return;
    setLoadingMore(true);
    try {
      const page = await getUnassignedFaces({
        limit: 60, cursor, hasEmbedding: onlyWithEmbedding ? true : undefined, sort: 'recent',
      });
      setItems((prev) => [...prev, ...page.items]);
      setCursor(page.nextCursor);
      setHasMore(page.nextCursor != null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setLoadingMore(false);
    }
  }

  // After assigning/ignoring a face, drop it from the pool and refresh people
  // (a new person may have been created).
  function afterChange(faceId: string) {
    setItems((prev) => prev.filter((i) => i.faceId !== faceId));
    void listPeople().then(setPeople).catch(() => { /* non-fatal */ });
  }

  if (status === 'error') {
    return (
      <div className="folder-error" role="alert">
        {t('people.loadError')} <button type="button" className="retry-button" onClick={() => void load()}>{t('common.tryAgain')}</button>
      </div>
    );
  }

  return (
    <div className="unassigned-faces" aria-label={t('people.tabUnassigned')}>
      <div className="unassigned-toolbar">
        <label className="unassigned-filter">
          <input
            type="checkbox"
            checked={onlyWithEmbedding}
            onChange={(e) => setOnlyWithEmbedding(e.target.checked)}
          />
          {t('face.onlyWithEmbedding')}
        </label>
      </div>

      {status === 'loading' ? (
        <p className="muted" role="status">{t('common.loading')}</p>
      ) : items.length === 0 ? (
        <p className="muted">{t('face.noUnassigned')}</p>
      ) : (
        <>
          <ul className="people-grid">
            {items.map((fc, idx) => (
              <li key={fc.faceId} className="people-card unassigned-card">
                <FaceCrop
                  faceId={fc.faceId}
                  fileItemId={fc.fileItemId}
                  box={fc.box}
                  alt={t('face.unassignedFaceAlt')}
                  onClick={() => onOpenFace(items.map((i) => i.faceId), idx)}
                />
                <AssignToPersonMenu
                  faceId={fc.faceId}
                  people={people}
                  allowIgnore
                  onChanged={() => afterChange(fc.faceId)}
                  invalidateAuth={invalidateAuth}
                />
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
