import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  BackHandler,
  FlatList,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  type HWEvent,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { ApiError, getBaseUrl } from '../api/client';
import {
  cancelBeautyLabRun,
  createBeautyLabUploadSession,
  getBeautyLabItem,
  getBeautyLabUploadSession,
  listBeautyLabItems,
  removeBeautyLabItem,
  requestBeautyLabAnalysis,
  retryBeautyLabRun,
  revokeBeautyLabUploadSession,
  type BeautyLabItem,
  type BeautyLabItemDetail,
  type BeautyLabMetric,
  type BeautyLabUploadSession,
} from '../api/aesthetics';
import { AuthedImage } from '../components/AuthedImage';
import { FocusableButton } from '../components/FocusableButton';
import { FocusableTile } from '../components/FocusableTile';
import { QrCode } from '../components/QrCode';
import { useMenuOverlay } from '../lib/useMenuOverlay';
import { useI18n, type TvMessageKey } from '../i18n';

const GRID_COLUMNS = 4;
const QR_POLL_MS = 4_000;
const METRIC_GROUPS = ['face', 'appearance', 'environment', 'overall'] as const;

type LabView =
  | { kind: 'grid' }
  | { kind: 'detail'; id: string }
  | { kind: 'compare' }
  | { kind: 'qr' };

interface Props {
  onLock: (reason?: 'pinChanged') => void;
  onSessionInvalid: () => void;
}

function metricLabel(t: (k: TvMessageKey) => string, key: string): string {
  const mk = `beautyLab.metric.${key}` as TvMessageKey;
  const label = t(mk);
  return label === mk ? key : label;
}

function statusLabel(t: (k: TvMessageKey) => string, status: string | null): string {
  if (!status) return t('beautyLab.notAnalyzed');
  const mk = `beautyLab.status.${status}` as TvMessageKey;
  const label = t(mk);
  return label === mk ? status : label;
}

// TV "Beauty Lab" (Laboratorio bellezza): native Fire TV Aesthetics Lab. Grid,
// multi-select, start analysis, detail with all 12 localized metrics, compare
// matrix, cancel/retry, remove, and QR mobile upload — all through the SAME grant
// and TV projection API the web lab uses. Derived media only (never originals).
export function BeautyLabScreen({ onLock, onSessionInvalid }: Props) {
  const { t } = useI18n();
  const [items, setItems] = useState<BeautyLabItem[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [view, setView] = useState<LabView>({ kind: 'grid' });
  const [selectionMode, setSelectionMode] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const overlay = useMenuOverlay();
  const viewRef = useRef<LabView['kind']>('grid');
  viewRef.current = view.kind;
  const selectionModeRef = useRef(false);
  selectionModeRef.current = selectionMode;

  // Map an API error to the shared teardown: 401 → pairing; 403 → lock (with the
  // pin-changed notice). Returns true when it consumed the error.
  const handleError = useCallback((err: unknown): boolean => {
    if (err instanceof ApiError && err.status === 401) { onSessionInvalid(); return true; }
    if (err instanceof ApiError && err.status === 403) {
      const body = err.body as { error?: string } | null;
      onLock(body?.error === 'pin_changed' ? 'pinChanged' : undefined);
      return true;
    }
    return false;
  }, [onLock, onSessionInvalid]);

  const load = useCallback(async (cursor: string | null, append: boolean) => {
    setLoading(true);
    try {
      const page = await listBeautyLabItems(cursor, 50);
      setItems((prev) => (append ? [...prev, ...page.items] : page.items));
      setNextCursor(page.nextCursor);
      setError(null);
    } catch (err) {
      if (!handleError(err)) setError(t('beautyLab.loadError'));
    } finally {
      setLoading(false);
    }
  }, [handleError, t]);

  useEffect(() => { void load(null, false); }, [load]);

  const refreshGrid = useCallback(() => { void load(null, false); }, [load]);

  // MENU toggles the action overlay (only from the grid root). Fire TV dispatches
  // key-up AND key-down; ignore key-down (eventKeyAction === 0) so one press is
  // one toggle — matching the album grid's handler.
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction === 0) return;
    if (evt.eventType === 'menu' && viewRef.current === 'grid') overlay.toggle();
  }, [overlay]);
  useTVEventHandler(onTVEvent);

  // Layered BACK: overlay → open panel → selection → lock at the root.
  useEffect(() => {
    const onBack = () => {
      if (overlay.visibleRef.current) { overlay.hide(); return true; }
      if (viewRef.current !== 'grid') { setView({ kind: 'grid' }); return true; }
      if (selectionModeRef.current) { setSelectionMode(false); setSelected(new Set()); return true; }
      onLock();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBack);
    return () => sub.remove();
  }, [overlay, onLock]);

  const toggleSelect = useCallback((id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }, []);

  const onTilePress = useCallback((item: BeautyLabItem) => {
    if (selectionMode) toggleSelect(item.id);
    else setView({ kind: 'detail', id: item.id });
  }, [selectionMode, toggleSelect]);

  const startAnalysis = useCallback(async () => {
    overlay.hide();
    const ids = [...selected];
    if (ids.length === 0) return;
    try {
      const res = await requestBeautyLabAnalysis(ids);
      setNotice(t('beautyLab.analysisStarted')
        .replace('{enqueued}', String(res.enqueued.length))
        .replace('{skipped}', String(res.skipped.length)));
      refreshGrid();
    } catch (err) {
      if (!handleError(err)) setNotice(t('beautyLab.analysisError'));
    }
  }, [overlay, selected, t, refreshGrid, handleError]);

  const cancelSelected = useCallback(async () => {
    overlay.hide();
    for (const id of selected) {
      try {
        const detail = await getBeautyLabItem(id);
        const run = detail.latestRun;
        if (run && (run.status === 'queued' || run.status === 'running')) await cancelBeautyLabRun(run.id);
      } catch (err) { if (handleError(err)) return; }
    }
    refreshGrid();
  }, [overlay, selected, refreshGrid, handleError]);

  const retrySelected = useCallback(async () => {
    overlay.hide();
    for (const id of selected) {
      try {
        const detail = await getBeautyLabItem(id);
        const run = detail.latestRun;
        if (run && (run.status === 'failed' || run.status === 'cancelled')) await retryBeautyLabRun(run.id);
      } catch (err) { if (handleError(err)) return; }
    }
    refreshGrid();
  }, [overlay, selected, refreshGrid, handleError]);

  const removeSelected = useCallback(async () => {
    overlay.hide();
    for (const id of selected) {
      try { await removeBeautyLabItem(id); } catch (err) { if (handleError(err)) return; }
    }
    setSelected(new Set());
    setSelectionMode(false);
    refreshGrid();
  }, [overlay, selected, refreshGrid, handleError]);

  if (view.kind === 'detail') {
    return (
      <BeautyLabDetail
        id={view.id}
        onClose={() => setView({ kind: 'grid' })}
        onChanged={refreshGrid}
        onError={handleError}
      />
    );
  }
  if (view.kind === 'compare') {
    return <BeautyLabCompare ids={[...selected]} onClose={() => setView({ kind: 'grid' })} onError={handleError} />;
  }
  if (view.kind === 'qr') {
    return (
      <BeautyLabQr
        onClose={() => { setView({ kind: 'grid' }); refreshGrid(); }}
        onError={handleError}
      />
    );
  }

  return (
    <View style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.title}>{t('beautyLab.title')}</Text>
        {selectionMode && (
          <Text style={styles.headerInfo}>
            {t('beautyLab.selected', { count: String(selected.size) })}
          </Text>
        )}
      </View>
      {notice && <Text style={styles.notice}>{notice}</Text>}
      {error && <Text style={styles.error}>{error}</Text>}

      {items.length === 0 && !loading ? (
        <Text style={styles.empty}>{t('beautyLab.empty')}</Text>
      ) : (
        <FlatList
          data={items}
          keyExtractor={(i) => i.id}
          numColumns={GRID_COLUMNS}
          contentContainerStyle={styles.grid}
          renderItem={({ item, index }) => {
            const isSelected = selected.has(item.id);
            return (
              <FocusableTile
                onSelect={() => onTilePress(item)}
                hasTVPreferredFocus={index === 0}
                accessibilityLabel={item.originalFileName}
                style={styles.tile}
              >
                <View style={styles.thumbBox}>
                  <AuthedImage path={item.thumbnailUrl} personal style={styles.thumb} />
                  {isSelected && <View style={styles.selectedBadge}><Text style={styles.badgeText}>✓</Text></View>}
                </View>
                <View style={styles.tileMeta}>
                  <Text style={styles.tileStatus} numberOfLines={1}>{statusLabel(t, item.latestRunStatus)}</Text>
                  {item.overallScore != null && (
                    <Text style={styles.tileScore}>{item.overallScore.toFixed(1)}/10</Text>
                  )}
                  {item.latestRunErrorCode && <Text style={styles.tileError}>!</Text>}
                </View>
              </FocusableTile>
            );
          }}
          ListFooterComponent={nextCursor ? (
            <FocusableButton label={t('beautyLab.loadMore')} onPress={() => void load(nextCursor, true)} />
          ) : null}
        />
      )}

      {overlay.visible && (
        <View style={styles.menu}>
          <Text style={styles.menuTitle}>{t('beautyLab.menu')}</Text>
          <FocusableButton
            label={t('beautyLab.addImages')}
            hasTVPreferredFocus
            onPress={() => { overlay.hide(); setView({ kind: 'qr' }); }}
          />
          {!selectionMode ? (
            <FocusableButton label={t('beautyLab.select')} onPress={() => { overlay.hide(); setSelectionMode(true); }} />
          ) : (
            <>
              {selected.size > 0 && (
                <>
                  <FocusableButton label={t('beautyLab.startAnalysis')} onPress={() => void startAnalysis()} />
                  {selected.size >= 2 && (
                    <FocusableButton label={t('beautyLab.compare')} onPress={() => { overlay.hide(); setView({ kind: 'compare' }); }} />
                  )}
                  <FocusableButton label={t('beautyLab.cancel')} onPress={() => void cancelSelected()} />
                  <FocusableButton label={t('beautyLab.retry')} onPress={() => void retrySelected()} />
                  <FocusableButton label={t('beautyLab.remove')} onPress={() => void removeSelected()} />
                </>
              )}
              <FocusableButton label={t('beautyLab.clearSelection')} onPress={() => { overlay.hide(); setSelected(new Set()); }} />
            </>
          )}
        </View>
      )}
    </View>
  );
}

// ── Detail ──────────────────────────────────────────────────────────────────

function BeautyLabDetail({
  id, onClose, onChanged, onError,
}: {
  id: string;
  onClose: () => void;
  onChanged: () => void;
  onError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [detail, setDetail] = useState<BeautyLabItemDetail | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getBeautyLabItem(id)
      .then((d) => { if (!cancelled) setDetail(d); })
      .catch((e) => { if (!cancelled && !onError(e)) setErr(t('beautyLab.loadError')); });
    return () => { cancelled = true; };
  }, [id, onError, t]);

  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => { onClose(); return true; });
    return () => sub.remove();
  }, [onClose]);

  const run = detail?.latestRun ?? null;
  const byGroup = useMemo(() => {
    const map = new Map<string, BeautyLabMetric[]>();
    for (const g of METRIC_GROUPS) map.set(g, []);
    for (const m of run?.metrics ?? []) { if (!map.has(m.group)) map.set(m.group, []); map.get(m.group)!.push(m); }
    return map;
  }, [run]);

  const cancellable = run && (run.status === 'queued' || run.status === 'running');
  const retryable = run && (run.status === 'failed' || run.status === 'cancelled');

  return (
    <View style={styles.detail}>
      <View style={styles.detailPreview}>
        {detail && <AuthedImage path={detail.previewUrl} personal style={styles.detailImage} />}
      </View>
      <ScrollView style={styles.detailPanel} contentContainerStyle={styles.detailPanelContent}>
        <FocusableButton label={t('beautyLab.close')} hasTVPreferredFocus onPress={onClose} />
        {err && <Text style={styles.error}>{err}</Text>}
        <Text style={styles.detailLine}>{t('beautyLab.detailStatus')}: {statusLabel(t, run?.status ?? null)}</Text>
        {!run && <Text style={styles.detailLine}>{t('beautyLab.notAnalyzed')}</Text>}
        {run && (
          <>
            {METRIC_GROUPS.map((g) => {
              const ms = byGroup.get(g) ?? [];
              if (ms.length === 0) return null;
              return (
                <View key={g} style={styles.group}>
                  <Text style={styles.groupTitle}>{t(`beautyLab.group.${g}` as TvMessageKey)}</Text>
                  {ms.map((m) => (
                    <View key={m.key} style={styles.metricRow}>
                      <Text style={styles.metricName}>{metricLabel(t, m.key)}</Text>
                      <Text style={styles.metricValue}>{m.value.toFixed(1)}/{m.scaleMax.toFixed(0)}</Text>
                    </View>
                  ))}
                </View>
              );
            })}
            <Text style={styles.detailLine}>{t('beautyLab.model')}: {run.modelName ?? '—'}{run.modelRevision ? ` (${run.modelRevision})` : ''}</Text>
            <Text style={styles.detailLine}>{t('beautyLab.runtime')}: {run.runtimeName ?? '—'}{run.runtimeVersion ? ` ${run.runtimeVersion}` : ''}</Text>
            <Text style={styles.detailLine}>{t('beautyLab.preprocessing')}: {run.preprocessingProfileKey}</Text>
            <Text style={styles.detailLine}>{t('beautyLab.duration')}: {run.durationMs != null ? `${run.durationMs} ms` : '—'}</Text>
            {run.warnings.map((w, i) => <Text key={i} style={styles.warning}>{w}</Text>)}
            {run.errorCode && <Text style={styles.error}>{t('beautyLab.errorPrefix')}: {run.errorCode}</Text>}
            {cancellable && (
              <FocusableButton label={t('beautyLab.cancel')} onPress={() => {
                cancelBeautyLabRun(run.id).then(() => { onChanged(); onClose(); }).catch((e) => { if (!onError(e)) setErr(t('beautyLab.loadError')); });
              }} />
            )}
            {retryable && (
              <FocusableButton label={t('beautyLab.retry')} onPress={() => {
                retryBeautyLabRun(run.id).then(() => { onChanged(); onClose(); }).catch((e) => { if (!onError(e)) setErr(t('beautyLab.loadError')); });
              }} />
            )}
          </>
        )}
        {detail && detail.history.length > 0 && (
          <View style={styles.group}>
            <Text style={styles.groupTitle}>{t('beautyLab.history')}</Text>
            {detail.history.map((h) => (
              <Text key={h.id} style={styles.detailLine}>
                {statusLabel(t, h.status)} · {h.overallScore != null ? `${h.overallScore.toFixed(1)}/10` : '—'}
              </Text>
            ))}
          </View>
        )}
      </ScrollView>
    </View>
  );
}

// ── Compare ─────────────────────────────────────────────────────────────────

function BeautyLabCompare({
  ids, onClose, onError,
}: {
  ids: string[];
  onClose: () => void;
  onError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [columns, setColumns] = useState<{ id: string; name: string; metrics: Map<string, number> }[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const cols: { id: string; name: string; metrics: Map<string, number> }[] = [];
      for (const id of ids) {
        try {
          const d = await getBeautyLabItem(id);
          const run = d.latestRun;
          if (!run || run.status !== 'succeeded' || run.metrics.length === 0) continue;
          const m = new Map<string, number>();
          for (const metric of run.metrics) m.set(metric.key, metric.value);
          cols.push({ id, name: d.originalFileName, metrics: m });
        } catch (e) { if (onError(e)) return; }
      }
      if (!cancelled) { setColumns(cols); setLoading(false); }
    })();
    return () => { cancelled = true; };
  }, [ids, onError]);

  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => { onClose(); return true; });
    return () => sub.remove();
  }, [onClose]);

  const metricKeys = useMemo(() => {
    const keys: string[] = [];
    for (const c of columns) for (const k of c.metrics.keys()) if (!keys.includes(k)) keys.push(k);
    return keys;
  }, [columns]);

  return (
    <View style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.title}>{t('beautyLab.compareTitle')}</Text>
        <FocusableButton label={t('beautyLab.close')} hasTVPreferredFocus onPress={onClose} />
      </View>
      <Text style={styles.notice}>{t('beautyLab.compareHint')}</Text>
      {loading ? (
        <Text style={styles.detailLine}>{t('beautyLab.loading')}</Text>
      ) : columns.length === 0 ? (
        <Text style={styles.empty}>{t('beautyLab.compareEmpty')}</Text>
      ) : (
        <ScrollView horizontal>
          <View>
            <View style={styles.compareRow}>
              <Text style={[styles.compareCell, styles.compareHead]}>·</Text>
              {columns.map((c) => (
                <Text key={c.id} style={[styles.compareCell, styles.compareHead]} numberOfLines={1}>{c.name}</Text>
              ))}
            </View>
            {metricKeys.map((key) => {
              const values = columns.map((c) => c.metrics.get(key));
              const max = Math.max(...values.filter((v): v is number => v != null));
              return (
                <View key={key} style={styles.compareRow}>
                  <Text style={[styles.compareCell, styles.compareLabel]} numberOfLines={1}>{metricLabel(t, key)}</Text>
                  {values.map((v, i) => (
                    <Text
                      key={columns[i].id}
                      style={[styles.compareCell, v != null && v === max ? styles.compareBest : null]}
                    >
                      {v != null ? v.toFixed(1) : '—'}
                    </Text>
                  ))}
                </View>
              );
            })}
          </View>
        </ScrollView>
      )}
    </View>
  );
}

// ── QR upload ───────────────────────────────────────────────────────────────

function BeautyLabQr({
  onClose, onError,
}: {
  onClose: () => void;
  onError: (err: unknown) => boolean;
}) {
  const { t } = useI18n();
  const [session, setSession] = useState<BeautyLabUploadSession | null>(null);
  const [counts, setCounts] = useState({ accepted: 0, rejected: 0 });
  const [secondsLeft, setSecondsLeft] = useState<number | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const sessionIdRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    createBeautyLabUploadSession()
      .then((s) => {
        if (cancelled) { void revokeBeautyLabUploadSession(s.id).catch(() => {}); return; }
        sessionIdRef.current = s.id;
        setSession(s);
      })
      .catch((e) => { if (!onError(e)) setErr(t('beautyLab.loadError')); });
    return () => {
      cancelled = true;
      const id = sessionIdRef.current;
      if (id) void revokeBeautyLabUploadSession(id).catch(() => {});
    };
  }, [onError, t]);

  // Poll ONLY this session while the QR screen is open (transient timer).
  useEffect(() => {
    if (!session) return;
    const timer = setInterval(() => {
      getBeautyLabUploadSession(session.id)
        .then((s) => setCounts({ accepted: s.accepted, rejected: s.rejected }))
        .catch((e) => onError(e));
    }, QR_POLL_MS);
    return () => clearInterval(timer);
  }, [session, onError]);

  useEffect(() => {
    if (!session) return;
    const expires = new Date(session.expiresAt).getTime();
    const tick = () => setSecondsLeft(Math.max(0, Math.round((expires - Date.now()) / 1000)));
    tick();
    const timer = setInterval(tick, 1_000);
    return () => clearInterval(timer);
  }, [session]);

  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => { onClose(); return true; });
    return () => sub.remove();
  }, [onClose]);

  return (
    <View style={styles.qr}>
      <Text style={styles.title}>{t('beautyLab.qrTitle')}</Text>
      <Text style={styles.notice}>{t('beautyLab.qrInstructions')}</Text>
      {err && <Text style={styles.error}>{err}</Text>}
      {session && <QrCode value={`${getBaseUrl()}${session.uploadUrl}`} size={320} accessibilityLabel={t('beautyLab.qrTitle')} />}
      <Text style={styles.detailLine}>
        {t('beautyLab.qrCounts', { accepted: String(counts.accepted), rejected: String(counts.rejected) })}
      </Text>
      {secondsLeft != null && (
        <Text style={styles.detailLine}>
          {secondsLeft > 0 ? t('beautyLab.qrExpiry', { seconds: String(secondsLeft) }) : t('beautyLab.qrExpired')}
        </Text>
      )}
      <FocusableButton label={t('beautyLab.close')} hasTVPreferredFocus onPress={onClose} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg, padding: spacing.lg },
  header: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginBottom: spacing.md },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '700' },
  headerInfo: { color: colors.accent, fontSize: font.body },
  notice: { color: colors.muted, fontSize: font.caption, marginBottom: spacing.sm },
  error: { color: colors.danger, fontSize: font.body, marginVertical: spacing.xs },
  empty: { color: colors.muted, fontSize: font.body, textAlign: 'center', marginTop: spacing.xl },
  grid: { gap: spacing.md },
  tile: { flex: 1 / GRID_COLUMNS, margin: spacing.xs },
  thumbBox: { width: '100%', aspectRatio: 1, backgroundColor: colors.panel, borderRadius: 8, overflow: 'hidden' },
  thumb: { width: '100%', height: '100%' },
  selectedBadge: { position: 'absolute', top: 8, right: 8, backgroundColor: colors.accent, borderRadius: 16, width: 32, height: 32, alignItems: 'center', justifyContent: 'center' },
  badgeText: { color: colors.text, fontSize: font.body, fontWeight: '700' },
  tileMeta: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: spacing.xs, paddingTop: spacing.xs },
  tileStatus: { color: colors.muted, fontSize: font.caption, flexShrink: 1 },
  tileScore: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
  tileError: { color: colors.danger, fontSize: font.caption, fontWeight: '700' },
  menu: { position: 'absolute', top: 0, right: 0, bottom: 0, width: 420, backgroundColor: colors.panel, padding: spacing.lg, gap: spacing.sm },
  menuTitle: { color: colors.text, fontSize: font.heading, fontWeight: '700', marginBottom: spacing.sm },
  // detail
  detail: { flex: 1, flexDirection: 'row', backgroundColor: colors.bg },
  detailPreview: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: spacing.lg },
  detailImage: { width: '100%', height: '100%' },
  detailPanel: { width: 620, backgroundColor: colors.panel },
  detailPanelContent: { padding: spacing.lg, gap: spacing.xs },
  detailLine: { color: colors.text, fontSize: font.body },
  group: { marginTop: spacing.sm },
  groupTitle: { color: colors.accent, fontSize: font.body, fontWeight: '700', marginBottom: spacing.xs },
  metricRow: { flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 2 },
  metricName: { color: colors.muted, fontSize: font.caption },
  metricValue: { color: colors.text, fontSize: font.caption, fontWeight: '600' },
  warning: { color: colors.danger, fontSize: font.caption },
  // compare
  compareRow: { flexDirection: 'row' },
  compareCell: { width: 200, padding: spacing.sm, color: colors.text, fontSize: font.caption, textAlign: 'center', borderWidth: 1, borderColor: colors.bg },
  compareHead: { backgroundColor: colors.panelFocused, fontWeight: '700' },
  compareLabel: { textAlign: 'left', color: colors.muted },
  compareBest: { backgroundColor: colors.accent, fontWeight: '700' },
  // qr
  qr: { flex: 1, backgroundColor: colors.bg, alignItems: 'center', justifyContent: 'center', gap: spacing.md, padding: spacing.xl },
});
