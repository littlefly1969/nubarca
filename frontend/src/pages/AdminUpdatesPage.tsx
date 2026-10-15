import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  cancelPrintBoxUpdate,
  getSystemUpdate,
  listPrintBoxUpdates,
  requestAllPrintBoxUpdates,
  requestPrintBoxUpdate,
  requestSystemUpdateApply,
  requestSystemUpdateCheck,
  type PrintBoxUpdate,
  type SystemUpdate,
  type SystemUpdateReport,
  type SystemUpdateRun,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

// The administrator's Updates page. "Check" asks the host updater what the
// newest signed release would change here; "Install" asks it to install
// exactly that release, with the migration answer that check required. Every
// gate (signature, migration policy, backup, smoke checks, rollback) runs on
// the host; this page only asks and shows what the updater reports. Print
// Boxes follow the server: each installs the published agent when asked.

const POLL_MS = 4000;

const ERROR_KEYS: Record<string, MessageKey> = {
  updater_unavailable: 'updates.error.updaterUnavailable',
  update_busy: 'updates.error.busy',
  check_again: 'updates.error.checkAgain',
  not_deployable: 'updates.error.notDeployable',
  no_bundle: 'updates.error.noBundle',
  up_to_date: 'updates.error.upToDate',
  manual_update: 'updates.error.manualUpdate',
};

const REASON_KEYS: Record<string, MessageKey> = {
  'release-not-published': 'updates.reason.releaseNotPublished',
  'manual-migration': 'updates.reason.manualMigration',
  'tv-artifact-missing': 'updates.reason.tvArtifactMissing',
};

const OUTCOME_KEYS: Record<string, MessageKey> = {
  ready: 'updates.outcome.ready',
  'up-to-date': 'updates.outcome.upToDate',
  'not-deployable': 'updates.outcome.notDeployable',
  applied: 'updates.outcome.applied',
  failed: 'updates.outcome.failed',
  interrupted: 'updates.outcome.interrupted',
  rejected: 'updates.outcome.rejected',
};

function errorKey(err: unknown): MessageKey {
  const code = err instanceof ApiError && typeof err.body === 'object' && err.body !== null
    ? (err.body as { error?: unknown }).error : undefined;
  return (typeof code === 'string' && ERROR_KEYS[code]) || 'updates.error.generic';
}

const short = (sha: string | null) => (sha ? sha.slice(0, 7) : '—');

export function AdminUpdatesPage() {
  const { state, invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [update, setUpdate] = useState<SystemUpdate | null>(null);
  const [boxes, setBoxes] = useState<PrintBoxUpdate[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [message, setMessage] = useState<MessageKey | null>(null);
  const [sending, setSending] = useState(false);
  // The migration confirmation belongs to ONE check: the release and the exact
  // migrations it found. A later check (another release, other migrations)
  // starts unconfirmed.
  const [confirmedFor, setConfirmedFor] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const [described, fleet] = await Promise.all([getSystemUpdate(signal), listPrintBoxUpdates(signal)]);
      setUpdate(described);
      setBoxes(fleet);
      setLoadFailed(false);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // While an install restarts the server the page simply keeps asking.
      setLoadFailed(true);
    }
  }, [invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const busy = update !== null
    && (update.requestPending || update.state === 'checking' || update.state === 'applying');
  const installing = update?.state === 'applying' || update?.current?.action === 'apply';
  const pendingBoxes = boxes?.some((box) => box.agent.updateTarget) ?? false;

  // Poll while the updater works, while a just-started install restarts the
  // server, and while Print Boxes are installing.
  useEffect(() => {
    if (!busy && !pendingBoxes && !(loadFailed && update !== null)) return;
    const timer = window.setInterval(() => void load(), POLL_MS);
    return () => window.clearInterval(timer);
  }, [busy, pendingBoxes, loadFailed, update, load]);

  const send = async (operation: () => Promise<unknown>, done: MessageKey) => {
    setSending(true);
    setMessage(null);
    try {
      await operation();
      setMessage(done);
    } catch (err) {
      setMessage(errorKey(err));
    } finally {
      setSending(false);
      await load();
    }
  };

  if (state.status !== 'authed') return null;

  const report = update?.report ?? null;
  const ready = report?.outcome === 'ready' && report.candidateSha !== null && report.releaseId !== null;
  const confirmationKey = report
    ? `${report.candidateSha}|${report.releaseId}|${report.checkedAt}|${report.migrations.join(',')}`
    : null;
  const confirmed = confirmationKey !== null && confirmedFor === confirmationKey;

  return (
    <section className="admin-page admin-updates" aria-busy={update === null && !loadFailed}>
      <header className="admin-header">
        <h2>{t('updates.heading')}</h2>
        <button type="button" className="refresh-button" onClick={() => void load()} disabled={sending}>
          {t('common.refresh')}
        </button>
      </header>

      {update === null && !loadFailed && <p className="muted" role="status">{t('updates.loading')}</p>}
      {loadFailed && (
        <div className={update ? 'muted' : 'folder-error'} role={update ? 'status' : 'alert'}>
          {t(update ? 'updates.reconnecting' : 'updates.loadError')}
        </div>
      )}
      {message && <p className="updates-message" role="status" data-testid="updates-message">{t(message)}</p>}

      {update && (
        <div className="admin-grid">
          <article className="admin-card" data-testid="updates-server">
            <h3>{t('updates.server')}</h3>
            <dl className="admin-card-list">
              <dt>{t('updates.running')}</dt>
              <dd>{update.runningVersion ?? '—'} <code>{short(update.runningSha)}</code></dd>
              <dt>{t('updates.updater')}</dt>
              <dd>
                <span className={`admin-console-badge ${update.updaterConnected ? 'admin-console-badge-clear' : 'admin-console-badge-off'}`}>
                  {t(update.updaterConnected ? 'updates.updaterConnected' : 'updates.updaterMissing')}
                </span>
              </dd>
              <dt>{t('updates.lastCheck')}</dt>
              <dd>{update.lastCheckAt ? formatDate(update.lastCheckAt) : '—'}</dd>
              {update.autoCheckHours ? (
                <>
                  <dt>{t('updates.autoCheck')}</dt>
                  <dd>{t('updates.autoCheckEvery', { hours: update.autoCheckHours })}</dd>
                </>
              ) : null}
            </dl>
            {!update.updaterConnected && <p className="muted">{t('updates.updaterMissingHelp')}</p>}
            <div className="admin-console-card-actions">
              <button type="button" className="row-action-primary" data-testid="updates-check"
                disabled={!update.updaterConnected || busy || sending}
                onClick={() => void send(requestSystemUpdateCheck, 'updates.checkRequested')}>
                {t('updates.check')}
              </button>
            </div>
            {busy && (
              <p className="muted" role="status" data-testid="updates-busy">
                {t(installing ? 'updates.installing' : 'updates.checking')}
              </p>
            )}
            {update.last && !busy && <LastRun run={update.last} formatDate={formatDate} />}
          </article>

          {report && (
            <ReportCard report={report} formatDate={formatDate}>
              {ready && (
                <div className="admin-console-card-actions">
                  {report.confirmMigrations && (
                    <label className="updates-confirm">
                      <input type="checkbox" checked={confirmed} data-testid="updates-confirm-migrations"
                        onChange={(event) => setConfirmedFor(event.target.checked ? confirmationKey : null)} />
                      {t('updates.confirmMigrations', { count: report.migrations.length })}
                    </label>
                  )}
                  <button type="button" className="row-action-primary" data-testid="updates-install"
                    disabled={!update.updaterConnected || busy || sending || (report.confirmMigrations && !confirmed)}
                    onClick={() => void send(
                      () => requestSystemUpdateApply(report.candidateSha!, report.releaseId!, report.confirmMigrations),
                      'updates.installRequested',
                    )}>
                    {t('updates.install', { version: report.candidateVersion ?? short(report.candidateSha) })}
                  </button>
                </div>
              )}
            </ReportCard>
          )}
        </div>
      )}

      {boxes && (
        <PrintBoxes boxes={boxes} sending={sending} formatDate={formatDate}
          onUpdate={(id) => void send(() => requestPrintBoxUpdate(id), 'updates.boxes.requested')}
          onCancel={(id) => void send(() => cancelPrintBoxUpdate(id), 'updates.boxes.cancelled')}
          onUpdateAll={() => void send(requestAllPrintBoxUpdates, 'updates.boxes.requested')} />
      )}
    </section>
  );
}

function LastRun({ run, formatDate }: { run: SystemUpdateRun; formatDate: (value: string) => string }) {
  const { t } = useI18n();
  const outcome = run.outcome ? OUTCOME_KEYS[run.outcome] : undefined;
  return (
    <p className="muted" data-testid="updates-last">
      {t(run.action === 'apply' ? 'updates.lastInstall' : 'updates.lastCheckRun')}{' '}
      {outcome ? t(outcome) : (run.outcome ?? '—')}
      {run.finishedAt && <> · {formatDate(run.finishedAt)}</>}
      {run.automatic && <> · {t('updates.automatic')}</>}
      {run.outcome !== 'ready' && run.outcome !== 'up-to-date' && run.outcome !== 'applied' && run.message && (
        <><br /><span className="updates-run-message">{run.message}</span></>
      )}
    </p>
  );
}

function ReportCard({ report, formatDate, children }: {
  report: SystemUpdateReport;
  formatDate: (value: string) => string;
  children?: React.ReactNode;
}) {
  const { t } = useI18n();
  const c = report.components;
  const changed: MessageKey[] = [
    ...(c.backend ? ['updates.component.backend' as const] : []),
    ...(c.frontend ? ['updates.component.frontend' as const] : []),
    ...(c.printAgent ? ['updates.component.printAgent' as const] : []),
    ...(c.tv && c.tvEnabled ? ['updates.component.tv' as const] : []),
  ];
  return (
    <article className="admin-card" data-testid="updates-report">
      <h3>{t('updates.release')}</h3>
      <p data-testid="updates-outcome">
        {report.outcome === 'up-to-date'
          ? t('updates.upToDate', { version: report.currentVersion ?? '—' })
          : report.outcome === 'ready'
            ? t('updates.available', { version: report.candidateVersion ?? '—', commit: short(report.candidateSha) })
            : t(OUTCOME_KEYS[report.outcome] ?? 'updates.outcome.notDeployable')}
      </p>
      {report.reason && REASON_KEYS[report.reason] && <p className="folder-error">{t(REASON_KEYS[report.reason])}</p>}
      <dl className="admin-card-list">
        <dt>{t('updates.checkedAt')}</dt>
        <dd>{report.checkedAt ? formatDate(report.checkedAt) : '—'}</dd>
        <dt>{t('updates.signature')}</dt>
        <dd>{t(report.signed ? 'updates.signed' : 'updates.unsigned')}</dd>
        {report.apiVariant && (<><dt>{t('updates.variant')}</dt><dd>{report.apiVariant}</dd></>)}
      </dl>
      {report.outcome !== 'up-to-date' && changed.length > 0 && (
        <p>
          {t('updates.changes')}{' '}
          {changed.map((key) => t(key, { version: c.printAgentVersion ?? '' })).join(', ')}
        </p>
      )}
      {report.migrations.length > 0 && (
        <details>
          <summary>{t('updates.migrations', { count: report.migrations.length })}</summary>
          <ul className="updates-list">{report.migrations.map((m) => <li key={m}><code>{m}</code></li>)}</ul>
        </details>
      )}
      {report.commits.length > 0 && (
        <details>
          <summary>{t('updates.commits', { count: report.commits.length })}</summary>
          <ul className="updates-list">
            {report.commits.map((commit) => (
              <li key={commit.sha}><code>{commit.sha.slice(0, 7)}</code> {commit.subject}</li>
            ))}
          </ul>
        </details>
      )}
      {children}
    </article>
  );
}

function BoxState({ box, formatDate }: { box: PrintBoxUpdate; formatDate: (value: string) => string }) {
  const { t } = useI18n();
  const agent = box.agent;
  if (agent.updateTarget) return <>{t('updates.boxes.pending', { version: agent.updateTarget })}</>;
  if (agent.updateResult === 'superseded' && agent.compatibility !== 'current') {
    return <>{t('updates.boxes.superseded', { version: agent.updateResultVersion ?? '' })}</>;
  }
  if (agent.updateResult === 'failed') {
    return <span className="print-agent-badge print-agent-incompatible">
      {t('updates.boxes.failed', { version: agent.updateResultVersion ?? '' })}
    </span>;
  }
  if (!agent.selfUpdate && agent.compatibility !== 'current') return <>{t('updates.boxes.manual')}</>;
  if (agent.compatibility === 'current') {
    return <>{t('print.agent.current')}{agent.updateResult === 'ok' && agent.updateResultAt
      ? <> · {formatDate(agent.updateResultAt)}</> : null}</>;
  }
  return <>{t('print.agent.updateAvailable', { version: agent.latestVersion ?? '' })}</>;
}

function PrintBoxes({ boxes, sending, formatDate, onUpdate, onCancel, onUpdateAll }: {
  boxes: PrintBoxUpdate[];
  sending: boolean;
  formatDate: (value: string) => string;
  onUpdate: (id: string) => void;
  onCancel: (id: string) => void;
  onUpdateAll: () => void;
}) {
  const { t } = useI18n();
  const updatable = boxes.filter((box) => box.canUpdate).length;
  return (
    <section className="updates-boxes" data-testid="updates-boxes">
      <header className="admin-header">
        <h3>{t('updates.boxes.heading')}</h3>
        {updatable > 0 && (
          <button type="button" className="row-action-primary" disabled={sending} onClick={onUpdateAll}
            data-testid="updates-boxes-all">
            {t('updates.boxes.updateAll', { count: updatable })}
          </button>
        )}
      </header>
      <p className="muted">{t('updates.boxes.help')}</p>
      {boxes.length === 0 ? (
        <p className="muted">{t('updates.boxes.none')}</p>
      ) : (
        <table className="jobs-table">
          <thead>
            <tr>
              <th>{t('updates.boxes.name')}</th>
              <th>{t('updates.boxes.owner')}</th>
              <th>{t('print.agent.version')}</th>
              <th>{t('updates.boxes.state')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {boxes.map((box) => (
              <tr key={box.stationId} data-testid="updates-box">
                <td>{box.name}</td>
                <td>{box.ownerName}</td>
                <td>{box.agent.version ?? '—'}</td>
                <td><BoxState box={box} formatDate={formatDate} /></td>
                <td>
                  {box.canUpdate && (
                    <button type="button" className="row-action" disabled={sending} onClick={() => onUpdate(box.stationId)}>
                      {t('updates.boxes.update')}
                    </button>
                  )}
                  {box.agent.updateTarget && (
                    <button type="button" className="row-action" disabled={sending} onClick={() => onCancel(box.stationId)}>
                      {t('common.cancel')}
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
