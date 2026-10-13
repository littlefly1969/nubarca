import { useCallback, useEffect, useState } from 'react';
import {
  clearSimulatedPrints,
  listSimulatedPrints,
  setPrinterSimulation,
  simulatedSheetUrl,
  type PrintDevice,
  type SimulatedPrint,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { paperLabel } from './PrinterPaperControl';

/**
 * A printer's simulated twin, for its owner: the switch between paper and the
 * twin, and — while simulated — the sheets it made, each to look at or
 * download, and one button that clears them here and on the box.
 */
export function PrinterSimulationControls({ stationId, device, onChanged }: {
  stationId: string;
  device: PrintDevice;
  onChanged: () => Promise<void>;
}) {
  const { t, formatDate } = useI18n();
  const simulated = device.simulated === true;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [prints, setPrints] = useState<SimulatedPrint[] | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try { setPrints(await listSimulatedPrints(stationId, device.id, signal)); }
    catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setError(t('print.sim.loadError'));
    }
  }, [stationId, device.id, t]);

  useEffect(() => {
    if (!simulated) return undefined;
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [simulated, load]);

  async function toggle() {
    setBusy(true); setError(null); setNotice(null);
    try {
      await setPrinterSimulation(stationId, device.id, !simulated);
      await onChanged();
    } catch { setError(t('print.sim.error')); }
    finally { setBusy(false); }
  }

  async function clear() {
    if (!window.confirm(t('print.sim.clearConfirm'))) return;
    setBusy(true); setError(null);
    try {
      const { cleared } = await clearSimulatedPrints(stationId, device.id);
      setNotice(t('print.sim.cleared', { count: cleared }));
      await load();
    } catch { setError(t('print.sim.error')); }
    finally { setBusy(false); }
  }

  return (
    <section className="print-station-section print-simulation" data-testid="print-simulation">
      <h4>{t('print.sim.title')}</h4>
      {device.simulationOnly ? (
        <p className="print-simulation-active">{t('print.sim.only')}</p>
      ) : device.simulationAvailable ? (
        <>
          <p className="muted">{t('print.sim.help')}</p>
          {simulated && <p className="print-simulation-active" role="status">{t('print.sim.active')}</p>}
          <div className="print-station-actions">
            <button type="button" className={simulated ? '' : 'primary-button'} disabled={busy}
              aria-pressed={simulated} onClick={() => void toggle()}>
              {t(simulated ? 'print.sim.off' : 'print.sim.on')}
            </button>
          </div>
        </>
      ) : (
        <p className="muted">{t('print.sim.unavailable')}</p>
      )}
      {error && <p className="folder-error" role="alert">{error}</p>}

      {simulated && (
        <div className="print-simulated-prints">
          <div className="print-simulated-head">
            <h5>{t('print.sim.prints')}</h5>
            <button type="button" disabled={busy || !prints?.length} onClick={() => void clear()}>
              {t('print.sim.clear')}
            </button>
          </div>
          {notice && <p className="muted" role="status">{notice}</p>}
          {prints?.length === 0 && <p className="muted">{t('print.sim.none')}</p>}
          <ul className="print-simulated-list">
            {prints?.map((print) => (
              <li key={print.id} className="print-simulated-row" data-testid="print-simulated-row">
                <img src={simulatedSheetUrl(print.id)} alt={t('print.sim.sheetAlt', { code: print.shortCode })}
                  loading="lazy" />
                <span className="print-simulated-text">
                  <code>{print.shortCode}</code>
                  <span className="muted">
                    {[print.format === '2x6x2' ? '2×6' : paperLabel(print.format), formatDate(print.createdAt),
                      ...(print.ownerName ? [print.ownerName] : [])].join(' · ')}
                  </span>
                </span>
                <a className="print-simulated-download" href={simulatedSheetUrl(print.id)}
                  download={`nubarca-${print.shortCode}.png`}>
                  {t('print.sim.download')}
                </a>
              </li>
            ))}
          </ul>
        </div>
      )}
    </section>
  );
}
