import { useEffect, useState } from 'react';
import {
  ApiError,
  getExactMediaDuplicateCleanupStatus,
  MEDIA_DUPLICATE_CLEANUP_TERMINAL,
  startExactMediaDuplicateCleanup,
  type MediaDuplicateCleanupStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

export function ExactMediaDuplicatesPanel() {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [runId, setRunId] = useState<string | null>(null);
  const [status, setStatus] = useState<MediaDuplicateCleanupStatus | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!runId || (status && MEDIA_DUPLICATE_CLEANUP_TERMINAL.has(status.status))) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const next = await getExactMediaDuplicateCleanupStatus(runId, controller.signal);
        setStatus(next);
        if (!MEDIA_DUPLICATE_CLEANUP_TERMINAL.has(next.status)) timer = setTimeout(poll, 1000);
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setError(t('cloud.dedupeStatusError'));
      }
    };
    void poll();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [runId, status?.status, invalidateAuth, t]);

  async function start() {
    if (!window.confirm(t('cloud.dedupeConfirm'))) return;
    setStarting(true);
    setError(null);
    setStatus(null);
    try {
      const created = await startExactMediaDuplicateCleanup();
      setRunId(created.runId);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      setError(t('cloud.dedupeStartError'));
    } finally {
      setStarting(false);
    }
  }

  const running = starting
    || (!!runId && (!status || !MEDIA_DUPLICATE_CLEANUP_TERMINAL.has(status.status)));

  return (
    <section className="cloud-tool-body">
      <p className="muted">{t('cloud.dedupeHint')}</p>
      <p className="muted">{t('cloud.dedupeTrashNote')}</p>
      {error && <div className="folder-error" role="alert">{error}</div>}
      {status && (
        <div role="status" aria-live="polite" data-testid="cf-dedupe-status">
          {status.status === 'succeeded' ? (
            <dl className="organizer-summary">
              <div><dt>{t('cloud.dedupeGroups')}</dt><dd>{status.duplicateGroupCount}</dd></div>
              <div><dt>{t('cloud.dedupeRemoved')}</dt><dd>{status.filesRemovedCount}</dd></div>
              <div><dt>{t('cloud.dedupeRetained')}</dt><dd>{status.filesRetainedCount}</dd></div>
            </dl>
          ) : status.status === 'failed' ? (
            <p>{t('cloud.dedupeFailed')}</p>
          ) : status.status === 'cancelled' ? (
            <p>{t('cloud.dedupeCancelled')}</p>
          ) : (
            <p>{t('cloud.dedupeRunning')}</p>
          )}
        </div>
      )}
      <button
        type="button"
        className="row-action-primary"
        data-testid="cf-dedupe"
        disabled={running}
        onClick={() => void start()}
      >
        {running ? t('cloud.dedupeRunning') : t('cloud.dedupeBtn')}
      </button>
    </section>
  );
}
