import { useEffect, useRef, useState } from 'react';
import { ApiError, getPhotoText, savePhotoText } from '@nubarca/api-client';
import { DEFAULT_PHOTO_TEXT, type PhotoTextLayout } from '@nubarca/contracts';
import { useI18n, type MessageKey } from '../i18n';
import { PhotoTextControls } from './PhotoTextControls';
import { PhotoTextOverlay } from './PhotoTextOverlay';
import './albumPhotoText.css';

/** One editor for the uploader's party view and the organizer's album. */
export function AlbumPhotoTextEditor({ endpoint, previewUrl, onClose, onSaved }: {
  endpoint: string; previewUrl: string; onClose(): void; onSaved(): void;
}) {
  const { t } = useI18n();
  const root = useRef<HTMLDivElement>(null);
  const [draft, setDraft] = useState<{ text: string; layout: PhotoTextLayout; version: number } | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const load = (signal?: AbortSignal) => {
    setError(null); setDraft(null);
    getPhotoText(endpoint, signal).then((data) => {
      if (!signal?.aborted) setDraft({ text: data.text ?? '', layout: data.layout ?? { ...DEFAULT_PHOTO_TEXT, mode: 'bubble' }, version: data.version });
    }).catch(() => { if (!signal?.aborted) setError('photoEdit.loadError'); });
  };
  useEffect(() => {
    const ctrl = new AbortController(); load(ctrl.signal);
    const opener = document.activeElement as HTMLElement | null;
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden'; root.current?.focus();
    return () => { ctrl.abort(); document.body.style.overflow = overflow; if (opener?.isConnected) opener.focus(); };
  }, [endpoint]);
  const submit = async (remove = false) => {
    if (!draft || busy) return;
    setBusy(true); setError(null);
    try {
      await savePhotoText(endpoint, { text: remove ? null : draft.text, layout: draft.layout, expectedVersion: draft.version });
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError && err.status === 409 ? 'photoEdit.conflict' : 'photoEdit.saveError');
    } finally { setBusy(false); }
  };
  return <div className="album-photo-editor" role="dialog" aria-modal="true" aria-label={t('photoEdit.title')}
    ref={root} tabIndex={-1} onKeyDown={(event) => {
      if (event.key === 'Escape' && !busy) { event.stopPropagation(); onClose(); }
      if (event.key === 'Tab') {
        const nodes = [...(root.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled), [tabindex]:not([tabindex="-1"])') ?? [])];
        const first = nodes[0]; const last = nodes[nodes.length - 1];
        if (event.shiftKey && (document.activeElement === first || document.activeElement === root.current)) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
      }
    }}>
    <div className="album-photo-editor-shell">
      <h2>{t('photoEdit.title')}</h2>
      <p>{t('photoEdit.sharedHelp')}</p>
      {draft ? <>
        <div className="album-photo-editor-preview">
          <img src={`${previewUrl}${previewUrl.includes('?') ? '&' : '?'}original=true`} alt="" />
          {draft.text.trim() && <PhotoTextOverlay text={draft.text} layout={draft.layout}
            onChange={(layout) => setDraft({ ...draft, layout })} />}
        </div>
        <label>{t('photoEdit.text')}
          <textarea value={draft.text} maxLength={300} onChange={(e) => setDraft({ ...draft, text: e.target.value })} />
        </label>
        <PhotoTextControls allowBelow={false} value={draft.layout} onChange={(layout) => setDraft({ ...draft, layout })} />
      </> : !error && <p role="status">{t('common.loading')}</p>}
      {error && <p role="alert">{t(error)}</p>}
      {error && <button type="button" disabled={busy} onClick={() => load()}>{t('photoEdit.reload')}</button>}
      <div className="album-photo-editor-actions">
        <button type="button" disabled={busy} onClick={onClose}>{t('common.cancel')}</button>
        <button type="button" disabled={busy || !draft} onClick={() => void submit(true)}>{t('photoEdit.remove')}</button>
        <button type="button" disabled={busy || !draft || /[\uD800-\uDFFF]/.test(draft.text)} onClick={() => void submit()}>{t('photoEdit.save')}</button>
      </div>
    </div>
  </div>;
}
