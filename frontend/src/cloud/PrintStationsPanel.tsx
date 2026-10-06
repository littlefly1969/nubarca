import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import {
  ApiError,
  cancelPrintJob,
  createPrintStation,
  createPrintTestJob,
  listPrintStations,
  listSharedPrinters,
  renewPrintStationEnrollment,
  revokePrintStation,
  setPrintStationDesiredState,
  type PrintJobSummary,
  type PrintStation,
  type PrintStationEnrollment,
  type SharedPrinter,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { PrinterCalibrationControls } from './PrinterCalibrationControls';
import { isPrinterOffline, PrinterMediaRemaining } from './PrinterMediaRemaining';
import { paperLabel, PrinterPaperControl } from './PrinterPaperControl';
import { PrinterSharingControls } from './PrinterSharingControls';
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

export function PrintStationsPanel() {
  const { state, invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [stations, setStations] = useState<PrintStation[] | null>(null);
  const [shared, setShared] = useState<SharedPrinter[]>([]);
  const [name, setName] = useState('');
  const [enrollment, setEnrollment] = useState<PrintStationEnrollment | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Only the newest read is applied, so a slow refresh can never put back
  // what a later one (or an action's own reload) already replaced.
  const latestRead = useRef(0);
  const load = useCallback(async (signal?: AbortSignal, quiet = false) => {
    const read = ++latestRead.current;
    try {
      // Your stations, and the printers lent to you: the second is its own
      // list, so a person with no station of their own still finds theirs.
      const [own, lent] = await Promise.all([listPrintStations(signal), listSharedPrinters(signal)]);
      if (read !== latestRead.current) return;
      setStations(own);
      setShared(lent);
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
      setEnrollment(await createPrintStation(name.trim()));
      setName('');
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

  async function renew(station: PrintStation) {
    setBusy(station.id); setError(null);
    try { setEnrollment(await renewPrintStationEnrollment(station.id)); }
    catch { setError(t('print.enrollmentError')); }
    finally { setBusy(null); }
  }

  if (state.status !== 'authed') return null;

  return (
    <section className="print-stations" aria-busy={stations === null || busy !== null}>
      <form className="print-station-create" onSubmit={(event) => void create(event)}>
        <label>
          <span>{t('print.stationName')}</span>
          <input value={name} maxLength={120} onChange={(event) => setName(event.target.value)} />
        </label>
        <button type="submit" className="primary-button" disabled={!name.trim() || busy !== null}>
          {t('print.createStation')}
        </button>
        <button type="button" className="refresh-button" onClick={() => void load()}>
          {t('common.refresh')}
        </button>
      </form>

      {enrollment && (
        <section className="print-enrollment" role="status" data-testid="print-enrollment">
          <h3>{t('print.enrollmentTitle')}</h3>
          <p>{t('print.enrollmentWarning', { date: formatDate(enrollment.enrollmentExpiresAt) })}</p>
          <code>{`NubArca.PrintAgent.exe enroll --server ${window.location.origin} --station ${enrollment.id} --token ${enrollment.enrollmentToken}`}</code>
          <button type="button" onClick={() => setEnrollment(null)}>{t('common.close')}</button>
        </section>
      )}

      {error && <p className="folder-error" role="alert">{error}</p>}
      {stations === null && <p className="muted" role="status">{t('print.loading')}</p>}
      {stations?.length === 0 && shared.length === 0 && (
        <p className="muted" data-testid="print-empty">{t('print.empty')}</p>
      )}

      <ul className="print-station-list" aria-label={t('print.listLabel')}>
        {stations?.map((station) => {
          const observedPrinter = station.devices[0] ?? null;
          // The test page goes on the paper in the printer, so it needs a
          // printer that can print that paper.
          const printPrinter = station.devices.find((device) =>
            (device.papers ?? (device.supportsPhoto10x15 ? ['10x15'] : []))
              .includes(device.loadedPaperSize ?? '10x15')
            && (device.observedState === 'ready' || device.observedState === 'busy')) ?? null;
          return (
            <li key={station.id} className={`print-station-card print-station-${station.status}`}>
              <header>
                <div>
                  <h3>{station.name}</h3>
                  <span className={`print-status print-status-${station.status}`}>{t(STATUS_KEYS[station.status])}</span>
                </div>
                <span className="muted">{station.agentVersion ?? t('print.notEnrolled')}</span>
              </header>
              <dl>
                <div><dt>{t('print.printer')}</dt><dd>{observedPrinter?.displayName ?? t('print.noPrinter')}</dd></div>
                {observedPrinter && (
                  <PrinterMediaRemaining
                    remaining={observedPrinter.mediaRemainingPrints}
                    observedAt={observedPrinter.mediaRemainingObservedAt}
                    offline={isPrinterOffline(station.status, observedPrinter.observedState)}
                  />
                )}
                {observedPrinter && (
                  <div>
                    <dt>{t('print.stripCut')}</dt>
                    <dd data-testid="print-strip-cut">
                      {t(observedPrinter.cutsStrips ? 'print.stripCutPrinter' : 'print.stripCutHand')}
                    </dd>
                  </div>
                )}
                <div><dt>{t('print.lastSeen')}</dt><dd>{station.lastSeenAt ? formatDate(station.lastSeenAt) : '—'}</dd></div>
                <div><dt>{t('print.queue')}</dt><dd>{station.queueCount}</dd></div>
                <div><dt>{t('print.currentJob')}</dt><dd>{station.currentJob ? `${station.currentJob.shortCode} · ${station.currentJob.state}` : '—'}</dd></div>
                <div><dt>{t('print.lastError')}</dt><dd>{station.lastError ?? '—'}</dd></div>
              </dl>
              {(station.queue?.length ?? 0) > 0 && (
                <PrintQueueList jobs={station.queue ?? []} busy={busy === station.id}
                  onCancel={(job) => void action(station.id, () => cancelPrintJob(job.id), 'print.job.cancelError')} />
              )}
              {station.revokedAt === null && observedPrinter && (
                <PrinterPaperControl stationId={station.id} device={observedPrinter}
                  onSaved={() => load()} />
              )}
              {observedPrinter && <PrinterUsageSummary usage={observedPrinter.usage ?? []} />}
              {station.revokedAt === null && observedPrinter && (
                <PrinterCalibrationControls stationId={station.id} device={observedPrinter}
                  onSaved={() => load()} />
              )}
              {station.revokedAt === null && observedPrinter && (
                <PrinterSharingControls stationId={station.id} device={observedPrinter}
                  onChanged={() => load()} />
              )}
              {station.revokedAt === null && (
                <div className="print-station-actions">
                  {station.desiredState === 'running' ? (
                    <button type="button" disabled={busy === station.id}
                      onClick={() => void action(station.id,
                        () => setPrintStationDesiredState(station.id, 'paused'), 'print.actionError')}>
                      {t('print.pause')}
                    </button>
                  ) : (
                    <button type="button" disabled={busy === station.id}
                      onClick={() => void action(station.id,
                        () => setPrintStationDesiredState(station.id, 'running'), 'print.actionError')}>
                      {t('print.resume')}
                    </button>
                  )}
                  <button type="button" disabled={!printPrinter || station.desiredState !== 'running' || busy === station.id}
                    onClick={() => printPrinter && void action(station.id,
                      () => createPrintTestJob(station.id, printPrinter.id), 'print.testError')}>
                    {t('print.testPrint')}
                  </button>
                  <button type="button" disabled={busy === station.id} onClick={() => void renew(station)}>
                    {t('print.newEnrollment')}
                  </button>
                  <button type="button" className="destructive-button" disabled={busy === station.id}
                    onClick={() => window.confirm(t('print.revokeConfirm', { name: station.name }))
                      && void action(station.id, () => revokePrintStation(station.id), 'print.revokeError')}>
                    {t('print.revoke')}
                  </button>
                </div>
              )}
            </li>
          );
        })}
      </ul>

      <SharedPrintersList printers={shared} onChanged={() => load()} />
    </section>
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
