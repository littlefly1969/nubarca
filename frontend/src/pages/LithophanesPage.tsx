import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import {
  ApiError, cancelLithophane, createLithophane, deleteLithophane, getLithophanePreview,
  getLithophaneStatuses, listLithophanes, lithophaneDownloadUrl, lithophaneCapUrl, previewLithophane,
  type LithophanePreview, type LithophaneQueueEntry, type LithophaneSettings, type LithophaneSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { LithophaneQueue, useLithophaneQueue } from '../lithophanes/LithophaneQueue';
import { LithophaneSphere } from '../lithophanes/LithophaneSphere';
import { LithophanePhotoEditor } from '../lithophanes/LithophanePhotoEditor';
import {
  activeLithophane, DEFAULT_LITHOPHANE_SETTINGS, DEFAULT_LITHOPHANE_TEXT_HEIGHT, hasLithophaneText,
  LITHOPHANE_TEXT_MAX_LENGTH, lithophaneRequestSettings, suggestedLithophaneResolution, validLithophaneSettings,
  validLithophaneText,
} from '../lithophanes/lithophaneModel';
import '../lithophanes/lithophanes.css';
import { TextFontPicker } from '../text/TextFontPicker';
import { textFont } from '../text/textFonts';

type NumericField = 'diameterMm' | 'openingMm' | 'minThicknessMm' | 'maxThicknessMm' | 'backgroundThicknessMm' | 'resolutionMm' | 'brightness' | 'contrast' | 'gamma';
const FIELDS: { key: NumericField; min: number; max: number; step: number; label: MessageKey }[] = [
  { key: 'diameterMm', min: 40, max: 200, step: 1, label: 'lithophane.diameter' },
  { key: 'openingMm', min: 4, max: 130, step: 1, label: 'lithophane.opening' },
  { key: 'minThicknessMm', min: 0.4, max: 2, step: 0.1, label: 'lithophane.minThickness' },
  { key: 'maxThicknessMm', min: 0.5, max: 5, step: 0.1, label: 'lithophane.maxThickness' },
  { key: 'backgroundThicknessMm', min: 0.4, max: 2, step: 0.1, label: 'lithophane.backgroundThickness' },
  { key: 'resolutionMm', min: 0.15, max: 1, step: 0.05, label: 'lithophane.resolution' },
  { key: 'brightness', min: -0.5, max: 0.5, step: 0.05, label: 'lithophane.brightness' },
  { key: 'contrast', min: 0.25, max: 3, step: 0.05, label: 'lithophane.contrast' },
  { key: 'gamma', min: 0.3, max: 3, step: 0.05, label: 'lithophane.gamma' },
];
// A lithophane of words only has no photograph to place or tone.
const SHAPE_FIELDS = new Set<keyof LithophaneSettings>([
  'diameterMm', 'openingMm', 'minThicknessMm', 'maxThicknessMm', 'backgroundThicknessMm', 'resolutionMm',
]);
const TEXT_POSITIONS: { value: NonNullable<LithophaneSettings['textPosition']>; label: MessageKey }[] = [
  { value: 'below', label: 'lithophane.textBelow' },
  { value: 'above', label: 'lithophane.textAbove' },
  { value: 'onPhoto', label: 'lithophane.textOnPhoto' },
];

export function LithophanesPage() {
  const { t, formatNumber } = useI18n();
  const { invalidateAuth } = useAuth();
  // id null: a lithophane of words only. '' is a saved project whose photo is gone.
  // The editor is always open: until a photo is chosen it makes a lithophane of words only.
  const [photo, setPhoto] = useState<{ id: string | null; name: string }>({ id: null, name: '' });
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
  const textOnly = photo.id === null;
  const textMissing = textOnly && !hasLithophaneText(settings);
  const valid = validLithophaneSettings(settings) && !textMissing;
  const suggestedResolution = suggestedLithophaneResolution(settings);
  // The photo is already whole on the sphere below this zoom: lower values
  // change the number, not the map. Known only after a fresh preview.
  const minimumZoom = preview?.minimumZoom != null && settings.zoom < preview.minimumZoom - 0.005
    ? Math.ceil(preview.minimumZoom * 100) / 100 : null;
  // The suggested value must sit on the field's 0.05 step grid, or the
  // browser's own form validation would silently refuse to submit.
  const zoomFix = minimumZoom === null ? null : Math.round(Math.ceil(minimumZoom / 0.05 - 1e-9) * 5) / 100;

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

  const queue = useLithophaneQueue(onError);
  const choose = (p: LithophaneQueueEntry) => {
    action.current?.abort(); setBusy(false);
    setPhoto({ id: p.fileItemId, name: p.name }); setPreview(null); setNotice(null);
    setSettings((old) => ({ ...old, centerX: 0.5, centerY: 0.5, zoom: 1 }));
  };
  const perform = async (work: (signal: AbortSignal) => Promise<void>) => {
    action.current?.abort();
    const ctrl = new AbortController(); action.current = ctrl;
    setBusy(true); setNotice(null);
    try { await work(ctrl.signal); } catch (e) { if (!ctrl.signal.aborted) onError(e); }
    finally { if (!ctrl.signal.aborted) setBusy(false); }
  };
  const generate = (e: FormEvent) => {
    e.preventDefault(); if (photo.id === '' || !valid || busy) return;
    const sent = lithophaneRequestSettings(settings);
    const fingerprint = JSON.stringify({ fileItemId: photo.id, settings: sent });
    if (request.current?.fingerprint !== fingerprint) request.current = { fingerprint, id: crypto.randomUUID() };
    const requestId = request.current.id;
    void perform(async (signal) => {
      const created = await createLithophane(photo.id, sent, requestId, signal);
      if (signal.aborted) return;
      setItems((old) => [created, ...old.filter((x) => x.id !== created.id)]);
      // The server ended this photo's wait when it accepted the generation.
      if (photo.id) queue.forget(photo.id);
      setNotice(t('lithophane.created'));
      request.current = null;
    });
  };

  return <div className="lithophane-page">
    <h3>{t('lab.lithophanes')}</h3>
    <p>{t('lithophane.intro')}</p>
    {notice && <p role="status" className="lithophane-notice">{notice}</p>}
    <LithophaneQueue queue={queue} selected={photo.id} disabled={busy} onSelect={choose} />
    <p className="lithophane-text-only">
      <button type="button" className="btn btn-secondary" disabled={busy} aria-pressed={textOnly} onClick={() => {
        action.current?.abort(); setBusy(false);
        setPhoto({ id: null, name: '' }); setPreview(null); setNotice(null);
      }}>{t('lithophane.textOnlyStart')}</button>
    </p>
    <div className="lithophane-editor">
      <form onSubmit={generate}>
        <div className="lithophane-photo-heading"><h4>{photo.name || t('lithophane.textOnlyTitle')}</h4></div>
        {textOnly && <p className="lithophane-hint">{t('lithophane.textOnlyHint')}</p>}
        {photo.id && <LithophanePhotoEditor key={photo.id} fileId={photo.id} settings={settings} disabled={busy}
          onChange={(s) => { setSettings(s); setPreview(null); }} />}
        <fieldset disabled={busy}>
          <legend>{t('lithophane.parameters')}</legend>
          <label>{t('lithophane.useMode')}
            <select value={settings.hanging ? 'hanging' : 'standing'} onChange={(e) => {
              const hanging = e.target.value === 'hanging';
              setSettings((old) => ({ ...old, hanging, internalFit: hanging, printBrim: hanging, printAnchor: false,
                ...(hanging ? { externalRelief: true,
                  openingMm: Math.min(old.diameterMm * 0.65, Math.max(30, old.openingMm)),
                  backgroundThicknessMm: Math.max(0.8, old.backgroundThicknessMm ?? old.minThicknessMm) } : {}),
              })); setPreview(null);
            }}>
              <option value="standing">{t('lithophane.standing')}</option>
              <option value="hanging">{t('lithophane.hanging')}</option>
            </select>
          </label>
          <div className="lithophane-fields">{FIELDS.filter((field) => !textOnly || SHAPE_FIELDS.has(field.key)).map((field) => <label key={field.key}>{t(field.label)}
            <input type="number" min={field.key === 'openingMm' && settings.hanging ? 16 : field.key === 'backgroundThicknessMm' && settings.hanging ? 0.8 : field.min}
              max={field.key === 'openingMm' ? settings.diameterMm * 0.65 : field.max} step={field.step} value={settings[field.key] ?? ''}
              onChange={(e) => {
                setSettings((old) => ({ ...old, [field.key]: Number(e.target.value) })); setPreview(null);
              }} required />
          </label>)}</div>
          {!settings.hanging && <><label className="lithophane-check"><input type="checkbox" checked={settings.baseCollar ?? false}
            onChange={(e) => {
              setSettings((old) => ({ ...old, baseCollar: e.target.checked })); setPreview(null);
            }} />{t('lithophane.baseCollar')}</label>
          <p className="lithophane-hint">{t('lithophane.baseCollarHelp')}</p>
          <label className="lithophane-check"><input type="checkbox" checked={settings.externalRelief ?? false}
            onChange={(e) => { setSettings((old) => ({ ...old, externalRelief: e.target.checked })); setPreview(null); }} />
            {t('lithophane.externalRelief')}</label></>}
          <p className="lithophane-hint">{t('lithophane.reliefHelp')}</p>
          {settings.hanging && <><label className="lithophane-check"><input type="checkbox" checked={settings.internalFit ?? false}
            onChange={(e) => {
              setSettings((old) => ({ ...old, internalFit: e.target.checked, printAnchor: false, printBrim: e.target.checked }));
              setPreview(null);
            }} />{t('lithophane.internalFit')}</label>
            {settings.internalFit ? <><p className="lithophane-hint">{t('lithophane.internalFitHelp')}</p>
              <label className="lithophane-check"><input type="checkbox" checked={settings.printBrim ?? false}
              onChange={(e) => { setSettings((old) => ({ ...old, printBrim: e.target.checked })); setPreview(null); }} />
              {t('lithophane.printBrim')}</label>
              <p className="lithophane-hint">{t('lithophane.printBrimHelp')}</p></>
              : <><label className="lithophane-check"><input type="checkbox" checked={settings.printAnchor ?? false}
                onChange={(e) => { setSettings((old) => ({ ...old, printAnchor: e.target.checked })); setPreview(null); }} />
                {t('lithophane.printAnchor')}</label>
                <p className="lithophane-hint">{t('lithophane.hangingHelp')}</p></>}
          </>}
        </fieldset>
        <fieldset disabled={busy} className="lithophane-text">
          <legend>{t('lithophane.textSection')}</legend>
          <label>{t('lithophane.text')}
            <textarea rows={2} maxLength={LITHOPHANE_TEXT_MAX_LENGTH} value={settings.text ?? ''}
              style={{ fontFamily: textFont(settings.textFont).family, fontWeight: textFont(settings.textFont).weight }}
              aria-invalid={!validLithophaneText(settings.text) || textMissing} data-testid="lithophane-text"
              aria-describedby="lithophane-text-hint"
              onChange={(e) => { setSettings((old) => ({ ...old, text: e.target.value })); setPreview(null); }} />
          </label>
          <span id="lithophane-text-hint"
            className={validLithophaneText(settings.text) ? 'lithophane-hint' : 'lithophane-hint lithophane-hint--error'}>
            {t(textMissing ? 'lithophane.textRequired' : 'lithophane.textHelp', { max: LITHOPHANE_TEXT_MAX_LENGTH })}
          </span>
          <TextFontPicker name="lithophane-font" value={textFont(settings.textFont).id} disabled={busy}
            onChange={(font) => { setSettings((old) => ({ ...old, textFont: font })); setPreview(null); }} />
          <div className="lithophane-fields">
            <label>{t('lithophane.textHeight')}
              <input type="number" min={3} max={30} step={0.5} value={settings.textHeightMm ?? DEFAULT_LITHOPHANE_TEXT_HEIGHT}
                onChange={(e) => { setSettings((old) => ({ ...old, textHeightMm: Number(e.target.value) })); setPreview(null); }} required />
            </label>
            {!textOnly && <label>{t('lithophane.textPosition')}
              <select value={settings.textPosition ?? 'below'} onChange={(e) => {
                setSettings((old) => ({ ...old, textPosition: e.target.value as LithophaneSettings['textPosition'] })); setPreview(null);
              }}>{TEXT_POSITIONS.map((p) => <option key={p.value} value={p.value}>{t(p.label)}</option>)}</select>
            </label>}
          </div>
        </fieldset>
        {!valid && !textMissing && (suggestedResolution !== null
          ? <div role="alert" className="lithophane-too-detailed">
            <p>{t('lithophane.tooDetailed', { mm: formatNumber(suggestedResolution) })}</p>
            <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => {
              setSettings((old) => ({ ...old, resolutionMm: suggestedResolution })); setPreview(null);
            }}>{t('lithophane.useResolution', { mm: formatNumber(suggestedResolution) })}</button>
          </div>
          : <p role="alert">{t('lithophane.invalid')}</p>)}
        <div className="lithophane-actions">
          <button type="button" className="btn btn-secondary" disabled={!valid || busy} onClick={() => void perform(async (signal) => {
            const result = await previewLithophane(photo.id, lithophaneRequestSettings(settings), signal);
            if (!signal.aborted) setPreview(result);
          })}>{t('lithophane.preview')}</button>
          <button type="submit" className="btn btn-primary" disabled={!valid || busy || photo.id === ''}>{t('lithophane.generate')}</button>
          <button type="button" className="btn btn-secondary" disabled={busy} onClick={() => {
            setSettings({ ...DEFAULT_LITHOPHANE_SETTINGS }); setPreview(null);
          }}>{t('lithophane.reset')}</button>
        </div>
        {busy && <p role="status">{t('common.loading')}</p>}
      </form>
      <div>{preview ? <><LithophaneSphere preview={preview} /><p>{t('lithophane.estimate', {
        triangles: formatNumber(preview.triangleCount),
        mb: formatNumber(preview.sizeBytes / 1_000_000, { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
      })}</p>{minimumZoom !== null && <p className="lithophane-zoom-hint">
        {t('lithophane.zoomBelowMinimum', { zoom: formatNumber(minimumZoom) })}{' '}
        <button type="button" className="btn btn-secondary" disabled={busy}
          onClick={() => setSettings((old) => ({ ...old, zoom: zoomFix! }))}>
          {t('lithophane.useZoom', { zoom: formatNumber(zoomFix!) })}</button>
      </p>}</> : <p className="lithophane-preview-placeholder">{t('lithophane.previewPrompt')}</p>}</div>
    </div>

    <section aria-label={t('lithophane.history')} className="lithophane-history">
      <h4>{t('lithophane.history')}</h4>
      {loading && <p role="status">{t('common.loading')}</p>}
      {loadFailed && <button className="btn btn-secondary" onClick={() => setReload((v) => v + 1)}>{t('common.retry')}</button>}
      {!loading && !loadFailed && items.length === 0 && <p>{t('lithophane.empty')}</p>}
      <ul>{items.map((item) => <li key={item.id}>
        <div><strong>{item.name}</strong><p>{formatNumber(item.settings.diameterMm)} mm · {formatNumber(item.sizeBytes / 1_000_000, {
          minimumFractionDigits: 1, maximumFractionDigits: 1,
        })} MB</p>
          <p>{t(`lithophane.status.${item.status}`)}</p>
          {activeLithophane(item.status) && <progress value={item.completedRows} max={item.totalRows}
            aria-label={t('lithophane.progress')} />}
        </div>
        <div className="lithophane-actions">
          {item.downloadReady && <a className="btn btn-primary" href={lithophaneDownloadUrl(item.id)} download>{t('lithophane.download')}</a>}
          {item.downloadReady && item.settings.hanging && <a className="btn btn-secondary" href={lithophaneCapUrl(item.id)} download>{t('lithophane.downloadCap')}</a>}
          {(item.downloadReady || item.completedRows > 0) && <button type="button" className="btn btn-secondary" disabled={busy}
            onClick={() => void perform(async (signal) => {
              const result = await getLithophanePreview(item.id, signal);
              if (signal.aborted) return;
              // Projects saved before the background wall existed used the photo's thinnest wall there.
              setSettings({ ...item.settings, backgroundThicknessMm: item.settings.backgroundThicknessMm ?? item.settings.minThicknessMm });
              // No photograph but words: a lithophane of words only.
              setPhoto({ id: item.fileItemId ?? (hasLithophaneText(item.settings) ? null : ''), name: item.name });
              setPreview(result);
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
