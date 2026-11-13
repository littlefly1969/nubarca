import { useEffect, useState } from 'react';
import { listMedia, type MediaItem, type MediaListResponse } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// Uses the owner gallery API and its thumbnails/cursors; never downloads originals.
export function LithophanePhotoPicker({ selected, onSelect, onError }: {
  selected: string | null; onSelect(photo: MediaItem): void; onError(error: unknown): void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState('');
  const [query, setQuery] = useState('');
  const [cursor, setCursor] = useState<string | null>(null);
  const [result, setResult] = useState<MediaListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    const ctrl = new AbortController();
    setLoading(true); setFailed(false);
    listMedia({ kind: 'image', q: query || undefined, limit: 24, cursor }, ctrl.signal).then((page) => {
      if (ctrl.signal.aborted) return;
      setResult((old) => ({ ...page, items: cursor ? [...(old?.items ?? []), ...page.items] : page.items }));
    }).catch((error: unknown) => {
      if (!ctrl.signal.aborted) { setFailed(true); onError(error); }
    }).finally(() => { if (!ctrl.signal.aborted) setLoading(false); });
    return () => ctrl.abort();
  }, [cursor, onError, query, retry]);
  return <section aria-label={t('lithophane.choosePhoto')}>
    <form className="lithophane-search" onSubmit={(e) => {
      e.preventDefault(); setResult(null); setCursor(null); setQuery(draft.trim()); setRetry((v) => v + 1);
    }}>
      <label>{t('lithophane.searchPhoto')}<input value={draft} onChange={(e) => setDraft(e.target.value)} type="search" /></label>
      <button type="submit" className="btn btn-secondary">{t('lithophane.search')}</button>
    </form>
    {result?.items.length === 0 && !loading && <p>{t('lithophane.noPhotos')}</p>}
    <ul className="lithophane-photos">
      {result?.items.map((photo) => <li key={photo.id}><button type="button" aria-pressed={selected === photo.id}
        onClick={() => onSelect(photo)} aria-label={photo.displayName || photo.name}>
        <img src={photo.thumbnailUrl} alt="" loading="lazy" decoding="async" />
        <span>{photo.displayName || photo.name}</span>
      </button></li>)}
    </ul>
    {loading && <p role="status">{t('common.loading')}</p>}
    {failed && <button type="button" className="btn btn-secondary" onClick={() => setRetry((v) => v + 1)}>{t('common.retry')}</button>}
    {!loading && result?.nextCursor && <button type="button" className="btn btn-secondary"
      onClick={() => setCursor(result.nextCursor)}>{t('lithophane.morePhotos')}</button>}
  </section>;
}
