import { useEffect, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { cancelMailRun, getMailRun, listMailRunFailures } from './mailApi';
import { useRunPolling } from './useRunPolling';
import { isRunActive, type MailRun, type MailRunFailure, type MailRunPhase, type MailRunStatus } from './types';

export const RUN_STATUS_KEYS: Record<MailRunStatus, MessageKey> = {
  queued: 'mail.run.status.queued',
  running: 'mail.run.status.running',
  succeeded: 'mail.run.status.succeeded',
  partial: 'mail.run.status.partial',
  failed: 'mail.run.status.failed',
  cancelled: 'mail.run.status.cancelled',
};

const PHASE_KEYS: Record<MailRunPhase, MessageKey> = {
  preparing: 'mail.run.phase.preparing',
  scanning: 'mail.run.phase.scanning',
  importing: 'mail.run.phase.importing',
  finalizing: 'mail.run.phase.finalizing',
};

interface MailRunPanelProps {
  run: MailRun;
  // Called once when a followed run reaches a terminal status.
  onTerminal?: (run: MailRun) => void;
}

// One import run: status, phase, progress and the counters, live while it is
// queued or running (polled every ~2 s), plus "Interrompi" and the list of
// messages that could not be imported. Keyed by run id by its parent, so a
// resumed import is a fresh panel with its own polling.
export function MailRunPanel({ run: initial, onTerminal }: MailRunPanelProps) {
  const { t, formatNumber, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const { run, pollError, replace } = useRunPolling(initial, onTerminal);
  const [cancelling, setCancelling] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [showFailures, setShowFailures] = useState(false);
  const active = isRunActive(run);
  const n = (v: number) => formatNumber(v);

  async function cancel() {
    setCancelling(true);
    setActionError(null);
    try {
      replace(await cancelMailRun(run.id));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        // `not_active`: it finished meanwhile. Show what it finished as.
        try { replace(await getMailRun(run.id)); } catch { /* the poll will catch up */ }
        return;
      }
      setActionError(t('mail.run.cancelError'));
    } finally {
      setCancelling(false);
    }
  }

  const total = run.totalMessages;
  const processed = Math.min(run.processedMessages, total || run.processedMessages);
  const percent = total > 0 ? Math.round((processed / total) * 100) : null;

  return (
    <div className={`mail-run is-${run.status}`} data-testid="mail-run" data-status={run.status}>
      <p className="mail-run-status" role="status" aria-live="polite">
        <strong>{t(RUN_STATUS_KEYS[run.status])}</strong>
        {active && run.phase && <> · {t(PHASE_KEYS[run.phase])}</>}
        {run.cancellationRequested && active && <> · {t('mail.run.cancelRequested')}</>}
      </p>

      {(active || total > 0) && (
        <div className="mail-run-progress">
          <progress
            max={total > 0 ? total : undefined}
            value={total > 0 ? processed : undefined}
            aria-label={t('mail.run.progressLabel')}
          />
          <span className="muted">
            {total > 0
              ? t('mail.run.progress', { processed: n(processed), total: n(total), percent: percent ?? 0 })
              : t('mail.run.progressUnknown')}
          </span>
        </div>
      )}

      <dl className="mail-run-counters">
        <dt>{t('mail.run.imported')}</dt><dd>{n(run.importedMessages)}</dd>
        <dt>{t('mail.run.alreadyPresent')}</dt><dd>{n(run.alreadyPresentMessages)}</dd>
        <dt>{t('mail.run.skippedDeleted')}</dt><dd>{n(run.skippedDeletedMessages)}</dd>
        <dt>{t('mail.run.failed')}</dt><dd>{n(run.failedMessages)}</dd>
        <dt>{t('mail.run.attachmentsCreated')}</dt><dd>{n(run.attachmentsCreated)}</dd>
        <dt>{t('mail.run.attachmentsReused')}</dt><dd>{n(run.attachmentsReused)}</dd>
        <dt>{t('mail.run.attachmentsSkipped')}</dt><dd>{n(run.attachmentsSkipped)}</dd>
      </dl>

      {run.completedAt && !active && (
        <p className="muted mail-run-when">
          {t('mail.run.completedAt', { date: formatDate(run.completedAt, { dateStyle: 'medium', timeStyle: 'short' }) })}
        </p>
      )}
      {run.errorMessage && <p className="inline-error" role="alert">{run.errorMessage}</p>}
      {pollError && <p className="muted" role="status">{t('mail.run.pollError')}</p>}
      {actionError && <p className="inline-error" role="alert">{actionError}</p>}

      <div className="details-actions">
        {active && (
          <button
            type="button"
            className="files-action files-action-destructive"
            onClick={() => void cancel()}
            disabled={cancelling || run.cancellationRequested}
            data-testid="mail-run-cancel"
          >
            {t('mail.run.cancel')}
          </button>
        )}
        {run.failedMessages > 0 && (
          <button
            type="button"
            className="files-action"
            aria-expanded={showFailures}
            onClick={() => setShowFailures((v) => !v)}
            data-testid="mail-run-failures-toggle"
          >
            {showFailures ? t('mail.run.hideFailures') : t('mail.run.showFailures')}
          </button>
        )}
      </div>

      {showFailures && <MailRunFailures runId={run.id} />}
    </div>
  );
}

function MailRunFailures({ runId }: { runId: string }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [items, setItems] = useState<MailRunFailure[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [state, setState] = useState<'idle' | 'loading' | 'error'>('idle');

  async function load(from: string | null) {
    setState('loading');
    try {
      const page = await listMailRunFailures(runId, from);
      setItems((prev) => (from === null ? page.items : [...prev, ...page.items]));
      setCursor(page.nextCursor ?? null);
      setState('idle');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setState('error');
    }
  }

  useEffect(() => {
    void load(null);
    // Loaded once per run when the list is opened.
  }, [runId]);

  return (
    <div className="mail-failures" data-testid="mail-run-failures">
      {items.length > 0 && (
        <table className="mail-failures-table">
          <caption className="visually-hidden">{t('mail.failures.caption')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('mail.failures.folder')}</th>
              <th scope="col">{t('mail.failures.position')}</th>
              <th scope="col">{t('mail.failures.code')}</th>
            </tr>
          </thead>
          <tbody>
            {items.map((f, i) => (
              <tr key={`${f.position}-${i}`}>
                <td>{f.folderPath.join(' / ')}</td>
                <td>{f.position}</td>
                <td>
                  <code>{f.code}</code>
                  {f.message && <span className="muted"> — {f.message}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {state === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
      {state === 'error' && <p className="inline-error" role="alert">{t('mail.failures.error')}</p>}
      {state === 'idle' && items.length === 0 && <p className="muted">{t('mail.failures.none')}</p>}
      {cursor && state !== 'loading' && (
        <button type="button" className="files-action" onClick={() => void load(cursor)}>{t('common.loadMore')}</button>
      )}
    </div>
  );
}
