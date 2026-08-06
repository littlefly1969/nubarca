import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ApiError,
  cancelAestheticRun,
  getAestheticLabItem,
  listAestheticLabItems,
  removeAestheticLabItem,
  requestAestheticAnalysis,
  retryAestheticRun,
  uploadAestheticLabItem,
  type AestheticLabItem,
  type AestheticLabItemDetail,
  type AestheticMetric,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

// UI batch cap mirrors the server default (HumanAesExpert:MaximumBatchItems=20);
// the server remains authoritative and rejects a larger batch.
const UI_MAX_BATCH = 20;

// Metric groups rendered in this order in the detail view.
const METRIC_GROUPS = ['face', 'appearance', 'environment', 'overall'] as const;

function metricLabel(t: (k: MessageKey) => string, key: string): string {
  const mk = `aesthetics.metric.${key}` as MessageKey;
  const label = t(mk);
  return label === mk ? key : label;
}

function statusLabel(t: (k: MessageKey) => string, status: string | null): string {
  if (!status) return t('aesthetics.status.none');
  const mk = `aesthetics.status.${status}` as MessageKey;
  const label = t(mk);
  return label === mk ? status : label;
}

export function AestheticsLabPage() {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();

  const [items, setItems] = useState<AestheticLabItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [comparisonIds, setComparisonIds] = useState<string[] | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const load = useCallback(
    async (append: boolean, fromCursor: string | null) => {
      try {
        const page = await listAestheticLabItems(fromCursor, 50);
        setItems((prev) => (append ? [...prev, ...page.items] : page.items));
        setCursor(page.nextCursor);
        setError(null);
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setError(t('aesthetics.loadError'));
      } finally {
        setLoading(false);
      }
    },
    [invalidateAuth, t],
  );

  useEffect(() => {
    setLoading(true);
    void load(false, null);
  }, [load]);

  // Poll while any item has a live run so status/scores refresh without a manual
  // reload. Stops when nothing is queued/running.
  const hasLive = useMemo(
    () => items.some((i) => i.latestRunStatus === 'queued' || i.latestRunStatus === 'running'),
    [items],
  );
  useEffect(() => {
    if (!hasLive) return;
    const id = window.setInterval(() => void load(false, null), 4000);
    return () => window.clearInterval(id);
  }, [hasLive, load]);

  function toggle(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function onUpload(files: FileList | null) {
    if (!files || files.length === 0) return;
    setBusy(true);
    setNotice(null);
    let ok = 0;
    let failed = 0;
    for (const file of Array.from(files)) {
      try {
        await uploadAestheticLabItem(file);
        ok += 1;
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          setBusy(false);
          return;
        }
        failed += 1;
      }
    }
    setNotice(t('aesthetics.uploadResult', { ok, failed }));
    setBusy(false);
    if (fileInputRef.current) fileInputRef.current.value = '';
    await load(false, null);
  }

  async function onStartAnalysis() {
    const ids = [...selected];
    if (ids.length === 0) return;
    if (ids.length > UI_MAX_BATCH) {
      setNotice(t('aesthetics.batchLimit', { max: UI_MAX_BATCH }));
      return;
    }
    setBusy(true);
    setNotice(null);
    try {
      const result = await requestAestheticAnalysis(ids);
      setNotice(
        t('aesthetics.analysisRequested', {
          enqueued: result.enqueued.length,
          skipped: result.skipped.length,
        }),
      );
      setSelected(new Set());
      await load(false, null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        setNotice(t('aesthetics.batchLimit', { max: UI_MAX_BATCH }));
        return;
      }
      setNotice(t('aesthetics.analysisError'));
    } finally {
      setBusy(false);
    }
  }

  async function onRemove(item: AestheticLabItem) {
    if (!window.confirm(t('aesthetics.confirmRemove', { name: item.originalFileName }))) return;
    setBusy(true);
    try {
      await removeAestheticLabItem(item.id);
      setSelected((prev) => {
        const next = new Set(prev);
        next.delete(item.id);
        return next;
      });
      await load(false, null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setNotice(t('aesthetics.removeError'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="aesthetics-page" aria-label={t('aesthetics.heading')}>
      <header className="page-header">
        {/* h3: the Laboratory shell owns the h2. */}
        <h3>{t('lab.aesthetics')}</h3>
        <p className="page-intro">{t('aesthetics.intro')}</p>
        <p className="aesthetics-disclaimer" role="note">
          {t('aesthetics.disclaimer')}
        </p>
      </header>

      <div className="aesthetics-upload">
        <label className="row-action-primary" aria-disabled={busy}>
          {busy ? t('aesthetics.uploading') : t('aesthetics.selectFiles')}
          <input
            ref={fileInputRef}
            type="file"
            accept="image/*"
            multiple
            hidden
            disabled={busy}
            onChange={(e) => void onUpload(e.target.files)}
          />
        </label>
        <span className="aesthetics-upload-hint">{t('aesthetics.uploadHint')}</span>
      </div>

      {notice && (
        <div className="gallery-notice" role="status" data-testid="aesthetics-notice">
          {notice}
        </div>
      )}

      {selected.size > 0 && (
        <div className="aesthetics-actions" role="region" aria-label={t('aesthetics.actions')}>
          <span data-testid="aesthetics-selected-count">
            {t('aesthetics.selectedCount', { count: selected.size, max: UI_MAX_BATCH })}
          </span>
          <button
            type="button"
            className="row-action-primary"
            data-testid="aesthetics-start-analysis"
            disabled={busy || selected.size > UI_MAX_BATCH}
            onClick={() => void onStartAnalysis()}
          >
            {t('aesthetics.startAnalysis')}
          </button>
          <button
            type="button"
            className="row-action"
            data-testid="aesthetics-compare-scores"
            disabled={busy || selected.size < 2}
            onClick={() => setComparisonIds([...selected])}
          >
            {t('aesthetics.compareScores')}
          </button>
          <button type="button" className="row-action" onClick={() => setSelected(new Set())}>
            {t('aesthetics.clearSelection')}
          </button>
        </div>
      )}

      {loading ? (
        <p data-testid="aesthetics-loading">{t('aesthetics.loading')}</p>
      ) : error ? (
        <p className="error" role="alert">{error}</p>
      ) : items.length === 0 ? (
        <p data-testid="aesthetics-empty">{t('aesthetics.empty')}</p>
      ) : (
        <ul className="aesthetics-grid" data-testid="aesthetics-grid">
          {items.map((item) => (
            <AestheticCard
              key={item.id}
              item={item}
              selected={selected.has(item.id)}
              onToggle={() => toggle(item.id)}
              onOpen={() => setDetailId(item.id)}
              onRemove={() => void onRemove(item)}
            />
          ))}
        </ul>
      )}

      {cursor && !loading && (
        <button
          type="button"
          className="row-action"
          data-testid="aesthetics-load-more"
          disabled={busy}
          onClick={() => void load(true, cursor)}
        >
          {t('aesthetics.loadMore')}
        </button>
      )}

      {detailId && (
        <AestheticDetailModal
          itemId={detailId}
          onClose={() => setDetailId(null)}
          onChanged={() => void load(false, null)}
        />
      )}

      {comparisonIds && (
        <AestheticComparisonModal
          itemIds={comparisonIds}
          onClose={() => setComparisonIds(null)}
        />
      )}
    </section>
  );
}

function AestheticCard({
  item,
  selected,
  onToggle,
  onOpen,
  onRemove,
}: {
  item: AestheticLabItem;
  selected: boolean;
  onToggle: () => void;
  onOpen: () => void;
  onRemove: () => void;
}) {
  const { t } = useI18n();
  const score = item.overallScore;
  return (
    <li className={`aesthetics-card${selected ? ' is-selected' : ''}`} data-selected={selected}>
      <button
        type="button"
        role="checkbox"
        aria-checked={selected}
        aria-label={t(selected ? 'aesthetics.deselectAria' : 'aesthetics.selectAria', { name: item.originalFileName })}
        className={`aesthetics-select${selected ? ' is-selected' : ''}`}
        onClick={onToggle}
      >
        <span aria-hidden="true">{selected ? '✓' : ''}</span>
      </button>
      <button type="button" className="aesthetics-thumb" onClick={onOpen} aria-label={t('aesthetics.openDetail', { name: item.originalFileName })}>
        <img src={item.thumbnailUrl} alt="" loading="lazy" />
      </button>
      <div className="aesthetics-meta">
        <span className="aesthetics-name" title={item.originalFileName}>{item.originalFileName}</span>
        <span className={`aesthetics-status status-${item.latestRunStatus ?? 'none'}`}>
          {statusLabel(t, item.latestRunStatus)}
        </span>
        {score !== null && (
          <span className="aesthetics-score" data-testid="aesthetics-overall">
            {t('aesthetics.overallScore', { score: (score * 10).toFixed(1) })}
          </span>
        )}
        {item.latestRunErrorCode && (
          <span className="aesthetics-error-code">{t('aesthetics.errorPrefix')}: {item.latestRunErrorCode}</span>
        )}
        <span className="aesthetics-profile">{item.profileKey}</span>
      </div>
      <button type="button" className="row-action row-action-destructive aesthetics-remove" onClick={onRemove}>
        {t('aesthetics.remove')}
      </button>
    </li>
  );
}

function AestheticDetailModal({
  itemId,
  onClose,
  onChanged,
}: {
  itemId: string;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [detail, setDetail] = useState<AestheticLabItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const reload = useCallback(async () => {
    try {
      setDetail(await getAestheticLabItem(itemId));
      setError(null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setError(t('aesthetics.detailError'));
    }
  }, [itemId, invalidateAuth, t]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const run = detail?.latestRun ?? null;
  const live = run?.status === 'queued' || run?.status === 'running';
  useEffect(() => {
    if (!live) return;
    const id = window.setInterval(() => void reload(), 4000);
    return () => window.clearInterval(id);
  }, [live, reload]);

  async function onCancel() {
    if (!run) return;
    setBusy(true);
    try {
      await cancelAestheticRun(run.id);
      await reload();
      onChanged();
    } catch {
      /* best-effort */
    } finally {
      setBusy(false);
    }
  }

  async function onRetry() {
    if (!run) return;
    setBusy(true);
    try {
      await retryAestheticRun(run.id);
      await reload();
      onChanged();
    } catch {
      /* best-effort */
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={t('aesthetics.detailTitle')}>
      <div className="modal aesthetics-detail">
        <div className="modal-header">
          <h2>{detail?.originalFileName ?? t('aesthetics.detailTitle')}</h2>
          <button type="button" className="row-action" onClick={onClose}>{t('aesthetics.close')}</button>
        </div>

        {error && <p className="error" role="alert">{error}</p>}
        {!detail ? (
          <p>{t('aesthetics.loading')}</p>
        ) : (
          <div className="aesthetics-detail-body">
            <img className="aesthetics-preview" src={detail.previewUrl} alt="" />
            <p className="aesthetics-disclaimer" role="note">{t('aesthetics.disclaimer')}</p>

            {!run ? (
              <p data-testid="aesthetics-no-run">{t('aesthetics.noRun')}</p>
            ) : (
              <div className="aesthetics-run">
                <dl className="aesthetics-run-meta">
                  <div><dt>{t('aesthetics.runStatus')}</dt><dd data-testid="aesthetics-run-status">{statusLabel(t, run.status)}</dd></div>
                  <div><dt>{t('aesthetics.model')}</dt><dd>{run.modelName ?? '—'} {run.modelRevision ?? ''}</dd></div>
                  <div><dt>{t('aesthetics.runtime')}</dt><dd>{run.runtimeName ?? '—'} {run.runtimeVersion ?? ''}</dd></div>
                  <div><dt>{t('aesthetics.preprocessing')}</dt><dd>{run.preprocessingProfileKey}</dd></div>
                  <div><dt>{t('aesthetics.runDate')}</dt><dd>{formatDate(run.createdAt)}</dd></div>
                  <div><dt>{t('aesthetics.duration')}</dt><dd>{run.durationMs != null ? `${run.durationMs} ms` : '—'}</dd></div>
                </dl>

                {run.errorCode && (
                  <p className="aesthetics-error-code" role="alert">{t('aesthetics.errorPrefix')}: {run.errorCode}</p>
                )}
                {run.warnings.length > 0 && (
                  <ul className="aesthetics-warnings">
                    {run.warnings.map((w, i) => <li key={i}>{w}</li>)}
                  </ul>
                )}

                {run.status === 'succeeded' && run.metrics.length > 0 && (
                  <ExpertMetricsView metrics={run.metrics} />
                )}

                {/* Prepared sections — rendered ONLY when the capability actually
                    completed, never as empty placeholders. */}
                {run.completedCapabilities.includes('score_head') && (
                  <section className="aesthetics-cap-section" data-testid="aesthetics-score-head">
                    <h3>{t('aesthetics.section.scoreHead')}</h3>
                  </section>
                )}
                {run.completedCapabilities.includes('meta_voter') && (
                  <section className="aesthetics-cap-section" data-testid="aesthetics-meta-voter">
                    <h3>{t('aesthetics.section.metaVoter')}</h3>
                  </section>
                )}
                {run.texts.length > 0 && (
                  <section className="aesthetics-cap-section" data-testid="aesthetics-text">
                    <h3>{t('aesthetics.section.text')}</h3>
                    {run.texts.map((tx, i) => (
                      <div key={i} className="aesthetics-text-item">
                        <strong>{tx.kind}</strong>
                        <p>{tx.text}</p>
                      </div>
                    ))}
                  </section>
                )}

                <div className="aesthetics-run-actions">
                  {live && (
                    <button type="button" className="row-action" disabled={busy} onClick={() => void onCancel()}>
                      {t('aesthetics.cancelRun')}
                    </button>
                  )}
                  {(run.status === 'failed' || run.status === 'cancelled') && (
                    <button type="button" className="row-action" disabled={busy} onClick={() => void onRetry()}>
                      {t('aesthetics.retryRun')}
                    </button>
                  )}
                </div>
              </div>
            )}

            {detail.history.length > 1 && (
              <section className="aesthetics-history">
                <h3>{t('aesthetics.history')}</h3>
                <ul>
                  {detail.history.map((h) => (
                    <li key={h.id} data-testid="aesthetics-history-item">
                      <span>{formatDate(h.createdAt)}</span>
                      <span>{statusLabel(t, h.status)}</span>
                      {h.overallScore !== null && <span>{(h.overallScore * 10).toFixed(1)}</span>}
                      {h.errorCode && <span className="aesthetics-error-code">{h.errorCode}</span>}
                    </li>
                  ))}
                </ul>
              </section>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function AestheticComparisonModal({
  itemIds,
  onClose,
}: {
  itemIds: string[];
  onClose: () => void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [details, setDetails] = useState<AestheticLabItemDetail[] | null>(null);
  const [unavailable, setUnavailable] = useState(0);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      const results = await Promise.allSettled(
        itemIds.map((id) => getAestheticLabItem(id, abort.signal)),
      );
      if (abort.signal.aborted) return;

      const unauthorized = results.some(
        (result) => result.status === 'rejected'
          && result.reason instanceof ApiError
          && result.reason.status === 401,
      );
      if (unauthorized) {
        invalidateAuth();
        return;
      }

      const loaded = results
        .filter((result): result is PromiseFulfilledResult<AestheticLabItemDetail> => result.status === 'fulfilled')
        .map((result) => result.value);
      const comparable = loaded.filter(
        (detail) => detail.latestRun?.status === 'succeeded'
          && detail.latestRun.metrics.length > 0,
      );
      setUnavailable(results.length - comparable.length);
      setDetails(comparable);
      if (comparable.length === 0) setError(t('aesthetics.compareEmpty'));
    })().catch(() => {
      if (!abort.signal.aborted) setError(t('aesthetics.compareError'));
    });
    return () => abort.abort();
  }, [itemIds, invalidateAuth, t]);

  const metricsByGroup = useMemo(() => {
    const groups = new Map<string, AestheticMetric[]>();
    for (const group of METRIC_GROUPS) {
      const seen = new Set<string>();
      const metrics: AestheticMetric[] = [];
      for (const detail of details ?? []) {
        for (const metric of detail.latestRun?.metrics ?? []) {
          if (metric.group === group && !seen.has(metric.key)) {
            seen.add(metric.key);
            metrics.push(metric);
          }
        }
      }
      if (metrics.length > 0) groups.set(group, metrics);
    }
    return groups;
  }, [details]);

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-label={t('aesthetics.compareTitle')}>
      <div className="modal aesthetics-comparison">
        <div className="modal-header">
          <div>
            <h2>{t('aesthetics.compareTitle')}</h2>
            <p className="muted">{t('aesthetics.compareHint')}</p>
          </div>
          <button type="button" className="row-action" onClick={onClose}>{t('aesthetics.close')}</button>
        </div>

        {details === null && !error && <p>{t('aesthetics.compareLoading')}</p>}
        {error && <p className="error" role="alert">{error}</p>}
        {details && unavailable > 0 && (
          <p className="gallery-notice" role="status">
            {t('aesthetics.compareUnavailable', { count: unavailable })}
          </p>
        )}

        {details && details.length > 0 && (
          <div className="aesthetics-comparison-scroll">
            <table className="aesthetics-comparison-table" data-testid="aesthetics-comparison-table">
              <thead>
                <tr>
                  <th scope="col">{t('aesthetics.compareMetric')}</th>
                  {details.map((detail) => (
                    <th key={detail.id} scope="col" title={detail.originalFileName}>
                      {detail.originalFileName}
                    </th>
                  ))}
                </tr>
              </thead>
              {[...metricsByGroup.entries()].map(([group, metrics]) => (
                <tbody key={group}>
                  <tr className="aesthetics-comparison-group">
                    <th scope="rowgroup" colSpan={details.length + 1}>
                      {t(`aesthetics.group.${group}` as MessageKey)}
                    </th>
                  </tr>
                  {metrics.map((metric) => {
                    const values = details
                      .map((detail) => detail.latestRun?.metrics.find((m) => m.key === metric.key)?.value)
                      .filter((value): value is number => value !== undefined);
                    const best = values.length > 1 ? Math.max(...values) : null;
                    return (
                      <tr key={metric.key} data-metric-key={metric.key}>
                        <th scope="row">{metricLabel(t, metric.key)}</th>
                        {details.map((detail) => {
                          const value = detail.latestRun?.metrics.find((m) => m.key === metric.key)?.value;
                          return (
                            <td key={detail.id} className={best !== null && value === best ? 'is-best' : undefined}>
                              {value === undefined ? '—' : (value * 10).toFixed(1)}
                            </td>
                          );
                        })}
                      </tr>
                    );
                  })}
                </tbody>
              ))}
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

function ExpertMetricsView({ metrics }: { metrics: AestheticMetric[] }) {
  const { t } = useI18n();
  const byGroup = useMemo(() => {
    const map = new Map<string, AestheticMetric[]>();
    for (const m of metrics) {
      const list = map.get(m.group) ?? [];
      list.push(m);
      map.set(m.group, list);
    }
    return map;
  }, [metrics]);

  return (
    <div className="aesthetics-metrics" data-testid="aesthetics-metrics">
      {METRIC_GROUPS.filter((g) => byGroup.has(g)).map((group) => (
        <div key={group} className="aesthetics-metric-group">
          <h4>{t(`aesthetics.group.${group}` as MessageKey)}</h4>
          <ul>
            {(byGroup.get(group) ?? []).map((m) => (
              <li key={m.key} className="aesthetics-metric-row" data-metric-key={m.key}>
                <span className="aesthetics-metric-label">{metricLabel(t, m.key)}</span>
                <span className="aesthetics-metric-bar" aria-hidden="true">
                  <span
                    className="aesthetics-metric-fill"
                    style={{ width: `${Math.round(((m.value - m.scaleMin) / (m.scaleMax - m.scaleMin)) * 100)}%` }}
                  />
                </span>
                <span className="aesthetics-metric-value">{(m.value * 10).toFixed(1)}</span>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}
