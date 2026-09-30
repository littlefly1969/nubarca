import { useState } from 'react';
import {
  ApiError, createPrintTestJob, type PrintStationStatus, type SharedPrinter,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { PrinterPaperControl } from './PrinterPaperControl';

const STATUS_KEYS: Record<PrintStationStatus, MessageKey> = {
  online: 'print.statusOnline',
  degraded: 'print.statusDegraded',
  offline: 'print.statusOffline',
  revoked: 'print.statusRevoked',
};

/** Sheets this loan still allows; null is no ceiling. */
function sheetsLeft(printer: SharedPrinter): number | null {
  return printer.maxSheets === null ? null : Math.max(0, printer.maxSheets - printer.usedSheets);
}

/**
 * The printers other people lend to the reader: the two things the reader
 * may do at one — say which paper is in, print a test page — and how many
 * of the loan's sheets are used. The owner's other printers, stations and
 * work are not here, and colours, pause and the loan itself stay theirs.
 */
export function SharedPrintersList({ printers, onChanged }: {
  printers: SharedPrinter[];
  onChanged: () => Promise<void>;
}) {
  const { t } = useI18n();
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<{ id: string; text: string; error: boolean } | null>(null);

  async function testPrint(printer: SharedPrinter) {
    setBusy(printer.shareId); setMessage(null);
    try {
      await createPrintTestJob(printer.stationId, printer.deviceId);
      await onChanged();
      setMessage({ id: printer.shareId, text: t('print.shared.testQueued'), error: false });
    } catch (err) {
      const code = err instanceof ApiError ? (err.body as { error?: string } | null)?.error : undefined;
      setMessage({
        id: printer.shareId,
        text: t(code === 'share_exhausted' ? 'print.shared.exhausted' : 'print.testError'),
        error: true,
      });
    } finally {
      setBusy(null);
    }
  }

  if (printers.length === 0) return null;
  return (
    <section className="print-shared" data-testid="print-shared">
      <h3>{t('print.shared.title')}</h3>
      <ul className="print-station-list" aria-label={t('print.shared.title')}>
        {printers.map((printer) => {
          const left = sheetsLeft(printer);
          const printable = printer.papers.includes(printer.loadedPaperSize)
            && (printer.observedState === 'ready' || printer.observedState === 'busy')
            && printer.stationStatus !== 'offline' && printer.stationStatus !== 'revoked'
            && left !== 0;
          return (
            <li key={printer.shareId} className={`print-station-card print-station-${printer.stationStatus}`}
              data-testid="print-shared-printer">
              <header>
                <div>
                  <h3>{printer.displayName}</h3>
                  <span className={`print-status print-status-${printer.stationStatus}`}>
                    {t(STATUS_KEYS[printer.stationStatus])}
                  </span>
                </div>
                <span className="muted">{t('print.shared.by', { name: printer.ownerName })}</span>
              </header>
              <dl>
                <div><dt>{t('print.shared.station')}</dt><dd>{printer.stationName}</dd></div>
                <div>
                  <dt>{t('print.stripCut')}</dt>
                  <dd>{t(printer.cutsStrips ? 'print.stripCutPrinter' : 'print.stripCutHand')}</dd>
                </div>
                <div>
                  <dt>{t('print.shared.sheets')}</dt>
                  <dd data-testid="print-shared-sheets">
                    {printer.maxSheets === null
                      ? t('print.share.usedNoCeiling', { used: printer.usedSheets })
                      : t('print.share.usedOf', { used: printer.usedSheets, max: printer.maxSheets })}
                  </dd>
                </div>
              </dl>
              <PrinterPaperControl stationId={printer.stationId} onSaved={onChanged}
                device={{
                  id: printer.deviceId,
                  supportsPhoto10x15: printer.supportsPhoto10x15,
                  loadedPaperSize: printer.loadedPaperSize,
                  papers: printer.papers,
                  loadedPaperChangedBy: printer.loadedPaperChangedBy,
                  loadedPaperChangedAt: printer.loadedPaperChangedAt,
                }} />
              <div className="print-station-actions">
                <button type="button" disabled={!printable || busy === printer.shareId}
                  onClick={() => void testPrint(printer)}>
                  {t('print.testPrint')}
                </button>
              </div>
              {left === 0 && <p className="muted" role="status">{t('print.shared.exhausted')}</p>}
              {message?.id === printer.shareId && (
                <p role={message.error ? 'alert' : 'status'} className={message.error ? 'folder-error' : 'muted'}>
                  {message.text}
                </p>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
