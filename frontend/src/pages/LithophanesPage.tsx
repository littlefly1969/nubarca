import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import {
  ApiError, cancelLithophane, createLithophane, deleteLithophane, getLithophanePreview,
  getLithophaneStatuses, listLithophanes, lithophaneDownloadUrl, previewLithophane,
  type LithophanePreview, type LithophaneSettings, type LithophaneSummary, type MediaItem,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { LithophanePhotoPicker } from '../lithophanes/LithophanePhotoPicker';
import { LithophaneSphere } from '../lithophanes/LithophaneSphere';
import { activeLithophane, DEFAULT_LITHOPHANE_SETTINGS, validLithophaneSettings } from '../lithophanes/lithophaneModel';
import '../lithophanes/lithophanes.css';

const FIELDS: { key: keyof LithophaneSettings; min: number; max: number; step: number; label: MessageKey }[] = [
  { key: 'diameterMm', min: 40, max: 200, step: 1, label: 'lithophane.diameter' },
  { key: 'openingMm', min: 4, max: 130, step: 1, label: 'lithophane.opening' },
  { key: 'minThicknessMm', min: 0.4, max: 2, step: 0.1, label: 'lithophane.minThickness' },
  { key: 'maxThicknessMm', min: 0.5, max: 5, step: 0.1, label: 'lithophane.maxThickness' },
  { key: 'resolutionMm', min: 0.15, max: 1, step: 0.05, label: 'lithophane.resolution' },
  { key: 'pictureWidthDegrees', min: 40, max: 300, step: 5, label: 'lithophane.pictureWidth' },
  { key: 'pictureHeightDegrees', min: 30, max: 140, step: 5, label: 'lithophane.pictureHeight' },
  { key: 'centerX', min: 0, max: 1, step: 0.05, label: 'lithophane.centerX' },
  { key: 'centerY', min: 0, max: 1, step: 0.05, label: 'lithophane.centerY' },
  { key: 'zoom', min: 0.05, max: 4, step: 0.05, label: 'lithophane.zoom' },
  { key: 'brightness', min: -0.5, max: 0.5, step: 0.05, label: 'lithophane.brightness' },
  { key: 'contrast', min: 0.25, max: 3, step: 0.05, label: 'lithophane.contrast' },
  { key: 'gamma', min: 0.3, max: 3, step: 0.05, label: 'lithophane.gamma' },
];

export function LithophanesPage() {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [photo, setPhoto] = useState<{ id: string; name: string } | null>(null);
  const [picking, setPicking] = useState(true);
  const [settings, setSettings] = useState<LithophaneSettings>({ ...DEFAULT_LITHOPHANE_SETTINGS });
  const [preview, setPreview] = useState<LithophanePreview | null>(null);
  const [items, setItems] = useState<LithophaneSummary[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [reload, setReload] = useState(0);
  const action = useRef<AbortController | null>(null);
  const request = useRef<{ fingerprint: string; id: string } | null>(null);
  const valid = validLithophaneSettings(settings);

  const onError = useCallback((error: unknown) => {
    if (error instanceof Error && error.name === 'AbortError') return;
    if (error instanceof ApiError && error.status === 401) { invalidateAuth(); return; }
    const code = error instanceof ApiError && typeof error.body === 'object' && error.body !== null
      && 'error' in error.body ? (error.body as { error: unknown }).error : null;
    setNotice(t(code === 'project_limit' ? 'lithophane.projectLimit'
      : code === 'image_too_large' ? 'lithophane.imageTooLarge'
      : error instanceof ApiError && error.status === 403 ? 'lithophane.forbidden'
      : code === 'source_unavailable' ? 'lithophane.sourceUnavailable'
      : error instanceof ApiError && error.status === 429 ? 'lithophane.rateLimit'
      : 'lithophane.error'));
  }, [invalidateAuth, t]);

  useEffect(() => {
    const ctrl = new AbortController();
    setLoading(true); setLoadFailed(false);
    listLithophanes(null, ctrl.signal).then((page) => {
      if (!ctrl.signal.aborted) { setItems(page.items); setCursor(page.nextCursor); }
    }).catch((e: unknown) => { if (!ctrl.signal.aborted) { onError(e); setLoadFailed(true); } })
      .finally(() => { if (!ctrl.signal.aborted) setLoading(false); });
    return () => ctrl.abort();
  }, [onError, reload]);
  useEffect(() => () => action.current?.abort(), []);

  const activeIds = items.filter((item) => activeLithophane(item.status)).map((item) => item.id).join(',');
  useEffect(() => {
    if (!activeIds) return;
    const ctrl = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    const poll = async () => {
      try {
        const fresh = await getLithophaneStatuses(activeIds.split(','), ctrl.signal);
        if (ctrl.signal.aborted) return;
        const byId = new Map(fresh.map((x) => [x.id, x]));
        const queried = new Set(activeIds.split(','));
        setItems((old) => old.filter((x) => !queried.has(x.id) || byId.has(x.id)).map((x) => byId.get(x.id) ?? x));
      } catch (e) {
        if (!ctrl.signal.aborted) onError(e);
        if (e instanceof ApiError && (e.status === 401 || e.status === 403)) ctrl.abort();
      }
      if (!ctrl.signal.aborted) timer = setTimeout(() => void poll(), 3000);
    };
    timer = setTimeout(() => void poll(), 1500);
    return () => { ctrl.abort(); clearTimeout(timer); };
  }, [activeIds, onError]);

  const choose = (p: MediaItem) => {
    action.current?.abort(); setBusy(false);
    setPhoto({ id: p.id, name: p.displayName || p.name }); setPicking(false); setPreview(null); setNotice(null);
  };
  const perform = async (work: (signal: AbortSignal) => Promise<void>) => {
    action.current?.abort();
    const ctrl = new AbortController(); action.current = ctrl;
    setBusy(true); setNotice(null);
    try { await work(ctrl.signal); } catch (e) { if (!ctrl.signal.aborted) onError(e); }
    finally { if (!ctrl.signal.aborted) setBusy(false); }
  };
  const generate = (e: FormEvent) => {
    e.preventDefault(); if (!photo?.id || !valid || busy) return;
    const fingerprint = JSON.stringify({ fileItemId: photo.id, settings });
    if (request.current?.fingerprint !== fingerprint) request.current = { fingerprint, id: crypto.randomUUID() };
    const requestId = request.current.id;
    void perform(async (signal) => {
      const created = await createLithophane(photo.id, settings, requestId, signal);
      if (signal.aborted) return;
      setItems((old) => [created, ...old.filter((x) => x.id !== created.id)]);
      setNotice(t('lithophane.created'));
      request.current = null;
    });
  };

  return <div className="lithophane-page">
    <h3>{t('lab.lithophanes')}</h3>
    <p>{t('lithophane.intro')}</p>
    {notice && <p role="status" className="lithophane-notice">{notice}</p>}
    {picking && <LithophanePhotoPicker selected={photo?.id ?? null} onSelect={choose} onError={onError} />}
    {photo && <div className="lithophane-editor">
      <form onSubmit={generate}>
        <div className="lithophane-photo-heading"><h4>{photo.name}</h4>
          <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => setPicking((v) => !v)}>{t('lithophane.changePhoto')}</button>
        </div>
        <fieldset disabled={busy}>
          <legend>{t('lithophane.parameters')}</legend>
          <div className="lithophane-fields">{FIELDS.map((field) => <label key={field.key}>{t(field.label)}
            <input type="number" min={field.min} max={field.max} step={field.step} value={settings[field.key]}
              onChange={(e) => {
                setSettings((old) => ({ ...old, [field.key]: Number(e.target.value) })); setPreview(null);
              }} required />
          </label>)}</div>
        </fieldset>
        {!valid && <p role="alert">{t('lithophane.invalid')}</p>}
        <div className="lithophane-actions">
          <button type="button" className="btn btn-secondary" disabled={!valid || busy} onClick={() => void perform(async (signal) => {
            const result = await previewLithophane(photo.id, settings, signal); if (!signal.aborted) setPreview(result);
          })}>{t('lithophane.preview')}</button>
          <button type="submit" className="btn btn-primary" disabled={!valid || busy || !photo.id}>{t('lithophane.generate')}</button>
          <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => {
            setSettings({ ...DEFAULT_LITHOPHANE_SETTINGS }); setPreview(null);
          }}>{t('lithophane.reset')}</button>
        </div>
        {busy && <p role="status">{t('common.loading')}</p>}
      </form>
      <div>{preview ? <><LithophaneSphere preview={preview} /><p>{t('lithophane.estimate', {
        triangles: preview.triangleCount.toLocaleString(), mb: (preview.sizeBytes / 1_000_000).toFixed(1),
      })}</p></> : <p className="lithophane-preview-placeholder">{t('lithophane.previewPrompt')}</p>}</div>
    </div>}

    <section aria-label={t('lithophane.history')} className="lithophane-history">
      <h4>{t('lithophane.history')}</h4>
      {loading && <p role="status">{t('common.loading')}</p>}
      {loadFailed && <button className="btn btn-secondary" onClick={() => setReload((v) => v + 1)}>{t('common.retry')}</button>}
      {!loading && !loadFailed && items.length === 0 && <p>{t('lithophane.empty')}</p>}
      <ul>{items.map((item) => <li key={item.id}>
        <div><strong>{item.name}</strong><p>{item.settings.diameterMm} mm · {(item.sizeBytes / 1_000_000).toFixed(1)} MB</p>
          <p>{t(`lithophane.status.${item.status}`)}</p>
          {activeLithophane(item.status) && <progress value={item.completedRows} max={item.totalRows}
            aria-label={t('lithophane.progress')} />}
        </div>
        <div className="lithophane-actions">
          {item.downloadReady && <a className="btn btn-primary" href={lithophaneDownloadUrl(item.id)} download>{t('lithophane.download')}</a>}
          {(item.downloadReady || item.completedRows > 0) && <button type="button" className="btn btn-secondary" disabled={busy}
            onClick={() => void perform(async (signal) => {
              const result = await getLithophanePreview(item.id, signal);
              if (signal.aborted) return;
              setSettings(item.settings); setPhoto({ id: item.fileItemId ?? '', name: item.name });
              setPicking(false); setPreview(result);
            })}>{t('lithophane.view')}</button>}
          {(item.status === 'queued' || item.status === 'running') && <button type="button" className="btn btn-secondary" disabled={busy}
            onClick={() => void perform(async (signal) => {
              await cancelLithophane(item.id, signal);
              if (!signal.aborted) setItems((old) => old.map((x) => x.id === item.id ? { ...x, status: 'cancelling' } : x));
            })}>{t('lithophane.cancel')}</button>}
          <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => {
            if (!window.confirm(t('lithophane.confirmDelete'))) return;
            void perform(async (signal) => {
              await deleteLithophane(item.id, signal);
              if (!signal.aborted) setItems((old) => old.filter((x) => x.id !== item.id));
            });
          }}>{t('lithophane.delete')}</button>
        </div>
      </li>)}</ul>
      {cursor && <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => void perform(async (signal) => {
        const page = await listLithophanes(cursor, signal);
        if (!signal.aborted) {
          setItems((old) => [...old, ...page.items.filter((x) => !old.some((p) => p.id === x.id))]); setCursor(page.nextCursor);
        }
      })}>{t('lithophane.moreProjects')}</button>}
    </section>
    <p className="lithophane-print-hint">{t('lithophane.printHint')}</p>
  </div>;
}
