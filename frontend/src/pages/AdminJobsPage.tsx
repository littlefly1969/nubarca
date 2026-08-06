import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  cancelAdminJob,
  getAdminJob,
  listAdminJobs,
  isTerminal,
  getAdminJobCatalog,
  getAdminJobPending,
  enqueueAdminJob,
  type AdminJobPage,
  type AdminJobSummary,
  type AdminJobCommandSpec,
  type AdminJobParamSpec,
  type AdminJobParamValues,
  type AdminJobPendingCounts,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

// Slice 90 — admin background-jobs dashboard. Visibility + cooperative
// cancellation only (NOT a force-kill): a running job stops at its next safe
// checkpoint via the engine's cancellation flag/heartbeat. Renders only the
// safe summary fields the API returns — never payload, lock owner, paths,
// hashes, blob ids, raw metadata, or tokens.

const POLL_MS = 5000;
const STATUS_FILTERS = ['', 'queued', 'running', 'succeeded', 'failed', 'cancelled'] as const;

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; data: AdminJobPage }
  | { kind: 'forbidden' }
  | { kind: 'error'; message: string };

function ageFrom(iso: string | null): string {
  if (!iso) return '—';
  const secs = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (secs < 60) return `${secs}s`;
  if (secs < 3600) return `${Math.floor(secs / 60)}m`;
  if (secs < 86400) return `${Math.floor(secs / 3600)}h`;
  return `${Math.floor(secs / 86400)}d`;
}

function ProgressBar({ job }: { job: AdminJobSummary }) {
  const { t } = useI18n();
  const { progressCurrent, progressTotal, progressMessage } = job;
  const pct =
    progressTotal && progressTotal > 0 && progressCurrent != null
      ? Math.min(100, Math.round((progressCurrent / progressTotal) * 100))
      : null;
  if (progressCurrent == null && progressTotal == null && !progressMessage) {
    return <span className="muted">—</span>;
  }
  return (
    <div className="job-progress" aria-label={t('adminJobs.progressAria')}>
      {pct != null && (
        <div className="job-progress-bar" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
          <span className="job-progress-fill" style={{ width: `${pct}%` }} />
        </div>
      )}
      <span className="job-progress-text muted">
        {progressCurrent != null && progressTotal != null ? `${progressCurrent}/${progressTotal}` : ''}
        {progressMessage ? ` ${progressMessage}` : ''}
      </span>
    </div>
  );
}

export function AdminJobsPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [statusFilter, setStatusFilter] = useState('');
  const [selected, setSelected] = useState<AdminJobSummary | null>(null);
  const [cancelling, setCancelling] = useState(false);
  const filterRef = useRef(statusFilter);
  filterRef.current = statusFilter;

  const load = useCallback(
    async (quiet: boolean, signal?: AbortSignal) => {
      if (!quiet) setStatus({ kind: 'loading' });
      try {
        const data = await listAdminJobs(
          { status: filterRef.current || undefined, pageSize: 50 }, signal);
        setStatus({ kind: 'ready', data });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (err instanceof ApiError && err.status === 403) { setStatus({ kind: 'forbidden' }); return; }
        if (!quiet) setStatus({ kind: 'error', message: t('adminJobs.loadError') });
      }
    },
    [invalidateAuth, t],
  );

  // Initial load + reload when the filter changes.
  useEffect(() => {
    const controller = new AbortController();
    void load(false, controller.signal);
    return () => controller.abort();
  }, [load, statusFilter]);

  // Quiet polling so running/queued jobs update without flicker.
  useEffect(() => {
    const timer = setInterval(() => { void load(true); }, POLL_MS);
    return () => clearInterval(timer);
  }, [load]);

  // Refresh the open detail drawer (it may be mid-run).
  const refreshSelected = useCallback(async (id: string) => {
    try {
      const job = await getAdminJob(id);
      setSelected(job);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
      // 404/other: leave the drawer as-is.
    }
  }, [invalidateAuth]);

  useEffect(() => {
    if (!selected || isTerminal(selected.status)) return;
    const timer = setInterval(() => { void refreshSelected(selected.id); }, POLL_MS);
    return () => clearInterval(timer);
  }, [selected, refreshSelected]);

  async function onCancel(job: AdminJobSummary) {
    if (!window.confirm(t('adminJobs.confirmCancel'))) return;
    setCancelling(true);
    try {
      const updated = await cancelAdminJob(job.id);
      setSelected(updated);
      void load(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // 404/409: refresh so the UI reflects the real state.
      void refreshSelected(job.id);
    } finally {
      setCancelling(false);
    }
  }

  if (state.status !== 'authed') return null;

  return (
    <section className="admin-jobs-page">
      <header className="gallery-header">
        <h2>{t('adminJobs.heading')}</h2>
        <p className="muted">{t('adminJobs.intro')}</p>
      </header>

      {/* Zone 1 — commands: launch worker jobs with parameters. Separated from
          the queue-status zone below (the user asked to keep the commands and
          the information distinct). */}
      <CommandsConsole onEnqueued={() => void load(true)} />

      <h3 className="admin-console-queue-heading">{t('adminJobs.queueHeading')}</h3>

      {status.kind === 'loading' && <p className="muted" role="status">{t('adminJobs.loading')}</p>}
      {status.kind === 'forbidden' && (
        <p className="folder-error" role="alert">{t('adminJobs.forbidden')}</p>
      )}
      {status.kind === 'error' && (
        <div className="folder-error" role="alert">
          {status.message}
          <button type="button" className="retry-button" onClick={() => void load(false)}>{t('common.tryAgain')}</button>
        </div>
      )}

      {status.kind === 'ready' && (
        <>
          <ul className="jobs-counters" aria-label={t('adminJobs.countersAria')}>
            <li><span className="jobs-counter-n">{status.data.counts.queued}</span> {t('adminJobs.queued')}</li>
            <li><span className="jobs-counter-n">{status.data.counts.running}</span> {t('adminJobs.running')}</li>
            <li><span className="jobs-counter-n">{status.data.counts.succeeded}</span> {t('adminJobs.succeeded')}</li>
            <li><span className="jobs-counter-n">{status.data.counts.failed}</span> {t('adminJobs.failed')}</li>
            <li><span className="jobs-counter-n">{status.data.counts.cancelled}</span> {t('adminJobs.cancelled')}</li>
          </ul>

          <div className="gallery-sort">
            <label htmlFor="job-status-filter" className="gallery-sort-label">{t('adminJobs.thStatus')}</label>
            <select
              id="job-status-filter"
              className="gallery-select"
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
            >
              {STATUS_FILTERS.map((s) => (
                <option key={s || 'all'} value={s}>{s === '' ? t('adminJobs.all') : s}</option>
              ))}
            </select>
          </div>

          {status.data.items.length === 0 ? (
            <p className="muted">{t('adminJobs.noJobs')}</p>
          ) : (
            <table className="jobs-table" aria-label={t('adminJobs.tableAria')}>
              <thead>
                <tr>
                  <th>{t('adminJobs.thType')}</th><th>{t('adminJobs.thStatus')}</th><th>{t('adminJobs.thAttempts')}</th><th>{t('adminJobs.thProgress')}</th>
                  <th>{t('adminJobs.thCreated')}</th><th>{t('adminJobs.thError')}</th>
                </tr>
              </thead>
              <tbody>
                {status.data.items.map((job) => (
                  <tr key={job.id}>
                    <td>
                      <button type="button" className="job-row-open" onClick={() => setSelected(job)}>
                        {job.type}
                      </button>
                    </td>
                    <td>
                      <span className={`job-status-badge job-status-${job.status}`}>{job.status}</span>
                      {job.cancellationRequested && !isTerminal(job.status) && (
                        <span className="job-cancel-flag" title={t('adminJobs.cancellationRequestedTitle')}>{t('adminJobs.cancellingFlag')}</span>
                      )}
                    </td>
                    <td>{job.attempts}/{job.maxAttempts}</td>
                    <td><ProgressBar job={job} /></td>
                    <td className="muted">{ageFrom(job.createdAt)}</td>
                    <td className="muted">{job.lastErrorCode ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}

      {selected && (
        <JobDetailDrawer
          job={selected}
          cancelling={cancelling}
          onClose={() => setSelected(null)}
          onCancel={() => void onCancel(selected)}
        />
      )}
    </section>
  );
}

function JobDetailDrawer({
  job, cancelling, onClose, onCancel,
}: {
  job: AdminJobSummary;
  cancelling: boolean;
  onClose: () => void;
  onCancel: () => void;
}) {
  const { t } = useI18n();
  const canCancel = !isTerminal(job.status) && !job.cancellationRequested;
  return (
    <aside className="job-detail-drawer" role="dialog" aria-modal="true" aria-label={t('adminJobs.jobDialogLabel', { type: job.type })}>
      <div className="job-detail-head">
        <h3>{job.type}</h3>
        <button type="button" aria-label={t('common.close')} onClick={onClose}>✕</button>
      </div>

      <ProgressBar job={job} />

      <dl className="job-detail-meta">
        <dt>{t('adminJobs.dStatus')}</dt><dd><span className={`job-status-badge job-status-${job.status}`}>{job.status}</span></dd>
        <dt>{t('adminJobs.dAttempts')}</dt><dd>{job.attempts}/{job.maxAttempts}</dd>
        <dt>{t('adminJobs.dPriority')}</dt><dd>{job.priorityClass} ({job.priority})</dd>
        {job.sliceNumber > 0 && (<><dt>{t('adminJobs.dSlice')}</dt><dd>#{job.sliceNumber}</dd></>)}
        {job.yieldReason && (<><dt>{t('adminJobs.dLastYield')}</dt><dd>{job.yieldReason}</dd></>)}
        <dt>{t('adminJobs.dCreated')}</dt><dd>{t('adminJobs.ago', { age: ageFrom(job.createdAt) })}</dd>
        <dt>{t('adminJobs.dStarted')}</dt><dd>{job.startedAt ? t('adminJobs.ago', { age: ageFrom(job.startedAt) }) : '—'}</dd>
        <dt>{t('adminJobs.dCompleted')}</dt><dd>{job.completedAt ? t('adminJobs.ago', { age: ageFrom(job.completedAt) }) : '—'}</dd>
        <dt>{t('adminJobs.dCancellation')}</dt><dd>{job.cancellationRequested ? t('adminJobs.requested') : t('adminJobs.no')}</dd>
        {job.lastErrorCode && (<><dt>{t('adminJobs.dError')}</dt><dd>{job.lastErrorCode}</dd></>)}
        {job.lastErrorMessage && (<><dt>{t('adminJobs.dDetail')}</dt><dd className="job-error-detail">{job.lastErrorMessage}</dd></>)}
      </dl>

      {canCancel ? (
        <button
          type="button"
          className="row-action row-action-destructive"
          onClick={onCancel}
          disabled={cancelling}
        >
          {cancelling ? t('adminJobs.requesting') : t('adminJobs.requestCancellation')}
        </button>
      ) : (
        <p className="muted">
          {isTerminal(job.status)
            ? t('adminJobs.finishedCannotCancel')
            : t('adminJobs.alreadyRequested')}
        </p>
      )}
    </aside>
  );
}

// ── Commands console (zone 1) ───────────────────────────────────────────────
// Entirely server-driven: the catalog endpoint returns the launchable commands
// with their parameter specs, and every card + form is rendered from that. New
// commands appear here automatically — no UI change. Command titles/descriptions
// and parameter labels are looked up by key (cast to MessageKey; the i18n layer
// falls back to the key itself if a translation is ever missing).

const CATEGORY_ORDER = ['metadata', 'media', 'storage', 'ai'] as const;

function CommandsConsole({ onEnqueued }: { onEnqueued: () => void }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [commands, setCommands] = useState<AdminJobCommandSpec[] | null>(null);
  const [pending, setPending] = useState<AdminJobPendingCounts>({});
  const [loadError, setLoadError] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    getAdminJobCatalog(controller.signal)
      .then((c) => setCommands(c.commands))
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setLoadError(true);
      });
    return () => controller.abort();
  }, [invalidateAuth]);

  // Pending counts load separately (wider queries, server-side cached) so the
  // cards render immediately and the "N waiting" badges fill in after.
  const loadPending = useCallback((signal?: AbortSignal) => {
    getAdminJobPending(signal)
      .then(setPending)
      .catch(() => { /* counts are informational — never block the console */ });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    loadPending(controller.signal);
    return () => controller.abort();
  }, [loadPending]);

  if (loadError) {
    return <div className="folder-error" role="alert">{t('adminJobs.consoleLoadError')}</div>;
  }
  if (!commands) return null;

  const groups = CATEGORY_ORDER
    .map((cat) => ({ cat, items: commands.filter((c) => c.category === cat) }))
    .filter((g) => g.items.length > 0);

  return (
    <section className="admin-console" aria-label={t('adminJobs.consoleHeading')}>
      <div className="admin-console-head">
        <h3>{t('adminJobs.consoleHeading')}</h3>
        <p className="muted">{t('adminJobs.consoleIntro')}</p>
        <ul className="admin-console-legend muted">
          <li><span className="admin-console-badge admin-console-badge-pending">N</span> {t('adminJobs.legendPending')}</li>
          <li><span className="admin-console-badge admin-console-badge-clear">✓</span> {t('adminJobs.legendClear')}</li>
          <li><span className="admin-console-badge admin-console-badge-off">—</span> {t('adminJobs.legendDisabled')}</li>
          <li><strong>{t('adminJobs.simulate')}</strong> · {t('adminJobs.legendSimulate')}</li>
        </ul>
      </div>
      {groups.map(({ cat, items }) => (
        <div key={cat} className="admin-console-category">
          <h4>{t(`adminJobs.cat.${cat}` as MessageKey)}</h4>
          <div className="admin-console-grid">
            {items.map((cmd) => (
              <CommandCard
                key={cmd.key}
                command={cmd}
                pending={pending[cmd.key]}
                onEnqueued={() => { onEnqueued(); loadPending(); }}
              />
            ))}
          </div>
        </div>
      ))}
    </section>
  );
}

type ParamValue = boolean | number | string;

function initialValues(command: AdminJobCommandSpec): AdminJobParamValues {
  const v: AdminJobParamValues = {};
  for (const p of command.params) {
    if (p.kind === 'bool') v[p.name] = p.defaultBool;
    else if (p.kind === 'int') { if (p.defaultInt != null) v[p.name] = p.defaultInt; }
    // choice: preselect the configured production model (state of the art)
    else if (p.kind === 'choice') v[p.name] = p.defaultText ?? '';
    else v[p.name] = ''; // text | guid start empty (controlled input)
  }
  return v;
}

// Only send parameters the user actually set: booleans always, ints when
// numeric, text/guid when non-empty. The backend applies its own defaults for
// anything omitted.
function buildSubmit(command: AdminJobCommandSpec, values: AdminJobParamValues): AdminJobParamValues {
  const out: AdminJobParamValues = {};
  for (const p of command.params) {
    const raw = values[p.name];
    if (p.kind === 'bool') out[p.name] = raw === true;
    else if (p.kind === 'int') { if (typeof raw === 'number' && !Number.isNaN(raw)) out[p.name] = raw; }
    else if (typeof raw === 'string' && raw.trim() !== '') out[p.name] = raw.trim();
  }
  return out;
}

function CommandCard({
  command, pending, onEnqueued,
}: {
  command: AdminJobCommandSpec;
  pending?: number;
  onEnqueued: () => void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [values, setValues] = useState<AdminJobParamValues>(() => initialValues(command));
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [feedback, setFeedback] = useState<{ ok: boolean; text: string } | null>(null);

  const hasParams = command.params.length > 0;
  const missingRequired = command.params.some(
    (p) => p.required && (values[p.name] === undefined || values[p.name] === ''));
  // A dry run only counts what WOULD be processed — make that unmistakable on
  // the button itself instead of a silently-preselected checkbox.
  const isDryRun = command.params.some((p) => p.name === 'dryRun') && values.dryRun === true;
  // Only an explicit `false` disables a command: a response without the field
  // (older server) must stay usable rather than locking the whole console.
  const available = command.available !== false;

  async function run() {
    // Explicit confirm when a destructive/heavy flag is actually enabled.
    const dangerActive = command.params.some((p) => p.danger && values[p.name] === true);
    if (dangerActive && !window.confirm(t('adminJobs.confirmDanger'))) return;
    setBusy(true);
    setFeedback(null);
    try {
      const res = await enqueueAdminJob(command.key, buildSubmit(command, values));
      setFeedback({
        ok: true,
        text: res.alreadyQueued
          ? t('adminJobs.alreadyQueued')
          : t('adminJobs.enqueued', { type: res.jobType }),
      });
      onEnqueued();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setFeedback({ ok: false, text: t('adminJobs.enqueueError') });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={`admin-console-card${available ? '' : ' is-unavailable'}`}>
      <div className="admin-console-card-head">
        <h5>
          {t(`adminJobs.cmd.${command.key}.title` as MessageKey)}
          {!available && (
            <span className="admin-console-badge admin-console-badge-off">
              {t('adminJobs.disabledBadge')}
            </span>
          )}
          {available && pending != null && pending > 0 && (
            <span className="admin-console-badge admin-console-badge-pending">
              {t('adminJobs.pendingBadge', { count: pending })}
            </span>
          )}
          {available && pending === 0 && (
            <span className="admin-console-badge admin-console-badge-clear">
              {t('adminJobs.pendingNone')}
            </span>
          )}
        </h5>
        <p className="muted">{t(`adminJobs.cmd.${command.key}.desc` as MessageKey)}</p>
        {!available && command.disabledReason && (
          <p className="muted admin-console-disabled-reason">
            {t(`adminJobs.disabled.${command.disabledReason}` as MessageKey)}
          </p>
        )}
      </div>

      {hasParams && (
        <button
          type="button"
          className="admin-console-params-toggle"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
        >
          {open ? '▾' : '▸'} {t('adminJobs.paramsToggle')}
        </button>
      )}
      {open && hasParams && (
        <div className="admin-console-params">
          {command.params.map((p) => (
            <ParamField
              key={p.name}
              spec={p}
              value={values[p.name]}
              onChange={(v) => setValues((prev) => ({ ...prev, [p.name]: v }))}
            />
          ))}
        </div>
      )}

      <div className="admin-console-card-actions">
        <button
          type="button"
          className="row-action-primary"
          disabled={busy || missingRequired || !available}
          onClick={() => void run()}
        >
          {busy
            ? t('adminJobs.runningCmd')
            : isDryRun ? t('adminJobs.simulate') : t('adminJobs.run')}
        </button>
        {isDryRun && !busy && (
          <span className="muted admin-console-dryrun-hint">{t('adminJobs.dryRunHint')}</span>
        )}
        {feedback && (
          <span className={feedback.ok ? 'admin-console-ok' : 'folder-error'} role="status">
            {feedback.text}
          </span>
        )}
      </div>
    </div>
  );
}

function ParamField({
  spec, value, onChange,
}: {
  spec: AdminJobParamSpec;
  value: ParamValue | undefined;
  onChange: (v: ParamValue) => void;
}) {
  const { t } = useI18n();
  const label = t(`adminJobs.param.${spec.name}` as MessageKey);

  if (spec.kind === 'bool') {
    return (
      <label className="admin-console-param admin-console-param-bool">
        <input
          type="checkbox"
          checked={value === true}
          onChange={(e) => onChange(e.target.checked)}
        />
        <span>{label}{spec.danger ? ' ⚠' : ''}</span>
      </label>
    );
  }

  if (spec.kind === 'int') {
    return (
      <label className="admin-console-param">
        <span>{label}</span>
        <input
          type="number"
          min={spec.min ?? undefined}
          max={spec.max ?? undefined}
          value={typeof value === 'number' ? value : ''}
          onChange={(e) => onChange(e.target.value === '' ? '' : Number(e.target.value))}
        />
      </label>
    );
  }

  // A closed option set (e.g. the AI model/profile): a select preselected on
  // the configured production model, never a free-text key to guess.
  if (spec.kind === 'choice') {
    const options = spec.options ?? [];
    return (
      <label className="admin-console-param">
        <span>{label}</span>
        <select
          className="gallery-select"
          value={typeof value === 'string' ? value : ''}
          onChange={(e) => onChange(e.target.value)}
        >
          {options.length === 0 && <option value="">{t('adminJobs.choiceNone')}</option>}
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.recommended ? t('adminJobs.choiceRecommended', { label: o.label }) : o.label}
            </option>
          ))}
        </select>
      </label>
    );
  }

  // text | guid
  return (
    <label className="admin-console-param">
      <span>{label}{spec.required ? ' *' : ''}</span>
      <input
        type="text"
        value={typeof value === 'string' ? value : ''}
        onChange={(e) => onChange(e.target.value)}
        placeholder={spec.kind === 'guid' ? '00000000-0000-0000-0000-000000000000' : undefined}
      />
    </label>
  );
}
