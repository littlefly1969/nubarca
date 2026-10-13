import { memo, useEffect, useMemo, useRef, useState } from 'react';
import { useI18n } from '../i18n';
import type { MessageKey } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { formatSize } from '../components/format';
import type { UploadFailure } from './engine/classify';
import { isFinished, type UploadItem, type UploadSnapshot } from './engine/uploadManager';
import { useUploads } from './UploadProvider';
import './UploadTray.css';

// THE UPLOAD TRAY. One place, in the corner of every page, where everything
// being sent can be watched, paused, retried or cancelled — and where a file
// that did not make it says why.
//
// Collapsed it is a single pill with the overall progress; open it is the
// list. Closing it never stops anything: the queue belongs to the shell.

const FAILURE_KEY: Record<UploadFailure, MessageKey> = {
  auth: 'uploadTray.failure.auth',
  too_large: 'uploadTray.failure.tooLarge',
  quota: 'uploadTray.failure.quota',
  rejected: 'uploadTray.failure.rejected',
  name_conflict: 'uploadTray.failure.nameConflict',
  missing_target: 'uploadTray.failure.missingTarget',
  not_permitted: 'uploadTray.failure.notPermitted',
  not_media: 'uploadTray.failure.notMedia',
  network: 'uploadTray.failure.network',
  server: 'uploadTray.failure.server',
  unknown: 'uploadTray.failure.unknown',
};

// Rows rendered at once. A thousand-photo folder still shows its first rows
// and an honest count of the rest; failures are always reachable through the
// filter.
const MAX_ROWS = 300;

// A thumbnail is decoded from the local file only below this size, so a tray
// full of 40-megapixel originals never stalls the page.
const MAX_THUMBNAIL_BYTES = 25 * 1024 * 1024;

export interface UploadTotals {
  total: number;
  done: number;
  failed: number;
  cancelled: number;
  active: number;
  totalBytes: number;
  sentBytes: number;
  waiting: number;
}

export function computeTotals(items: readonly UploadItem[]): UploadTotals {
  const totals: UploadTotals = { total: 0, done: 0, failed: 0, cancelled: 0, active: 0, totalBytes: 0, sentBytes: 0, waiting: 0 };
  for (const item of items) {
    if (item.phase === 'cancelled') {
      totals.cancelled++;
      continue;
    }
    totals.total++;
    totals.totalBytes += item.size;
    totals.sentBytes += item.phase === 'done' ? item.size : item.sentBytes;
    if (item.phase === 'done') totals.done++;
    else if (item.phase === 'failed') totals.failed++;
    else totals.active++;
    if (item.phase === 'waiting') totals.waiting++;
  }
  return totals;
}

// Bytes per second over the last few seconds of the run, for the time left.
function useThroughput(sentBytes: number, running: boolean): number | null {
  const samples = useRef<Array<{ at: number; bytes: number }>>([]);
  const [rate, setRate] = useState<number | null>(null);
  useEffect(() => {
    if (!running) {
      samples.current = [];
      setRate(null);
      return;
    }
    const now = Date.now();
    const list = samples.current;
    list.push({ at: now, bytes: sentBytes });
    while (list.length > 2 && now - list[0].at > 8000) list.shift();
    const first = list[0];
    const elapsed = (now - first.at) / 1000;
    if (elapsed >= 1.5 && sentBytes >= first.bytes) setRate((sentBytes - first.bytes) / elapsed);
  }, [sentBytes, running]);
  return rate;
}

function useDuration() {
  const { t, tn } = useI18n();
  return (seconds: number): string => {
    if (seconds < 60) return tn(Math.max(1, Math.round(seconds)), 'uploadTray.seconds');
    if (seconds < 3600) return tn(Math.round(seconds / 60), 'uploadTray.minutes');
    const h = Math.floor(seconds / 3600);
    return t('uploadTray.hours', { h, m: Math.round((seconds % 3600) / 60) });
  };
}

export function UploadTray({ snapshot }: { snapshot: UploadSnapshot }) {
  const { t, tn } = useI18n();
  const { manager, trayOpen, setTrayOpen } = useUploads();
  const [filter, setFilter] = useState<'all' | 'failed'>('all');
  const totals = useMemo(() => computeTotals(snapshot.items), [snapshot.items]);
  const running = totals.active > 0 && !snapshot.paused;
  const rate = useThroughput(totals.sentBytes, running);
  const duration = useDuration();

  useEffect(() => {
    if (totals.failed === 0 && filter === 'failed') setFilter('all');
  }, [totals.failed, filter]);

  if (snapshot.items.length === 0) return null;

  const percent = totals.totalBytes > 0 ? Math.round((totals.sentBytes / totals.totalBytes) * 100) : 100;
  const finished = totals.active === 0;
  const offline = typeof navigator !== 'undefined' && navigator.onLine === false && totals.waiting > 0;

  let title: string;
  if (!finished && snapshot.paused) title = t('uploadTray.titlePaused', { done: totals.done, total: totals.total });
  else if (!finished) title = t('uploadTray.titleActive', { done: totals.done, total: totals.total });
  else if (totals.failed > 0) title = t('uploadTray.titleWithFailures', { done: totals.done, failed: totals.failed });
  else if (totals.total === 0) title = t('uploadTray.titleCancelled');
  else title = tn(totals.done, 'uploadTray.titleDone');

  let subtitle: string | null = null;
  if (offline) subtitle = t('uploadTray.offline');
  else if (!finished) {
    const parts = [t('uploadTray.bytes', { sent: formatSize(totals.sentBytes), total: formatSize(totals.totalBytes) })];
    if (running && rate && rate > 0) {
      parts.push(t('uploadTray.speed', { speed: formatSize(rate) }));
      parts.push(t('uploadTray.eta', { time: duration((totals.totalBytes - totals.sentBytes) / rate) }));
    }
    subtitle = parts.join(' · ');
  }

  if (!trayOpen) {
    return (
      <button
        type="button"
        className={`upload-tray-pill${totals.failed > 0 && finished ? ' upload-tray-pill--failed' : ''}`}
        onClick={() => setTrayOpen(true)}
        aria-label={t('uploadTray.expand')}
        data-testid="upload-tray-pill"
      >
        <ProgressRing percent={percent} done={finished && totals.failed === 0} failed={finished && totals.failed > 0} />
        <span className="upload-tray-pill__text">{title}</span>
      </button>
    );
  }

  const visible = filter === 'failed' ? snapshot.items.filter((i) => i.phase === 'failed') : snapshot.items;
  const shown = visible.slice(0, MAX_ROWS);

  return (
    <section className="upload-tray" aria-label={t('uploadTray.region')} data-testid="upload-tray">
      <header className="upload-tray__header">
        <div className="upload-tray__heading">
          <h2 className="upload-tray__title" aria-live="polite">{title}</h2>
          {subtitle && <p className="upload-tray__subtitle">{subtitle}</p>}
        </div>
        <div className="upload-tray__header-actions">
          {!finished && (
            <button
              type="button"
              className="icon-button"
              onClick={() => (snapshot.paused ? manager.resume() : manager.pause())}
              aria-label={snapshot.paused ? t('uploadTray.resume') : t('uploadTray.pause')}
              title={snapshot.paused ? t('uploadTray.resume') : t('uploadTray.pause')}
              data-testid="upload-tray-pause"
            >
              <Icon name={snapshot.paused ? 'play' : 'pause'} size={18} />
            </button>
          )}
          <button
            type="button"
            className="icon-button"
            onClick={() => setTrayOpen(false)}
            aria-label={t('uploadTray.minimize')}
            title={t('uploadTray.minimize')}
            data-testid="upload-tray-minimize"
          >
            <Icon name="chevron-down" size={18} />
          </button>
          {finished && (
            <button
              type="button"
              className="icon-button"
              onClick={() => { manager.clearFinished(); if (totals.failed === 0) setTrayOpen(false); }}
              aria-label={t('uploadTray.clear')}
              title={t('uploadTray.clear')}
              data-testid="upload-tray-clear"
            >
              <Icon name="close" size={18} />
            </button>
          )}
        </div>
      </header>

      {!finished && (
        <div className="upload-tray__bar" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent}
          aria-label={t('uploadTray.region')}>
          <div className={`upload-tray__bar-fill${snapshot.paused ? ' is-paused' : ''}`} style={{ width: `${percent}%` }} />
        </div>
      )}

      {totals.failed > 0 && (
        <div className="upload-tray__filters" role="group">
          <button type="button" className={`upload-tray__chip${filter === 'all' ? ' is-active' : ''}`}
            aria-pressed={filter === 'all'} onClick={() => setFilter('all')}>
            {t('uploadTray.filterAll', { count: snapshot.items.length })}
          </button>
          <button type="button" className={`upload-tray__chip${filter === 'failed' ? ' is-active' : ''}`}
            aria-pressed={filter === 'failed'} onClick={() => setFilter('failed')} data-testid="upload-tray-filter-failed">
            {t('uploadTray.filterFailed', { count: totals.failed })}
          </button>
        </div>
      )}

      <ul className="upload-tray__list" data-testid="upload-tray-list">
        {shown.map((item) => (
          <UploadRow
            key={item.id}
            item={item}
            onCancel={manager.cancel.bind(manager)}
            onRetry={manager.retry.bind(manager)}
            onDismiss={manager.dismiss.bind(manager)}
          />
        ))}
      </ul>
      {visible.length > shown.length && (
        <p className="upload-tray__more">{t('uploadTray.more', { count: visible.length - shown.length })}</p>
      )}

      <footer className="upload-tray__footer">
        {totals.failed > 0 && (
          <button type="button" className="upload-tray__action" onClick={() => manager.retryFailed()} data-testid="upload-tray-retry-all">
            <Icon name="restore" size={16} />{t('uploadTray.retryAll')}
          </button>
        )}
        {!finished && (
          <button type="button" className="upload-tray__action upload-tray__action--quiet" onClick={() => manager.cancelAll()}
            data-testid="upload-tray-cancel-all">
            {t('uploadTray.cancelAll')}
          </button>
        )}
      </footer>
    </section>
  );
}

function ProgressRing({ percent, done, failed }: { percent: number; done: boolean; failed: boolean }) {
  const r = 9;
  const c = 2 * Math.PI * r;
  if (done) return <span className="upload-tray-ring upload-tray-ring--done" aria-hidden="true"><Icon name="check" size={16} /></span>;
  if (failed) return <span className="upload-tray-ring upload-tray-ring--failed" aria-hidden="true">!</span>;
  return (
    <svg className="upload-tray-ring" width="24" height="24" viewBox="0 0 24 24" aria-hidden="true">
      <circle cx="12" cy="12" r={r} className="upload-tray-ring__track" />
      <circle cx="12" cy="12" r={r} className="upload-tray-ring__value"
        strokeDasharray={c} strokeDashoffset={c * (1 - percent / 100)} transform="rotate(-90 12 12)" />
    </svg>
  );
}

const UploadRow = memo(function UploadRow({ item, onCancel, onRetry, onDismiss }: {
  item: UploadItem;
  onCancel(id: string): void;
  onRetry(id: string): void;
  onDismiss(id: string): void;
}) {
  const { t } = useI18n();
  const percent = item.size > 0 ? Math.floor((item.sentBytes / item.size) * 100) : 0;
  const label = item.relativePath.length > 0 ? item.relativePath : item.name;

  let status: string;
  switch (item.phase) {
    case 'queued': status = t('uploadTray.phase.queued'); break;
    case 'uploading': status = t('uploadTray.phase.uploading', { percent }); break;
    case 'finishing': status = t('uploadTray.phase.finishing'); break;
    case 'waiting': status = t('uploadTray.phase.waiting'); break;
    case 'paused': status = t('uploadTray.phase.paused'); break;
    case 'cancelled': status = t('uploadTray.phase.cancelled'); break;
    case 'done':
      status = item.resultName && item.resultName !== item.name
        ? t('uploadTray.phase.renamed', { name: item.resultName })
        : t('uploadTray.phase.done');
      break;
    default:
      status = t(FAILURE_KEY[item.failure ?? 'unknown']);
  }
  const showBar = item.phase === 'uploading' || item.phase === 'finishing' || item.phase === 'waiting'
    || (item.phase === 'paused' && item.sentBytes > 0);

  return (
    <li className={`upload-row upload-row--${item.phase}`} data-testid="upload-row" data-phase={item.phase}>
      <Thumbnail file={item.file} />
      <div className="upload-row__body">
        <div className="upload-row__line">
          <span className="upload-row__name" title={label}>{label}</span>
          <span className="upload-row__size">{formatSize(item.size)}</span>
        </div>
        {showBar && (
          <div className="upload-row__bar" aria-hidden="true">
            <div className="upload-row__bar-fill" style={{ width: `${percent}%` }} />
          </div>
        )}
        <div className="upload-row__line">
          <span className="upload-row__status" title={item.detail ?? undefined}>
            {status}
            {item.phase === 'failed' && item.detail && item.failure === 'rejected' ? ` · ${item.detail}` : ''}
          </span>
          <span className="upload-row__target">{t('uploadTray.to', { target: item.target.label })}</span>
        </div>
      </div>
      <div className="upload-row__actions">
        {item.phase === 'failed' && item.failure !== 'too_large' && item.failure !== 'rejected' && (
          <button type="button" className="icon-button" onClick={() => onRetry(item.id)}
            aria-label={t('uploadTray.retry')} title={t('uploadTray.retry')}>
            <Icon name="restore" size={16} />
          </button>
        )}
        {!isFinished(item.phase) ? (
          <button type="button" className="icon-button" onClick={() => onCancel(item.id)}
            aria-label={t('uploadTray.cancel')} title={t('uploadTray.cancel')}>
            <Icon name="close" size={16} />
          </button>
        ) : (
          <button type="button" className="icon-button" onClick={() => onDismiss(item.id)}
            aria-label={t('uploadTray.dismiss')} title={t('uploadTray.dismiss')}>
            <Icon name={item.phase === 'done' ? 'check' : 'close'} size={16} />
          </button>
        )}
      </div>
    </li>
  );
});

function Thumbnail({ file }: { file: File }) {
  const isImage = file.type.startsWith('image/') && file.size <= MAX_THUMBNAIL_BYTES;
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!isImage || typeof URL.createObjectURL !== 'function') return undefined;
    const objectUrl = URL.createObjectURL(file);
    setUrl(objectUrl);
    return () => URL.revokeObjectURL(objectUrl);
  }, [file, isImage]);
  if (url) {
    return <img className="upload-row__thumb" src={url} alt="" loading="lazy" decoding="async" />;
  }
  return (
    <span className="upload-row__thumb upload-row__thumb--icon" aria-hidden="true">
      <Icon name={file.type.startsWith('video/') ? 'video' : file.type.startsWith('image/') ? 'photo' : 'files'} size={18} />
    </span>
  );
}
