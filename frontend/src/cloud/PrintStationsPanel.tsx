import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  ApiError,
  createPrintStation,
  createPrintTestJob,
  listPrintStations,
  renewPrintStationEnrollment,
  revokePrintStation,
  setPrintStationDesiredState,
  type PrintStation,
  type PrintStationEnrollment,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

const STATUS_KEYS: Record<PrintStation['status'], MessageKey> = {
  online: 'print.statusOnline',
  degraded: 'print.statusDegraded',
  offline: 'print.statusOffline',
  revoked: 'print.statusRevoked',
};

export function PrintStationsPanel() {
  const { state, invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [stations, setStations] = useState<PrintStation[] | null>(null);
  const [name, setName] = useState('');
  const [enrollment, setEnrollment] = useState<PrintStationEnrollment | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setStations(await listPrintStations(signal));
      setError(null);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('print.loadError'));
    }
  }, [invalidateAuth, t]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
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
      {stations?.length === 0 && <p className="muted" data-testid="print-empty">{t('print.empty')}</p>}

      <ul className="print-station-list" aria-label={t('print.listLabel')}>
        {stations?.map((station) => {
          const observedPrinter = station.devices[0] ?? null;
          const printPrinter = station.devices.find((device) => device.supportsPhoto10x15
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
                <div><dt>{t('print.lastSeen')}</dt><dd>{station.lastSeenAt ? formatDate(station.lastSeenAt) : '—'}</dd></div>
                <div><dt>{t('print.queue')}</dt><dd>{station.queueCount}</dd></div>
                <div><dt>{t('print.currentJob')}</dt><dd>{station.currentJob ? `${station.currentJob.shortCode} · ${station.currentJob.state}` : '—'}</dd></div>
                <div><dt>{t('print.lastError')}</dt><dd>{station.lastError ?? '—'}</dd></div>
              </dl>
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
    </section>
  );
}
