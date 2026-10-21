import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { formatSize } from '../components/format';
import {
  errorCode, getMailRun, getMailStatus, listMailSources, resumeMailImport, runIdOf, startMailImport,
} from './mailApi';
import { MailRunPanel } from './MailRunPanel';
import { filtersToParams, EMPTY_FILTERS } from './mailQuery';
import { isRunActive, type MailArchive, type MailRun, type MailSourceCandidate, type MailStatus } from './types';

type Load<T> = { kind: 'loading' } | { kind: 'ready'; value: T } | { kind: 'error' };

const UNAVAILABLE_KEYS: Record<string, MessageKey> = {
  disabled: 'mail.import.unavailable.disabled',
  importer_missing: 'mail.import.unavailable.importer_missing',
};

interface MailImportPanelProps {
  archives: Load<MailArchive[]>;
  reloadArchives: () => void;
}

// Import management: every archive with its counts and latest run, and the
// "Importa PST" flow over the owner's own PST files already in NubArca.
export function MailImportPanel({ archives, reloadArchives }: MailImportPanelProps) {
  const { t, formatDate, formatNumber } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Load<MailStatus>>({ kind: 'loading' });
  const [sources, setSources] = useState<Load<MailSourceCandidate[]>>({ kind: 'loading' });
  // Runs this page started or attached to, newer than what the archive list
  // last said. Keyed by archive.
  const [runs, setRuns] = useState<Record<string, MailRun>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<{ tone: 'info' | 'error'; text: string } | null>(null);

  const handleAuth = useCallback((err: unknown) => {
    if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return true; }
    return false;
  }, [invalidateAuth]);

  const loadSources = useCallback(async (signal?: AbortSignal) => {
    try {
      const items = await listMailSources(signal);
      setSources({ kind: 'ready', value: items });
    } catch (err) {
      if ((err as Error)?.name === 'AbortError' || handleAuth(err)) return;
      setSources({ kind: 'error' });
    }
  }, [handleAuth]);

  useEffect(() => {
    const controller = new AbortController();
    getMailStatus(controller.signal)
      .then((s) => {
        setStatus({ kind: 'ready', value: s });
        if (s.available) void loadSources(controller.signal);
      })
      .catch((err: unknown) => {
        if ((err as Error)?.name === 'AbortError' || handleAuth(err)) return;
        setStatus({ kind: 'error' });
      });
    return () => controller.abort();
  }, [handleAuth, loadSources]);

  const attach = useCallback((run: MailRun) => {
    setRuns((prev) => ({ ...prev, [run.archiveId]: run }));
  }, []);

  // A 409 import_running: follow the run that is already going instead of
  // reporting an error — the owner's intent ("import this") is being honoured.
  const attachToRunning = useCallback(async (body: unknown) => {
    const runId = runIdOf(body);
    if (runId) {
      try { attach(await getMailRun(runId)); } catch (err) { handleAuth(err); }
    }
    setMessage({ tone: 'info', text: t('mail.import.alreadyRunning') });
    reloadArchives();
  }, [attach, handleAuth, reloadArchives, t]);

  async function start(source: MailSourceCandidate) {
    setBusy(source.fileItemId);
    setMessage(null);
    try {
      const res = await startMailImport(source.fileItemId);
      attach(res.run);
      setMessage({ tone: 'info', text: t('mail.import.started', { name: res.archive.displayName }) });
      reloadArchives();
      void loadSources();
    } catch (err) {
      if (handleAuth(err)) return;
      const code = err instanceof ApiError ? errorCode(err.body) : null;
      if (err instanceof ApiError && err.status === 409 && code === 'import_running') {
        await attachToRunning(err.body);
      } else if (err instanceof ApiError && err.status === 400) {
        setMessage({ tone: 'error', text: t('mail.import.notPst') });
      } else if (err instanceof ApiError && err.status === 404) {
        setMessage({ tone: 'error', text: t('mail.import.sourceGone') });
        void loadSources();
      } else if (err instanceof ApiError && err.status === 503) {
        setMessage({ tone: 'error', text: t('mail.import.unavailable.generic') });
      } else {
        setMessage({ tone: 'error', text: t('mail.import.startError') });
      }
    } finally {
      setBusy(null);
    }
  }

  async function resume(archive: MailArchive) {
    setBusy(archive.id);
    setMessage(null);
    try {
      attach(await resumeMailImport(archive.id));
    } catch (err) {
      if (handleAuth(err)) return;
      const code = err instanceof ApiError ? errorCode(err.body) : null;
      if (err instanceof ApiError && err.status === 409 && code === 'source_unavailable') {
        setMessage({ tone: 'error', text: t('mail.import.sourceUnavailable') });
        reloadArchives();
      } else if (err instanceof ApiError && err.status === 409) {
        await attachToRunning(err.body);
      } else if (err instanceof ApiError && err.status === 503) {
        setMessage({ tone: 'error', text: t('mail.import.unavailable.generic') });
      } else {
        setMessage({ tone: 'error', text: t('mail.import.startError') });
      }
    } finally {
      setBusy(null);
    }
  }

  const available = status.kind === 'ready' && status.value.available;

  return (
    <div className="mail-imports" data-testid="mail-imports">
      {message && (
        <p className={message.tone === 'error' ? 'inline-error' : 'mail-notice'} role={message.tone === 'error' ? 'alert' : 'status'}>
          {message.text}
        </p>
      )}

      <section className="mail-card" aria-labelledby="mail-archives-title">
        <h2 id="mail-archives-title" className="mail-card-title">{t('mail.archives.title')}</h2>
        {archives.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {archives.kind === 'error' && <p className="inline-error" role="alert">{t('mail.archives.error')}</p>}
        {archives.kind === 'ready' && archives.value.length === 0 && (
          <p className="muted" data-testid="mail-archives-empty">{t('mail.archives.empty')}</p>
        )}
        {archives.kind === 'ready' && archives.value.length > 0 && (
          <ul className="mail-archive-list">
            {archives.value.map((a) => {
              const run = pickRun(runs[a.id], a.latestRun);
              const active = isRunActive(run);
              return (
                <li key={a.id} className="mail-archive" data-testid="mail-archive">
                  <div className="mail-archive-head">
                    <h3 className="mail-archive-name">{a.displayName}</h3>
                    <Link className="files-action" to={`/mail?${filtersToParams('mail', { ...EMPTY_FILTERS, archiveId: a.id })}`}>
                      {t('mail.archives.open')}
                    </Link>
                  </div>
                  <p className="muted mail-archive-meta">
                    {[
                      t('mail.archives.messages', { count: formatNumber(a.messageCount) }),
                      t('mail.archives.folders', { count: formatNumber(a.folderCount) }),
                      a.sourceFileName ? t('mail.archives.source', { name: a.sourceFileName }) : null,
                      a.lastImportedAt
                        ? t('mail.archives.lastImported', { date: formatDate(a.lastImportedAt, { dateStyle: 'medium', timeStyle: 'short' }) })
                        : null,
                    ].filter(Boolean).join(' · ')}
                  </p>
                  {!a.sourceAvailable && <p className="muted">{t('mail.archives.sourceMissing')}</p>}
                  {run && (
                    <MailRunPanel
                      key={run.id}
                      run={run}
                      onTerminal={(done) => { attach(done); reloadArchives(); }}
                    />
                  )}
                  {!active && (
                    <div className="details-actions">
                      <button
                        type="button"
                        className="files-action"
                        disabled={!available || !a.sourceAvailable || busy !== null}
                        onClick={() => void resume(a)}
                        data-testid="mail-archive-resume"
                      >
                        {run && run.status !== 'succeeded' ? t('mail.archives.resume') : t('mail.archives.reimport')}
                      </button>
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <section className="mail-card" aria-labelledby="mail-import-title">
        <h2 id="mail-import-title" className="mail-card-title">{t('mail.import.title')}</h2>
        <p className="muted">{t('mail.import.intro')}</p>
        {status.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {status.kind === 'error' && <p className="inline-error" role="alert">{t('mail.import.statusError')}</p>}
        {status.kind === 'ready' && !status.value.available && (
          <p className="inline-error" role="alert" data-testid="mail-import-unavailable">
            {t(UNAVAILABLE_KEYS[status.value.reason] ?? 'mail.import.unavailable.generic')}
          </p>
        )}
        {available && sources.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
        {available && sources.kind === 'error' && <p className="inline-error" role="alert">{t('mail.import.sourcesError')}</p>}
        {available && sources.kind === 'ready' && sources.value.length === 0 && (
          <p className="muted" data-testid="mail-sources-empty">
            {t('mail.import.noSources')} <Link to="/">{t('mail.import.goToFiles')}</Link>
          </p>
        )}
        {available && sources.kind === 'ready' && sources.value.length > 0 && (
          <ul className="mail-source-list" aria-label={t('mail.import.sourcesLabel')}>
            {sources.value.map((s) => (
              <li key={s.fileItemId} className="mail-source" data-testid="mail-source">
                <div className="mail-source-main">
                  <span className="mail-source-name">{s.name}</span>
                  <span className="muted">
                    {formatSize(s.sizeBytes)} · {formatDate(s.createdAt, { dateStyle: 'medium' })}
                    {s.alreadyImportedArchiveId && <> · {t('mail.import.alreadyImported')}</>}
                  </span>
                </div>
                <button
                  type="button"
                  className="row-action-primary"
                  disabled={busy !== null}
                  onClick={() => void start(s)}
                  aria-label={t('mail.import.startAria', { name: s.name })}
                >
                  {busy === s.fileItemId ? t('mail.import.starting') : t('mail.import.start')}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

// The run to show for an archive: what this page is following, unless the
// archive list has since reported a newer one.
function pickRun(local: MailRun | undefined, latest: MailRun | null): MailRun | null {
  if (!local) return latest;
  if (!latest || latest.id === local.id) return local;
  return Date.parse(latest.createdAt) > Date.parse(local.createdAt) ? latest : local;
}
