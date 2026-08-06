import { useCallback, useEffect, useId, useState } from 'react';
import type { ChangeEvent } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  listShareLinks,
  revokeShareLink,
  type ShareLinkListItem,
  type ShareLinkListResponse,
  type ShareLinkStatusFilter,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

const DEFAULT_LIMIT = 50;

const STATUS_OPTIONS: ReadonlyArray<{ value: ShareLinkStatusFilter; labelKey: MessageKey }> = [
  { value: 'all', labelKey: 'shares.filterAll' },
  { value: 'active', labelKey: 'shares.filterActive' },
  { value: 'expired', labelKey: 'shares.filterExpired' },
  { value: 'revoked', labelKey: 'shares.filterRevoked' },
];

type StatusKind = 'active' | 'revoked' | 'expired' | 'exhausted';

// Same precedence as the per-file ShareLinkPanel: an explicit revocation
// overrides natural expiry / exhaustion.
function statusOf(link: ShareLinkListItem): StatusKind {
  if (link.isRevoked) return 'revoked';
  if (link.isExpired) return 'expired';
  if (link.isExhausted) return 'exhausted';
  return 'active';
}

const STATUS_LABEL_KEY: Record<StatusKind, MessageKey> = {
  active: 'shares.statusActive',
  revoked: 'shares.statusRevoked',
  expired: 'shares.statusExpired',
  exhausted: 'shares.statusExhausted',
};

interface AppliedQuery {
  status: ShareLinkStatusFilter;
  offset: number;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; data: ShareLinkListResponse }
  | { kind: 'error'; message: string };

interface BannerMessage {
  tone: 'info' | 'error';
  text: string;
}

export function SharesPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const statusId = useId();

  const [applied, setApplied] = useState<AppliedQuery>({ status: 'all', offset: 0 });
  const [status, setStatus] = useState<LoadState>({ kind: 'loading' });
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(new Set());
  const [banner, setBanner] = useState<BannerMessage | null>(null);

  const load = useCallback(
    async (query: AppliedQuery, signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      try {
        const data = await listShareLinks(
          { status: query.status, limit: DEFAULT_LIMIT, offset: query.offset },
          signal,
        );
        setStatus({ kind: 'ready', data });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setStatus({
          kind: 'error',
          message: t('shares.loadError'),
        });
      }
    },
    [invalidateAuth, t],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(applied, controller.signal);
    return () => controller.abort();
  }, [applied, load]);

  function onStatusChange(event: ChangeEvent<HTMLSelectElement>) {
    const next = event.target.value as ShareLinkStatusFilter;
    setBanner(null);
    setApplied({ status: next, offset: 0 });
  }

  function onPrev() {
    setApplied((prev) => ({ ...prev, offset: Math.max(0, prev.offset - DEFAULT_LIMIT) }));
  }

  function onNext() {
    setApplied((prev) => ({ ...prev, offset: prev.offset + DEFAULT_LIMIT }));
  }

  function markBusy(id: string, busy: boolean) {
    setBusyIds((prev) => {
      const next = new Set(prev);
      if (busy) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  async function onRevoke(link: ShareLinkListItem) {
    if (busyIds.has(link.id)) return;
    const confirmed = window.confirm(
      t('shares.confirmRevoke', { name: link.fileName }),
    );
    if (!confirmed) return;

    markBusy(link.id, true);
    setBanner(null);
    try {
      await revokeShareLink(link.id);
      setBanner({ tone: 'info', text: t('shares.revoked', { name: link.fileName }) });
      await load(applied);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 404) {
        // Already gone (concurrent revoke / purge). Refresh so the row drops.
        setBanner({ tone: 'info', text: t('shares.gone') });
        await load(applied);
        return;
      }
      setBanner({ tone: 'error', text: t('shares.revokeError') });
    } finally {
      markBusy(link.id, false);
    }
  }

  if (state.status !== 'authed') {
    // ProtectedRoute already enforces this; the check keeps TypeScript happy.
    return null;
  }

  const items = status.kind === 'ready' ? status.data.items : [];
  const total = status.kind === 'ready' ? status.data.total : 0;
  const offset = applied.offset;
  const isLoading = status.kind === 'loading';
  const canPrev = offset > 0 && !isLoading;
  const canNext = status.kind === 'ready' && offset + items.length < total;
  const rangeStart = total === 0 ? 0 : offset + 1;
  const rangeEnd = offset + items.length;

  return (
    <section className="shares-page" aria-busy={isLoading}>
      <header className="shares-header">
        <h2>{t('shares.heading')}</h2>
        <div className="shares-controls">
          <label htmlFor={statusId} className="shares-filter-label">
            {t('common.status')}
          </label>
          <select
            id={statusId}
            value={applied.status}
            onChange={onStatusChange}
            className="shares-select"
          >
            {STATUS_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {t(opt.labelKey)}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="refresh-button"
            onClick={() => void load(applied)}
            disabled={isLoading}
          >
            {t('common.refresh')}
          </button>
        </div>
      </header>

      {banner !== null && (
        <p
          className={`shares-banner shares-banner-${banner.tone}`}
          role={banner.tone === 'error' ? 'alert' : 'status'}
        >
          {banner.text}
        </p>
      )}

      {status.kind === 'loading' && (
        <p className="muted" role="status">
          {t('shares.loading')}
        </p>
      )}

      {status.kind === 'error' && (
        <div className="folder-error" role="alert">
          {status.message}
          <button type="button" className="retry-button" onClick={() => void load(applied)}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {status.kind === 'ready' && items.length === 0 && (
        <p className="muted">
          {applied.status === 'all'
            ? t('shares.emptyAll')
            : applied.status === 'active'
              ? t('shares.emptyActive')
              : applied.status === 'expired'
                ? t('shares.emptyExpired')
                : t('shares.emptyRevoked')}
        </p>
      )}

      {status.kind === 'ready' && items.length > 0 && (
        <ul className="shares-list" aria-label={t('shares.listLabel')}>
          {items.map((link) => (
            <ShareRow
              key={link.id}
              link={link}
              busy={busyIds.has(link.id)}
              onRevoke={() => void onRevoke(link)}
            />
          ))}
        </ul>
      )}

      <nav className="shares-pagination" aria-label={t('shares.paginationLabel')}>
        <button type="button" className="row-action" onClick={onPrev} disabled={!canPrev}>
          {t('common.previous')}
        </button>
        <span className="muted shares-page-info">
          {total === 0 ? t('shares.noLinks') : t('shares.showing', { start: rangeStart, end: rangeEnd, total })}
        </span>
        <button type="button" className="row-action" onClick={onNext} disabled={!canNext}>
          {t('common.next')}
        </button>
      </nav>
    </section>
  );
}

interface ShareRowProps {
  link: ShareLinkListItem;
  busy: boolean;
  onRevoke(): void;
}

function ShareRow({ link, busy, onRevoke }: ShareRowProps) {
  const { t, formatDate } = useI18n();
  const kind = statusOf(link);
  const path = link.folderPath ?? '/';
  // Display the file in its logical location. Avoid a double slash at root.
  const location = path === '/' ? `/${link.fileName}` : `${path}/${link.fileName}`;

  return (
    <li className={`shares-row shares-row-${kind}`}>
      <div className="shares-row-main">
        <span className="shares-row-name" title={location}>
          {link.fileName}
        </span>
        <span className="shares-row-path muted" title={location}>
          {path}
        </span>
        <span className="shares-row-meta">
          {t('shares.createdAt', { date: formatDate(link.createdAt) })}
          {' · '}
          {link.expiresAt !== null
            ? t('shares.expiresAt', { date: formatDate(link.expiresAt) })
            : t('shares.noExpiry')}
          {' · '}
          {link.maxDownloads !== null
            ? t('shares.downloadsMax', { count: link.downloadCount, max: link.maxDownloads })
            : t('shares.downloads', { count: link.downloadCount })}
        </span>
      </div>
      <div className="shares-row-side">
        <span className={`share-status-badge share-status-${kind}`}>{t(STATUS_LABEL_KEY[kind])}</span>
        {!link.isRevoked && (
          <button
            type="button"
            className="destructive-button"
            onClick={onRevoke}
            disabled={busy}
          >
            {busy ? t('shares.revoking') : t('shares.revoke')}
          </button>
        )}
      </div>
    </li>
  );
}
