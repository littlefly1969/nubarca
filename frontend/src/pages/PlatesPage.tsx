import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  deletePlateImage,
  listPlateImages,
  type PlateImageListItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { PlateUploadPanel } from '../components/plates/PlateUploadPanel';
import { PlateImageGrid } from '../components/plates/PlateImageGrid';
import { PlateImageDetail } from '../components/plates/PlateImageDetail';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; items: PlateImageListItem[] }
  | { kind: 'error'; message: string };

// Owner-private Plates (Targhe) surface. Segregated from Files/Gallery/People/
// Party/TV/Private Vault: it talks only to /api/plates/* and renders derived
// media (thumbnail/preview), never originals inline.
export function PlatesPage() {
  const { invalidateAuth } = useAuth();
  const { t, tn } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    listPlateImages(ctrl.signal)
      .then((items) => setStatus({ kind: 'ready', items }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setStatus({ kind: 'error', message: t('plates.loadError') });
      });
  }, [invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  const handleDelete = useCallback(
    async (item: PlateImageListItem) => {
      if (!window.confirm(t('plates.confirmDelete', { name: item.originalFileName }))) return;
      try {
        await deletePlateImage(item.id);
        setSelectedId((cur) => (cur === item.id ? null : cur));
        load();
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        window.alert(t('plates.deleteError'));
      }
    },
    [invalidateAuth, load, t],
  );

  const selected =
    status.kind === 'ready' ? status.items.find((i) => i.id === selectedId) ?? null : null;

  return (
    <div className="page-container">
      {/* h3: the Laboratory shell owns the h2. */}
      <h3>{t('lab.plates')}</h3>
      <p className="plates-intro">{t('plates.intro')}</p>

      <PlateUploadPanel onUploaded={load} />

      {status.kind === 'loading' && <p>{t('common.loading')}</p>}
      {status.kind === 'error' && (
        <p className="page-error" role="alert">
          {status.message}
        </p>
      )}
      {status.kind === 'ready' &&
        (status.items.length === 0 ? (
          <p className="empty-state">{t('plates.empty')}</p>
        ) : (
          <>
            <p className="plates-count">{tn(status.items.length, 'plates.count')}</p>
            <PlateImageGrid items={status.items} onOpen={(i) => setSelectedId(i.id)} onDelete={handleDelete} />
          </>
        ))}

      {selected && (
        <PlateImageDetail
          id={selected.id}
          onClose={() => setSelectedId(null)}
          onDelete={() => void handleDelete(selected)}
          onChanged={load}
        />
      )}
    </div>
  );
}
