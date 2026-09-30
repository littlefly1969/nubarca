import { useState } from 'react';
import {
  PRINT_PAPER_SIZES, setPrinterPaper, type PrintDevice, type PrintPaperSize,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';

/** A paper as the photo trade writes it. */
export function paperLabel(paper: string): string {
  return paper.replace('x', '\u00d7');
}

/** What the control reads of a printer: the owner's own, or one lent to the reader. */
export type PaperPrinter = Pick<PrintDevice,
  'id' | 'supportsPhoto10x15' | 'loadedPaperSize' | 'papers' | 'loadedPaperChangedBy' | 'loadedPaperChangedAt'>;

/**
 * Which paper is in the printer, said by the operator: a dye-sublimation
 * printer takes one roll, and neither its Windows driver nor Gutenprint on
 * CUPS reports which. Guests are offered what that paper can make — a photo
 * and four photos on any, the twin strip only on 10x15 — and a sheet made
 * for another paper waits in the queue until that paper is in, so changing
 * rolls starts here. Whoever is standing at the printer sets it: its owner,
 * or the person it is lent to.
 */
export function PrinterPaperControl({ stationId, device, onSaved }: {
  stationId: string;
  device: PaperPrinter;
  onSaved: () => Promise<void>;
}) {
  const { t, formatDate } = useI18n();
  const loaded = device.loadedPaperSize ?? '10x15';
  const papers = device.papers ?? (device.supportsPhoto10x15 ? ['10x15'] : []);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  async function choose(paper: PrintPaperSize) {
    setBusy(true); setFailed(false);
    try {
      await setPrinterPaper(stationId, device.id, paper);
      await onSaved();
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="print-paper" data-testid="print-paper">
      <label className="print-paper-field">
        <span>{t('print.paper.label')}</span>
        <select value={loaded} disabled={busy} onChange={(e) => void choose(e.target.value as PrintPaperSize)}>
          {PRINT_PAPER_SIZES.map((paper) => (
            <option key={paper} value={paper}>{paperLabel(paper)}</option>
          ))}
        </select>
      </label>
      {device.loadedPaperChangedAt && (
        <p className="muted" data-testid="print-paper-changed">
          {t('print.paper.changed', {
            name: device.loadedPaperChangedBy || '—', date: formatDate(device.loadedPaperChangedAt),
          })}
        </p>
      )}
      <p className="muted">{t('print.paper.hint')}</p>
      {/* The paper is in, but the agent cannot print it: guests are offered
          nothing until it can, and the operator should know why. */}
      {!papers.includes(loaded) && (
        <p className="print-paper-warning" role="alert" data-testid="print-paper-unsupported">
          {t('print.paper.unsupported', { paper: paperLabel(loaded) })}
        </p>
      )}
      {failed && <p className="print-paper-warning" role="alert">{t('print.paper.error')}</p>}
    </div>
  );
}
