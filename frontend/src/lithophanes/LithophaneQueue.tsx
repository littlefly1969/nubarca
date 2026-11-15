import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import { listLithophaneQueue, removeFromLithophaneQueue, type LithophaneQueueEntry } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// The photographs the owner sent here with "Add to → Lithophanes" from the
// gallery, a list or an album. Small thumbnails only; never an original.
export function useLithophaneQueue(onError: (error: unknown) => void) {
  const [entries, setEntries] = useState<LithophaneQueueEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [reload, setReload] = useState(0);
  useEffect(() => {
    const ctrl = new AbortController();
    setLoading(true); setFailed(false);
    listLithophaneQueue(ctrl.signal).then((list) => { if (!ctrl.signal.aborted) setEntries(list); })
      .catch((e: unknown) => { if (!ctrl.signal.aborted) { setFailed(true); onError(e); } })
      .finally(() => { if (!ctrl.signal.aborted) setLoading(false); });
    return () => ctrl.abort();
  }, [onError, reload]);
  /** Drops an entry the server already ended (a generation used it). */
  const forget = useCallback((fileItemId: string) =>
    setEntries((old) => old.filter((x) => x.fileItemId !== fileItemId)), []);
  const remove = useCallback(async (fileItemId: string) => {
    try { await removeFromLithophaneQueue(fileItemId); forget(fileItemId); } catch (e) { onError(e); }
  }, [forget, onError]);
  return { entries, loading, failed, retry: () => setReload((v) => v + 1), forget, remove };
}

export function LithophaneQueue({ queue, selected, disabled, onSelect }: {
  queue: ReturnType<typeof useLithophaneQueue>; selected: string | null; disabled: boolean;
  onSelect(entry: LithophaneQueueEntry): void;
}) {
  const { t } = useI18n();
  return <section aria-label={t('lithophane.waiting')} className="lithophane-queue">
    <h4>{t('lithophane.waiting')}</h4>
    {queue.loading && <p role="status">{t('common.loading')}</p>}
    {queue.failed && <button type="button" className="btn btn-secondary" onClick={queue.retry}>{t('common.retry')}</button>}
    {!queue.loading && !queue.failed && queue.entries.length === 0 && <p className="lithophane-queue-empty">
      {t('lithophane.waitingEmpty')}{' '}
      <Link to="/media">{t('lithophane.openGallery')}</Link>{' · '}<Link to="/albums">{t('lithophane.openAlbums')}</Link>
    </p>}
    <ul className="lithophane-photos">
      {queue.entries.map((entry) => <li key={entry.fileItemId}>
        <button type="button" aria-pressed={selected === entry.fileItemId} disabled={disabled}
          onClick={() => onSelect(entry)} aria-label={entry.name}>
          <img src={entry.thumbnailUrl} alt="" loading="lazy" decoding="async" />
          <span>{entry.name}</span>
        </button>
        <button type="button" className="lithophane-queue-remove" disabled={disabled}
          aria-label={t('lithophane.removeWaiting', { name: entry.name })}
          onClick={() => void queue.remove(entry.fileItemId)}>×</button>
      </li>)}
    </ul>
  </section>;
}
