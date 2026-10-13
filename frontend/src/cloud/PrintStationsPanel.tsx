import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import {
  ApiError,
  cancelPrintJob,
  createPrintStation,
  createPrintTestJob,
  erasePrintStation,
  getPrintAgentBundle,
  listPrintStations,
  listSharedPrinters,
  previewPrintStationErase,
  renewPrintStationEnrollment,
  revokePrintStation,
  setPrintStationDesiredState,
  type PrintAgentBundle,
  type PrintAgentStatus,
  type PrintJobSummary,
  type PrintStation,
  type PrintStationEnrollment,
  type SharedPrinter,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { PrintAgentBundleCard } from './PrintAgentBundleCard';
import { PrintAgentSetupGuide } from './PrintAgentSetupGuide';
import { PrinterCalibrationControls } from './PrinterCalibrationControls';
import { isPrinterOffline, PrinterMediaRemaining } from './PrinterMediaRemaining';
import { paperLabel, PrinterPaperControl } from './PrinterPaperControl';
import { PrinterSharingControls } from './PrinterSharingControls';
import { PrinterSimulationControls } from './PrinterSimulationControls';
import { PrinterUsageSummary } from './PrinterUsageSummary';
import { SharedPrintersList } from './SharedPrintersList';

const STATUS_KEYS: Record<PrintStation['status'], MessageKey> = {
  online: 'print.statusOnline',
  degraded: 'print.statusDegraded',
  offline: 'print.statusOffline',
  revoked: 'print.statusRevoked',
};

const KIND_KEYS: Record<string, MessageKey> = {
  diagnostic: 'print.kind.diagnostic',
  'owner-photo': 'print.kind.ownerPhoto',
  'owner-grid4': 'print.kind.ownerGrid4',
  'owner-strip4': 'print.kind.ownerStrip4',
  'party-photo': 'print.kind.partyPhoto',
  'party-grid4': 'print.kind.partyGrid4',
  'party-strip4': 'print.kind.partyStrip4',
  'party-qr-card': 'print.kind.partyQrCard',
};

const STATE_KEYS: Record<string, MessageKey> = {
  requested: 'print.job.preparing',
  rendering: 'print.job.preparing',
  ready: 'print.job.ready',
  claimed: 'print.job.printing',
  submitting: 'print.job.printing',
  submitted: 'print.job.submitted',
};

/** Only what has not reached the printer can be taken back. */
const CANCELLABLE = new Set(['requested', 'rendering', 'ready']);

/**
 * How often the open panel re-reads the stations: a print's progress, the
 * printer's count of prints left, a printer coming back. One request at a time
 * for the whole read model, never one per printer.
 */
export const PRINT_STATIONS_REFRESH_MS = 8_000;

type View =
  | { kind: 'list' }
  | { kind: 'station'; id: string }
  | { kind: 'add' }
  /** Connect a box (with a code) or update one already connected (without). */
  | { kind: 'guide'; id: string; enrollment: PrintStationEnrollment | null; awaiting: boolean };

const AGENT_KEYS: Record<PrintAgentStatus['compatibility'], MessageKey> = {
  current: 'print.agent.current',
  'update-available': 'print.agent.updateAvailable',
  incompatible: 'print.agent.incompatible',
  unknown: 'print.agent.unknown',
};

/**
 * The printers page, phone first: a list of compact cards — name, state,
 * paper and prints left — each opening its printer; adding one is a short,
 * guided path that ends with the box connected.
 */
export function PrintStationsPanel() {
  const { state, invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [stations, setStations] = useState<PrintStation[] | null>(null);
  const [shared, setShared] = useState<SharedPrinter[]>([]);
  const [bundle, setBundle] = useState<PrintAgentBundle | null>(null);
  const [view, setView] = useState<View>({ kind: 'list' });
  const [name, setName] = useState('');
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [erased, setErased] = useState(false);

  // Only the newest read is applied, so a slow refresh can never put back
  // what a later one (or an action's own reload) already replaced.
  const latestRead = useRef(0);
  const load = useCallback(async (signal?: AbortSignal, quiet = false) => {
    const read = ++latestRead.current;
    try {
      // Your stations, and the printers lent to you: the second is its own
      // list, so a person with no station of their own still finds theirs.
      // The bundle is a nicety: a page without it still works.
      const [own, lent, published] = await Promise.all([
        listPrintStations(signal), listSharedPrinters(signal),
        getPrintAgentBundle(signal).catch(() => null),
      ]);
      if (read !== latestRead.current) return;
      setStations(own);
      setShared(lent);
      setBundle(published);
      setError(null);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // A background refresh that fails keeps the last good picture on screen.
      if (!quiet) setError(t('print.loadError'));
    }
  }, [invalidateAuth, t]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // While the panel is open and the page visible: re-read, one request at a
  // time — the next waits for the last to finish — and stop on leaving.
  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    const tick = async () => {
      if (document.visibilityState === 'visible') await load(controller.signal, true);
      if (!controller.signal.aborted) timer = window.setTimeout(() => void tick(), PRINT_STATIONS_REFRESH_MS);
    };
    timer = window.setTimeout(() => void tick(), PRINT_STATIONS_REFRESH_MS);
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [load]);

  async function create(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;
    setBusy('create'); setError(null);
    try {
      const enrollment = await createPrintStation(name.trim());
      setName('');
      setView({ kind: 'guide', id: enrollment.id, enrollment, awaiting: true });
      await load();
    } catch { setError(t('print.createError')); }
    finally { setBusy(null); }
  }

  async function action(id: string, operation: () => Promise<unknown>, message: MessageKey) {
    setBusy(id); setError(null);
    try { await operation(); await load(); }
    catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t(message));
    } finally { setBusy(null); }
  }

  /** Says what goes, asks once, then erases and returns to the list. */
  async function erase(station: PrintStation) {
    setBusy(station.id); setError(null);
    try {
      const preview = await previewPrintStationErase(station.id);
      if (!window.confirm(t('print.erase.confirm', { name: station.name, jobs: preview.jobs, parties: preview.parties }))) return;
      await erasePrintStation(station.id);
      setErased(true);
      setView({ kind: 'list' });
      await load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('print.erase.error'));
    } finally { setBusy(null); }
  }

  async function renew(station: PrintStation) {
    setBusy(station.id); setError(null);
    try {
      const enrollment = await renewPrintStationEnrollment(station.id);
      setView({ kind: 'guide', id: station.id, enrollment, awaiting: station.agentVersion === null });
    } catch { setError(t('print.enrollmentError')); }
    finally { setBusy(null); }
  }

  if (state.status !== 'authed') return null;

  const stationOf = (id: string) => stations?.find((x) => x.id === id) ?? null;
  const errorLine = error && <p className="folder-error" role="alert">{error}</p>;

  if (view.kind === 'add') {
    return (
      <section className="print-stations">
        <button type="button" className="print-back" onClick={() => setView({ kind: 'list' })}>{t('print.backToList')}</button>
        <form className="print-station-create" onSubmit={(event) => void create(event)}>
          <h3>{t('print.addPrinter')}</h3>
          <label>
            <span>{t('print.printerName')}</span>
            <input value={name} maxLength={120} autoFocus onChange={(event) => setName(event.target.value)} />
          </label>
          <div className="print-station-actions">
            <button type="submit" className="primary-button" disabled={!name.trim() || busy !== null}>
              {t('print.createAndConnect')}
            </button>
            <button type="button" onClick={() => setView({ kind: 'list' })}>{t('common.cancel')}</button>
          </div>
        </form>
        {errorLine}
      </section>
    );
  }

  if (view.kind === 'guide') {
    const station = stationOf(view.id);
    const connected = view.awaiting && station?.agentVersion ? (station.agent?.version ?? station.agentVersion) : null;
    return (
      <section className="print-stations">
        <button type="button" className="print-back" onClick={() => setView({ kind: 'list' })}>{t('print.backToList')}</button>
        {bundle?.canManage && <PrintAgentBundleCard bundle={bundle} onChanged={setBundle} />}
        <PrintAgentSetupGuide
          stationName={station?.name ?? view.enrollment?.name ?? ''}
          stationId={view.id}
          enrollment={view.enrollment}
          bundle={bundle}
          connectedVersion={connected}
          onNewCode={() => { if (station) void renew(station); }}
          onDone={() => setView(station ? { kind: 'station', id: view.id } : { kind: 'list' })}
          onOpenStation={() => setView({ kind: 'station', id: view.id })}
        />
        {errorLine}
      </section>
    );
  }

  if (view.kind === 'station') {
    const station = stationOf(view.id);
    if (!station) {
      return (
        <section className="print-stations">
          <button type="button" className="print-back" onClick={() => setView({ kind: 'list' })}>{t('print.backToList')}</button>
          {stations === null && <p className="muted" role="status">{t('print.loading')}</p>}
        </section>
      );
    }
    return (
      <section className="print-stations" aria-busy={busy !== null}>
        <button type="button" className="print-back" onClick={() => setView({ kind: 'list' })}>{t('print.backToList')}</button>
        {errorLine}
        <StationDetail
          station={station}
          busy={busy === station.id}
          formatDate={formatDate}
          onChanged={() => load()}
          onAction={(operation, message) => void action(station.id, operation, message)}
          onRenew={() => void renew(station)}
          onErase={() => void erase(station)}
          onUpdateGuide={() => setView({ kind: 'guide', id: station.id, enrollment: null, awaiting: false })}
        />
      </section>
    );
  }

  return (
    <section className="print-stations" aria-busy={stations === null || busy !== null}>
      <div className="print-stations-toolbar">
        <button type="button" className="primary-button" onClick={() => { setError(null); setView({ kind: 'add' }); }}>
          {t('print.addPrinter')}
        </button>
        <button type="button" className="refresh-button" onClick={() => void load()}>
          {t('common.refresh')}
        </button>
      </div>

      {bundle?.canManage && <PrintAgentBundleCard bundle={bundle} onChanged={setBundle} />}
      {errorLine}
      {erased && (
        <section className="print-erased" role="status" data-testid="print-erased">
          <p>{t('print.erase.done')}</p>
          <p className="muted">{t('print.erase.boxHint')}</p>
          <pre><code>{'sudo bash /opt/nubarca-print-agent/linux/uninstall-instance.sh --instance box --purge-state'}</code></pre>
          <button type="button" onClick={() => setErased(false)}>{t('common.close')}</button>
        </section>
      )}
      {stations === null && <p className="muted" role="status">{t('print.loading')}</p>}
      {stations?.length === 0 && shared.length === 0 && (
        <p className="muted" data-testid="print-empty">{t('print.empty')}</p>
      )}

      <ul className="print-station-list" aria-label={t('print.listLabel')}>
        {stations?.map((station) => {
          const printer = station.devices[0] ?? null;
          const agent = station.agent ?? null;
          return (
            <li key={station.id}>
              <button type="button" className={`print-station-row print-station-${station.status}`}
                data-testid="print-station-row" onClick={() => setView({ kind: 'station', id: station.id })}>
                <span className="print-station-row-head">
                  <strong>{station.name}</strong>
                  <span className="print-station-row-badges">
                    {printer?.simulated && <span className="print-sim-badge">{t('print.sim.badge')}</span>}
                    <span className={`print-status print-status-${station.status}`}>{t(STATUS_KEYS[station.status])}</span>
                  </span>
                </span>
                <span className="muted print-station-row-line">
                  {printer
                    ? [
                      printer.displayName,
                      paperLabel(printer.loadedPaperSize ?? '10x15'),
                      ...(typeof printer.mediaRemainingPrints === 'number'
                        ? [t('print.remainingShort', { count: printer.mediaRemainingPrints })] : []),
                    ].join(' · ')
                    : station.agentVersion ? t('print.noPrinter') : t('print.notEnrolled')}
                </span>
                {agent && agent.compatibility !== 'current' && agent.compatibility !== 'unknown' && (
                  <span className={`print-agent-badge print-agent-${agent.compatibility}`}>
                    {t(AGENT_KEYS[agent.compatibility], { version: agent.latestVersion ?? '' })}
                  </span>
                )}
              </button>
            </li>
          );
        })}
      </ul>

      <SharedPrintersList printers={shared} onChanged={() => load()} />
    </section>
  );
}

/** One printer, opened from the list: its sections, then what can be done with it. */
function StationDetail({ station, busy, formatDate, onChanged, onAction, onRenew, onUpdateGuide, onErase }: {
  station: PrintStation;
  busy: boolean;
  formatDate: (value: string) => string;
  onChanged: () => Promise<void>;
  onAction: (operation: () => Promise<unknown>, message: MessageKey) => void;
  onRenew: () => void;
  onUpdateGuide: () => void;
  onErase: () => void;
}) {
  const { t } = useI18n();
  const printer = station.devices[0] ?? null;
  const agent = station.agent ?? null;
  const live = station.revokedAt === null;
  // The test page goes on the paper in the printer, so it needs a printer
  // that can print that paper.
  const printPrinter = station.devices.find((device) =>
    (device.papers ?? (device.supportsPhoto10x15 ? ['10x15'] : []))
      .includes(device.loadedPaperSize ?? '10x15')
    && (device.observedState === 'ready' || device.observedState === 'busy')) ?? null;

  return (
    <article className={`print-station-card print-station-${station.status}`} data-testid="print-station-detail">
      <header>
        <h3>{station.name}</h3>
        <span className="print-station-row-badges">
          {printer?.simulated && <span className="print-sim-badge" data-testid="print-sim-badge">{t('print.sim.badge')}</span>}
          <span className={`print-status print-status-${station.status}`}>{t(STATUS_KEYS[station.status])}</span>
        </span>
      </header>

      <section className="print-station-section">
        <dl>
          <div><dt>{t('print.printer')}</dt><dd>{printer?.displayName ?? t('print.noPrinter')}</dd></div>
          {printer && (
            <PrinterMediaRemaining
              remaining={printer.mediaRemainingPrints}
              observedAt={printer.mediaRemainingObservedAt}
              offline={isPrinterOffline(station.status, printer.observedState)}
            />
          )}
          {printer && (
            <div>
              <dt>{t('print.stripCut')}</dt>
              <dd data-testid="print-strip-cut">
                {t(printer.cutsStrips ? 'print.stripCutPrinter' : 'print.stripCutHand')}
              </dd>
            </div>
          )}
          <div><dt>{t('print.queue')}</dt><dd>{station.queueCount}</dd></div>
          <div><dt>{t('print.currentJob')}</dt><dd>{station.currentJob ? `${station.currentJob.shortCode} · ${station.currentJob.state}` : '—'}</dd></div>
        </dl>
        {live && printer && <PrinterPaperControl stationId={station.id} device={printer} onSaved={onChanged} />}
      </section>

      {live && printer && <PrinterSimulationControls stationId={station.id} device={printer} onChanged={onChanged} />}

      {(station.queue?.length ?? 0) > 0 && (
        <PrintQueueList jobs={station.queue ?? []} busy={busy}
          onCancel={(job) => onAction(() => cancelPrintJob(job.id), 'print.job.cancelError')} />
      )}

      <section className="print-station-section" data-testid="print-agent">
        <h4>{t('print.agent.section')}</h4>
        <dl>
          <div><dt>{t('print.agent.version')}</dt><dd>{agent?.version ?? station.agentVersion ?? t('print.notEnrolled')}</dd></div>
          {agent?.build && <div><dt>{t('print.agent.build')}</dt><dd><code>{agent.build}</code></dd></div>}
          <div><dt>{t('print.lastSeen')}</dt><dd>{station.lastSeenAt ? formatDate(station.lastSeenAt) : '—'}</dd></div>
          <div><dt>{t('print.lastError')}</dt><dd>{station.lastError ?? '—'}</dd></div>
        </dl>
        {agent && (
          <p className={`print-agent-badge print-agent-${agent.compatibility}`} data-testid="print-agent-status">
            {t(AGENT_KEYS[agent.compatibility], { version: agent.latestVersion ?? '' })}
            {agent.unofficial && <>{' · '}{t('print.agent.unofficial')}</>}
          </p>
        )}
        {live && agent && (
          <button type="button" onClick={onUpdateGuide}>{t('print.agent.updateGuide')}</button>
        )}
      </section>

      {printer && <PrinterUsageSummary usage={printer.usage ?? []} />}
      {live && printer && <PrinterCalibrationControls stationId={station.id} device={printer} onSaved={onChanged} />}
      {live && printer && <PrinterSharingControls stationId={station.id} device={printer} onChanged={onChanged} />}

      {live && (
        <div className="print-station-actions">
          {station.desiredState === 'running' ? (
            <button type="button" disabled={busy}
              onClick={() => onAction(() => setPrintStationDesiredState(station.id, 'paused'), 'print.actionError')}>
              {t('print.pause')}
            </button>
          ) : (
            <button type="button" disabled={busy}
              onClick={() => onAction(() => setPrintStationDesiredState(station.id, 'running'), 'print.actionError')}>
              {t('print.resume')}
            </button>
          )}
          <button type="button" disabled={!printPrinter || station.desiredState !== 'running' || busy}
            onClick={() => printPrinter && onAction(
              () => createPrintTestJob(station.id, printPrinter.id), 'print.testError')}>
            {t('print.testPrint')}
          </button>
          <button type="button" disabled={busy} onClick={onRenew}>
            {t('print.newEnrollment')}
          </button>
          <button type="button" className="destructive-button" disabled={busy}
            onClick={() => window.confirm(t('print.revokeConfirm', { name: station.name }))
              && onAction(() => revokePrintStation(station.id), 'print.revokeError')}>
            {t('print.revoke')}
          </button>
        </div>
      )}

      {!live && (
        <section className="print-station-section" data-testid="print-erase">
          <h4>{t('print.erase.title')}</h4>
          <p className="muted">{t('print.erase.help')}</p>
          <div className="print-station-actions">
            <button type="button" className="destructive-button" disabled={busy} onClick={onErase}>
              {t('print.erase.title')}
            </button>
          </div>
        </section>
      )}
    </article>
  );
}

/**
 * What is waiting on a station, oldest first, whoever sent it: a sheet made
 * for another paper says which, and anything not yet at the printer can be
 * taken back — by the printer's owner too, when it came from a loan.
 */
function PrintQueueList({ jobs, busy, onCancel }: {
  jobs: PrintJobSummary[];
  busy: boolean;
  onCancel: (job: PrintJobSummary) => void;
}) {
  const { t } = useI18n();
  return (
    <section className="print-queue" data-testid="print-queue">
      <h4>{t('print.queueTitle')}</h4>
      <ol>
        {jobs.map((job) => (
          <li key={job.id} className="print-queue-row" data-testid="print-queue-row">
            <code>{job.shortCode}</code>
            <span>
              {KIND_KEYS[job.kind] ? t(KIND_KEYS[job.kind]) : job.kind}
              {' · '}{job.format === '2x6x2' ? '2×6' : paperLabel(job.format)}
              {job.ownerName && <span className="muted">{' · '}{t('print.job.from', { name: job.ownerName })}</span>}
              {job.simulated && <span className="print-sim-badge">{t('print.sim.badge')}</span>}
            </span>
            <span className={job.waitingForPaper ? 'print-queue-waiting' : 'muted'}>
              {job.waitingForPaper
                ? t('print.job.waitingPaper', { paper: paperLabel(job.waitingForPaper) })
                : STATE_KEYS[job.state] ? t(STATE_KEYS[job.state]) : job.state}
            </span>
            {CANCELLABLE.has(job.state) && (
              <button type="button" disabled={busy} onClick={() => onCancel(job)}>
                {t('print.job.cancel')}
              </button>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}
