import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import QRCode from 'qrcode';
import {
  cancelTvBeautyLabRun,
  createTvBeautyLabUploadSession,
  fetchTvBeautyLabMediaObjectUrl,
  getTvBeautyLabItem,
  getTvBeautyLabUploadSession,
  listTvBeautyLabItems,
  removeTvBeautyLabItem,
  requestTvBeautyLabAnalysis,
  retryTvBeautyLabRun,
  revokeTvBeautyLabUploadSession,
  type TvBeautyLabItem,
  type TvBeautyLabItemDetail,
  type TvBeautyLabMetric,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';

// TV "Beauty Lab" (Laboratorio bellezza): the grant-gated Aesthetics Lab on the
// TV. Browser /tv fallback (the native Fire TV app mirrors this). Reuses the web
// lab's exact capabilities through the TV projection API — list, detail with all
// 12 localized metrics, multi-select, start analysis, compare, cancel, retry,
// remove, and QR mobile upload — never an owner API and never an original image.
//
// Grant lifecycle: the ONE 15s re-validation timer (shared personal-grant TTL)
// runs while this screen is open; a 401 returns to pairing, a 403 locks. Derived
// media is fetched with the grant header into object URLs, revoked on every
// transition (detail close, refresh, removal, unmount, lock, invalidation).

const GRANT_REVALIDATE_MS = 15_000;
// Transient poll (only while the QR screen is open) of that one upload session.
const QR_POLL_MS = 4_000;
const GRID_COLUMNS = 4;
const METRIC_GROUPS = ['face', 'appearance', 'environment', 'overall'] as const;

type View =
  | { kind: 'grid' }
  | { kind: 'detail'; id: string }
  | { kind: 'compare' }
  | { kind: 'qr' };

function metricLabel(t: (k: MessageKey) => string, key: string): string {
  const mk = `aesthetics.metric.${key}` as MessageKey;
  const label = t(mk);
  return label === mk ? key : label;
}

function statusLabel(t: (k: MessageKey) => string, status: string | null): string {
  if (!status) return t('tv.beautyLab.notAnalyzed');
  const mk = `aesthetics.status.${status}` as MessageKey;
  const label = t(mk);
  return label === mk ? status : label;
}

export function TvBeautyLab({
  grant,
  onBack,
  onPersonalError,
}: {
  grant: string;
  onBack: () => void;
  onPersonalError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [items, setItems] = useState<TvBeautyLabItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<View>({ kind: 'grid' });
  const [selectionMode, setSelectionMode] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [menuOpen, setMenuOpen] = useState(false);
  const [focusIndex, setFocusIndex] = useState(0);
  const [notice, setNotice] = useState<string | null>(null);

  // Thumbnail object URLs keyed by item id; revoked when items change/unmount.
  const thumbUrls = useRef<Map<string, string>>(new Map());
  const tileRefs = useRef<(HTMLButtonElement | null)[]>([]);
  // Remember the grid focus so it can be restored after closing an overlay.
  const gridFocusRef = useRef(0);

  const revokeAllThumbs = useCallback(() => {
    for (const url of thumbUrls.current.values()) URL.revokeObjectURL(url);
    thumbUrls.current.clear();
  }, []);

  const load = useCallback(
    async (cursor: string | null, append: boolean) => {
      setLoading(true);
      try {
        const page = await listTvBeautyLabItems(grant, cursor, 50);
        setItems((prev) => (append ? [...prev, ...page.items] : page.items));
        setNextCursor(page.nextCursor);
        setError(null);
      } catch (err) {
        if (onPersonalError(err)) return;
        setError(t('tv.beautyLab.loadError'));
      } finally {
        setLoading(false);
      }
    },
    [grant, onPersonalError, t],
  );

  // Initial load + cleanup of every object URL on unmount.
  useEffect(() => {
    void load(null, false);
    return () => revokeAllThumbs();
  }, [load, revokeAllThumbs]);

  // Fetch thumbnails for items that don't have an object URL yet.
  useEffect(() => {
    let cancelled = false;
    for (const item of items) {
      if (thumbUrls.current.has(item.id)) continue;
      thumbUrls.current.set(item.id, ''); // reserve to avoid duplicate fetches
      fetchTvBeautyLabMediaObjectUrl(grant, item.thumbnailUrl)
        .then((url) => {
          if (cancelled) {
            URL.revokeObjectURL(url);
            return;
          }
          thumbUrls.current.set(item.id, url);
          // Force a re-render so the tile picks up the resolved URL.
          setItems((prev) => [...prev]);
        })
        .catch((err) => {
          onPersonalError(err);
          thumbUrls.current.delete(item.id);
        });
    }
    return () => {
      cancelled = true;
    };
  }, [items, grant, onPersonalError]);

  // The ONE grant re-validation timer (also refreshes live job statuses).
  useEffect(() => {
    const timer = window.setInterval(() => {
      listTvBeautyLabItems(grant, null, 50)
        .then((page) => {
          setItems((prev) => {
            // Drop thumbnails for items that disappeared.
            const keep = new Set(page.items.map((i) => i.id));
            for (const [id, url] of thumbUrls.current) {
              if (!keep.has(id)) {
                if (url) URL.revokeObjectURL(url);
                thumbUrls.current.delete(id);
              }
            }
            void prev;
            return page.items;
          });
          setNextCursor(page.nextCursor);
        })
        .catch((err) => onPersonalError(err));
    }, GRANT_REVALIDATE_MS);
    return () => window.clearInterval(timer);
  }, [grant, onPersonalError]);

  const refreshGrid = useCallback(() => {
    void load(null, false);
  }, [load]);

  const toggleSelect = useCallback((id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const openDetail = useCallback((id: string) => {
    gridFocusRef.current = focusIndex;
    setMenuOpen(false);
    setView({ kind: 'detail', id });
  }, [focusIndex]);

  const closeOverlay = useCallback(() => {
    setMenuOpen(false);
    setView({ kind: 'grid' });
    // Restore focus to the previously focused tile.
    setFocusIndex(gridFocusRef.current);
    window.setTimeout(() => tileRefs.current[gridFocusRef.current]?.focus(), 0);
  }, []);

  const startAnalysis = useCallback(async () => {
    const ids = [...selected];
    if (ids.length === 0) return;
    setMenuOpen(false);
    try {
      const res = await requestTvBeautyLabAnalysis(grant, ids);
      setNotice(
        t('tv.beautyLab.analysisRequested', {
          enqueued: String(res.enqueued.length),
          skipped: String(res.skipped.length),
        }),
      );
      refreshGrid();
    } catch (err) {
      if (onPersonalError(err)) return;
      setNotice(t('tv.beautyLab.analysisError'));
    }
  }, [selected, grant, t, refreshGrid, onPersonalError]);

  const cancelSelected = useCallback(async () => {
    setMenuOpen(false);
    const runIds = items
      .filter((i) => selected.has(i.id) && (i.latestRunStatus === 'queued' || i.latestRunStatus === 'running'))
      .map((i) => i.id);
    // Cancel keys on the RUN; the grid item id isn't the run id, so cancel via
    // the item's detail. Keep it simple: cancel is applied through detail view.
    void runIds;
    // Fetch each selected item's latest run and cancel if cancellable.
    for (const id of selected) {
      try {
        const detail = await getTvBeautyLabItem(grant, id);
        const run = detail.latestRun;
        if (run && (run.status === 'queued' || run.status === 'running')) {
          await cancelTvBeautyLabRun(grant, run.id);
        }
      } catch (err) {
        if (onPersonalError(err)) return;
      }
    }
    refreshGrid();
  }, [items, selected, grant, refreshGrid, onPersonalError]);

  const retrySelected = useCallback(async () => {
    setMenuOpen(false);
    for (const id of selected) {
      try {
        const detail = await getTvBeautyLabItem(grant, id);
        const run = detail.latestRun;
        if (run && (run.status === 'failed' || run.status === 'cancelled')) {
          await retryTvBeautyLabRun(grant, run.id);
        }
      } catch (err) {
        if (onPersonalError(err)) return;
      }
    }
    refreshGrid();
  }, [selected, grant, refreshGrid, onPersonalError]);

  const removeSelected = useCallback(async () => {
    setMenuOpen(false);
    const ids = [...selected];
    if (ids.length === 0) return;
    const first = items.find((i) => i.id === ids[0]);
    if (!window.confirm(t('tv.beautyLab.confirmRemove', { name: first?.originalFileName ?? '' }))) return;
    for (const id of ids) {
      try {
        await removeTvBeautyLabItem(grant, id);
        const url = thumbUrls.current.get(id);
        if (url) URL.revokeObjectURL(url);
        thumbUrls.current.delete(id);
      } catch (err) {
        if (onPersonalError(err)) return;
      }
    }
    setSelected(new Set());
    setSelectionMode(false);
    refreshGrid();
  }, [selected, items, grant, t, refreshGrid, onPersonalError]);

  // Container-level remote handling: MENU toggles the action overlay; BACK
  // closes the deepest layer first, then exits selection, then locks at root.
  const onGridKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Backspace' || e.key === 'Escape') {
      e.preventDefault();
      if (menuOpen) { setMenuOpen(false); return; }
      if (selectionMode) { setSelectionMode(false); setSelected(new Set()); return; }
      onBack();
      return;
    }
    if (e.key === 'm' || e.key === 'ContextMenu') {
      e.preventDefault();
      setMenuOpen((o) => !o);
      return;
    }
    // D-pad grid navigation.
    const move = (delta: number) => {
      e.preventDefault();
      setFocusIndex((cur) => {
        const next = Math.max(0, Math.min(items.length - 1, cur + delta));
        window.setTimeout(() => tileRefs.current[next]?.focus(), 0);
        return next;
      });
    };
    if (e.key === 'ArrowRight') move(1);
    else if (e.key === 'ArrowLeft') move(-1);
    else if (e.key === 'ArrowDown') move(GRID_COLUMNS);
    else if (e.key === 'ArrowUp') move(-GRID_COLUMNS);
  };

  if (view.kind === 'detail') {
    return (
      <TvBeautyLabDetail
        grant={grant}
        id={view.id}
        onClose={closeOverlay}
        onPersonalError={onPersonalError}
        onChanged={refreshGrid}
      />
    );
  }

  if (view.kind === 'compare') {
    return (
      <TvBeautyLabCompare
        grant={grant}
        ids={[...selected]}
        onClose={closeOverlay}
        onPersonalError={onPersonalError}
      />
    );
  }

  if (view.kind === 'qr') {
    return (
      <TvBeautyLabQr
        grant={grant}
        onClose={() => { closeOverlay(); refreshGrid(); }}
        onPersonalError={onPersonalError}
      />
    );
  }

  const menuActions = buildMenuActions({
    t,
    selectionMode,
    selectedCount: selected.size,
    onAdd: () => { setMenuOpen(false); setView({ kind: 'qr' }); },
    onSelect: () => { setMenuOpen(false); setSelectionMode(true); },
    onClear: () => { setMenuOpen(false); setSelected(new Set()); },
    onStart: () => void startAnalysis(),
    onCompare: () => { setMenuOpen(false); setView({ kind: 'compare' }); },
    onCancel: () => void cancelSelected(),
    onRetry: () => void retrySelected(),
    onRemove: () => void removeSelected(),
  });

  return (
    <div
      className="tv-beauty-lab"
      data-testid="tv-beauty-lab"
      onKeyDown={onGridKeyDown}
    >
      <header className="tv-beauty-lab-header">
        <h2>{t('tv.beautyLab.title')}</h2>
        {selectionMode && (
          <span data-testid="tv-beauty-lab-selected">
            {t('tv.beautyLab.selectedCount', { count: String(selected.size) })}
          </span>
        )}
      </header>

      {notice && <p role="status" data-testid="tv-beauty-lab-notice">{notice}</p>}
      {error && <p role="alert">{error}</p>}

      {items.length === 0 && !loading ? (
        <p data-testid="tv-beauty-lab-empty">{t('tv.beautyLab.empty')}</p>
      ) : (
        <div className="tv-beauty-lab-grid" role="list">
          {items.map((item, i) => {
            const url = thumbUrls.current.get(item.id);
            const isSelected = selected.has(item.id);
            return (
              <button
                key={item.id}
                type="button"
                role="listitem"
                ref={(el) => { tileRefs.current[i] = el; }}
                className={`tv-beauty-lab-tile${isSelected ? ' selected' : ''}`}
                data-testid="tv-beauty-lab-tile"
                aria-pressed={selectionMode ? isSelected : undefined}
                onFocus={() => setFocusIndex(i)}
                onClick={() => (selectionMode ? toggleSelect(item.id) : openDetail(item.id))}
              >
                {url ? (
                  <img src={url} alt={item.originalFileName} className="tv-beauty-lab-thumb" />
                ) : (
                  <span className="tv-beauty-lab-thumb placeholder" aria-hidden="true" />
                )}
                <span className="tv-beauty-lab-tile-status">{statusLabel(t, item.latestRunStatus)}</span>
                {item.overallScore != null && (
                  <span className="tv-beauty-lab-tile-score" data-testid="tv-beauty-lab-score">
                    {item.overallScore.toFixed(1)}/10
                  </span>
                )}
                {isSelected && <span className="tv-beauty-lab-tile-badge" aria-hidden="true">✓</span>}
                {item.latestRunErrorCode && (
                  <span className="tv-beauty-lab-tile-error" aria-hidden="true">!</span>
                )}
              </button>
            );
          })}
        </div>
      )}

      {nextCursor && (
        <button type="button" onClick={() => void load(nextCursor, true)} disabled={loading}>
          {t('tv.beautyLab.loadMore')}
        </button>
      )}

      {menuOpen && (
        <div className="tv-beauty-lab-menu" role="menu" data-testid="tv-beauty-lab-menu">
          {menuActions.map((a) => (
            <button key={a.key} type="button" role="menuitem" onClick={a.onClick}>
              {a.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

interface MenuAction { key: string; label: string; onClick: () => void }

function buildMenuActions(opts: {
  t: (k: MessageKey, p?: Record<string, string>) => string;
  selectionMode: boolean;
  selectedCount: number;
  onAdd: () => void;
  onSelect: () => void;
  onClear: () => void;
  onStart: () => void;
  onCompare: () => void;
  onCancel: () => void;
  onRetry: () => void;
  onRemove: () => void;
}): MenuAction[] {
  const { t, selectionMode, selectedCount } = opts;
  const actions: MenuAction[] = [
    { key: 'add', label: t('tv.beautyLab.addImages'), onClick: opts.onAdd },
  ];
  if (!selectionMode) {
    actions.push({ key: 'select', label: t('tv.beautyLab.select'), onClick: opts.onSelect });
  } else {
    if (selectedCount > 0) {
      actions.push({ key: 'start', label: t('tv.beautyLab.startAnalysis'), onClick: opts.onStart });
      if (selectedCount >= 2) {
        actions.push({ key: 'compare', label: t('tv.beautyLab.compare'), onClick: opts.onCompare });
      }
      actions.push({ key: 'cancel', label: t('tv.beautyLab.cancel'), onClick: opts.onCancel });
      actions.push({ key: 'retry', label: t('tv.beautyLab.retry'), onClick: opts.onRetry });
      actions.push({ key: 'remove', label: t('tv.beautyLab.remove'), onClick: opts.onRemove });
    }
    actions.push({ key: 'clear', label: t('tv.beautyLab.clearSelection'), onClick: opts.onClear });
  }
  return actions;
}

// ── Detail ──────────────────────────────────────────────────────────────────

function TvBeautyLabDetail({
  grant,
  id,
  onClose,
  onPersonalError,
  onChanged,
}: {
  grant: string;
  id: string;
  onClose: () => void;
  onPersonalError: (err: unknown) => boolean;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  const [detail, setDetail] = useState<TvBeautyLabItemDetail | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const previewRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getTvBeautyLabItem(grant, id)
      .then((d) => {
        if (cancelled) return;
        setDetail(d);
        return fetchTvBeautyLabMediaObjectUrl(grant, `/api/tv/personal/aesthetics/items/${id}/preview`);
      })
      .then((url) => {
        if (!url) return;
        if (cancelled) { URL.revokeObjectURL(url); return; }
        previewRef.current = url;
        setPreviewUrl(url);
      })
      .catch((err) => {
        if (onPersonalError(err)) return;
        setError(t('aesthetics.detailError'));
      });
    return () => {
      cancelled = true;
      if (previewRef.current) { URL.revokeObjectURL(previewRef.current); previewRef.current = null; }
    };
  }, [grant, id, onPersonalError, t]);

  useEffect(() => { closeRef.current?.focus(); }, []);

  const run = detail?.latestRun ?? null;
  const metricsByGroup = useMemo(() => {
    const map = new Map<string, TvBeautyLabMetric[]>();
    for (const g of METRIC_GROUPS) map.set(g, []);
    for (const m of run?.metrics ?? []) {
      if (!map.has(m.group)) map.set(m.group, []);
      map.get(m.group)!.push(m);
    }
    return map;
  }, [run]);

  const cancel = async () => {
    if (!run) return;
    try {
      await cancelTvBeautyLabRun(grant, run.id);
      onChanged();
      onClose();
    } catch (err) { if (!onPersonalError(err)) setError(t('aesthetics.detailError')); }
  };
  const retry = async () => {
    if (!run) return;
    try {
      await retryTvBeautyLabRun(grant, run.id);
      onChanged();
      onClose();
    } catch (err) { if (!onPersonalError(err)) setError(t('aesthetics.detailError')); }
  };

  const cancellable = run && (run.status === 'queued' || run.status === 'running');
  const retryable = run && (run.status === 'failed' || run.status === 'cancelled');

  return (
    <div
      className="tv-beauty-lab-detail"
      data-testid="tv-beauty-lab-detail"
      onKeyDown={(e) => {
        if (e.key === 'Backspace' || e.key === 'Escape') { e.preventDefault(); onClose(); }
      }}
    >
      <div className="tv-beauty-lab-detail-preview">
        {previewUrl && detail && <img src={previewUrl} alt={detail.originalFileName} />}
      </div>
      <div className="tv-beauty-lab-detail-panel">
        <button ref={closeRef} type="button" onClick={onClose}>{t('tv.beautyLab.detailBack')}</button>
        {error && <p role="alert">{error}</p>}
        {detail && (
          <>
            <p><strong>{t('aesthetics.runStatus')}:</strong> {statusLabel(t, run?.status ?? null)}</p>
            {!run && <p>{t('tv.beautyLab.notAnalyzed')}</p>}
            {run && (
              <>
                {METRIC_GROUPS.map((g) => {
                  const ms = metricsByGroup.get(g) ?? [];
                  if (ms.length === 0) return null;
                  return (
                    <section key={g} data-testid={`tv-beauty-lab-group-${g}`}>
                      <h3>{t(`aesthetics.group.${g}` as MessageKey)}</h3>
                      <ul>
                        {ms.map((m) => (
                          <li key={m.key}>
                            <span>{metricLabel(t, m.key)}</span>
                            <span>{m.value.toFixed(1)}/{m.scaleMax.toFixed(0)}</span>
                          </li>
                        ))}
                      </ul>
                    </section>
                  );
                })}
                <dl className="tv-beauty-lab-detail-meta">
                  <dt>{t('aesthetics.model')}</dt>
                  <dd>{run.modelName ?? '—'}{run.modelRevision ? ` (${run.modelRevision})` : ''}</dd>
                  <dt>{t('aesthetics.runtime')}</dt>
                  <dd>{run.runtimeName ?? '—'}{run.runtimeVersion ? ` ${run.runtimeVersion}` : ''}</dd>
                  <dt>{t('aesthetics.preprocessing')}</dt>
                  <dd>{run.preprocessingProfileKey}</dd>
                  <dt>{t('aesthetics.duration')}</dt>
                  <dd>{run.durationMs != null ? `${run.durationMs} ms` : '—'}</dd>
                </dl>
                {run.warnings.length > 0 && (
                  <ul className="tv-beauty-lab-detail-warnings" data-testid="tv-beauty-lab-warnings">
                    {run.warnings.map((w, i) => <li key={i}>{w}</li>)}
                  </ul>
                )}
                {run.errorCode && <p role="alert">{t('aesthetics.errorPrefix')}: {run.errorCode}</p>}
                {cancellable && <button type="button" onClick={() => void cancel()}>{t('aesthetics.cancelRun')}</button>}
                {retryable && <button type="button" onClick={() => void retry()}>{t('aesthetics.retryRun')}</button>}
              </>
            )}
            {detail.history.length > 0 && (
              <section className="tv-beauty-lab-detail-history">
                <h3>{t('aesthetics.history')}</h3>
                <ul>
                  {detail.history.map((h) => (
                    <li key={h.id}>{statusLabel(t, h.status)} · {h.overallScore != null ? `${h.overallScore.toFixed(1)}/10` : '—'}</li>
                  ))}
                </ul>
              </section>
            )}
          </>
        )}
      </div>
    </div>
  );
}

// ── Compare ─────────────────────────────────────────────────────────────────

function TvBeautyLabCompare({
  grant,
  ids,
  onClose,
  onPersonalError,
}: {
  grant: string;
  ids: string[];
  onClose: () => void;
  onPersonalError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [columns, setColumns] = useState<{ id: string; name: string; metrics: Map<string, number> }[]>([]);
  const [loading, setLoading] = useState(true);
  const closeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const cols: { id: string; name: string; metrics: Map<string, number> }[] = [];
      for (const id of ids) {
        try {
          const d = await getTvBeautyLabItem(grant, id);
          const run = d.latestRun;
          // EXCLUDE images without a completed run (same semantics as the web).
          if (!run || run.status !== 'succeeded' || run.metrics.length === 0) continue;
          const m = new Map<string, number>();
          for (const metric of run.metrics) m.set(metric.key, metric.value);
          cols.push({ id, name: d.originalFileName, metrics: m });
        } catch (err) {
          if (onPersonalError(err)) return;
        }
      }
      if (!cancelled) { setColumns(cols); setLoading(false); }
    })();
    return () => { cancelled = true; };
  }, [ids, grant, onPersonalError]);

  useEffect(() => { closeRef.current?.focus(); }, []);

  // Row order = union of metric keys in a stable order (first column's order).
  const metricKeys = useMemo(() => {
    const keys: string[] = [];
    for (const col of columns) {
      for (const k of col.metrics.keys()) if (!keys.includes(k)) keys.push(k);
    }
    return keys;
  }, [columns]);

  return (
    <div
      className="tv-beauty-lab-compare"
      data-testid="tv-beauty-lab-compare"
      onKeyDown={(e) => { if (e.key === 'Backspace' || e.key === 'Escape') { e.preventDefault(); onClose(); } }}
    >
      <header>
        <h2>{t('tv.beautyLab.compareTitle')}</h2>
        <button ref={closeRef} type="button" onClick={onClose}>{t('tv.beautyLab.detailBack')}</button>
      </header>
      <p className="muted">{t('tv.beautyLab.compareHint')}</p>
      {loading ? (
        <p>{t('tv.beautyLab.loading')}</p>
      ) : columns.length === 0 ? (
        <p data-testid="tv-beauty-lab-compare-empty">{t('tv.beautyLab.compareEmpty')}</p>
      ) : (
        <div className="tv-beauty-lab-compare-scroll">
          <table>
            <thead>
              <tr>
                <th>{t('aesthetics.compareMetric')}</th>
                {columns.map((c) => <th key={c.id}>{c.name}</th>)}
              </tr>
            </thead>
            <tbody>
              {metricKeys.map((key) => {
                const values = columns.map((c) => c.metrics.get(key));
                const max = Math.max(...values.filter((v): v is number => v != null));
                return (
                  <tr key={key}>
                    <th scope="row">{metricLabel(t, key)}</th>
                    {values.map((v, i) => (
                      <td
                        key={columns[i].id}
                        className={v != null && v === max ? 'best' : undefined}
                        data-best={v != null && v === max ? 'true' : undefined}
                      >
                        {v != null ? v.toFixed(1) : '—'}
                      </td>
                    ))}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── QR upload ───────────────────────────────────────────────────────────────

function TvBeautyLabQr({
  grant,
  onClose,
  onPersonalError,
}: {
  grant: string;
  onClose: () => void;
  onPersonalError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [qrSvg, setQrSvg] = useState<string | null>(null);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [counts, setCounts] = useState({ accepted: 0, rejected: 0 });
  const [expiresAt, setExpiresAt] = useState<number | null>(null);
  const [secondsLeft, setSecondsLeft] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const sessionRef = useRef<string | null>(null);

  // Create the session once; render its QR; revoke on close/unmount.
  useEffect(() => {
    let cancelled = false;
    createTvBeautyLabUploadSession(grant)
      .then(async (s) => {
        if (cancelled) { void revokeTvBeautyLabUploadSession(grant, s.id).catch(() => {}); return; }
        sessionRef.current = s.id;
        setSessionId(s.id);
        setExpiresAt(new Date(s.expiresAt).getTime());
        const absolute = `${window.location.origin}${s.uploadUrl}`;
        const svg = await QRCode.toString(absolute, { type: 'svg', margin: 1, width: 240 });
        if (!cancelled) setQrSvg(svg);
      })
      .catch((err) => { if (!onPersonalError(err)) setError(t('tv.beautyLab.loadError')); });
    return () => {
      cancelled = true;
      const id = sessionRef.current;
      if (id) void revokeTvBeautyLabUploadSession(grant, id).catch(() => {});
    };
  }, [grant, onPersonalError, t]);

  // Poll ONLY this session while the QR screen is open (transient timer).
  useEffect(() => {
    if (!sessionId) return;
    const timer = window.setInterval(() => {
      getTvBeautyLabUploadSession(grant, sessionId)
        .then((s) => setCounts({ accepted: s.accepted, rejected: s.rejected }))
        .catch((err) => onPersonalError(err));
    }, QR_POLL_MS);
    return () => window.clearInterval(timer);
  }, [sessionId, grant, onPersonalError]);

  // Expiry countdown (drives the on-screen timer only; no server call).
  useEffect(() => {
    if (expiresAt == null) return;
    const tick = () => setSecondsLeft(Math.max(0, Math.round((expiresAt - Date.now()) / 1000)));
    tick();
    const timer = window.setInterval(tick, 1_000);
    return () => window.clearInterval(timer);
  }, [expiresAt]);

  useEffect(() => { closeRef.current?.focus(); }, []);

  return (
    <div
      className="tv-beauty-lab-qr"
      data-testid="tv-beauty-lab-qr"
      onKeyDown={(e) => { if (e.key === 'Backspace' || e.key === 'Escape') { e.preventDefault(); onClose(); } }}
    >
      <h2>{t('tv.beautyLab.qrTitle')}</h2>
      <p>{t('tv.beautyLab.qrInstructions')}</p>
      {error && <p role="alert">{error}</p>}
      {qrSvg && (
        <div
          className="tv-beauty-lab-qr-code"
          data-testid="tv-beauty-lab-qr-code"
          aria-label={t('tv.beautyLab.qrTitle')}
          dangerouslySetInnerHTML={{ __html: qrSvg }}
        />
      )}
      <p data-testid="tv-beauty-lab-qr-counts">
        {t('tv.beautyLab.qrCounts', { accepted: String(counts.accepted), rejected: String(counts.rejected) })}
      </p>
      {secondsLeft != null && (
        <p>{secondsLeft > 0
          ? t('tv.beautyLab.qrExpiry', { seconds: String(secondsLeft) })
          : t('tv.beautyLab.qrExpired')}</p>
      )}
      <button ref={closeRef} type="button" onClick={onClose}>{t('tv.beautyLab.qrClose')}</button>
    </div>
  );
}
