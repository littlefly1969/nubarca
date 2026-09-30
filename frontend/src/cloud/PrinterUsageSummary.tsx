import type { PrinterUsage } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { paperLabel } from './PrinterPaperControl';

/**
 * Sheets per person on one printer, across its whole history and every loan:
 * how many it accepted — what a ceiling counts — how many came out, on which
 * paper and from where. Never what was on them.
 */
export function PrinterUsageSummary({ usage }: { usage: PrinterUsage[] }) {
  const { t } = useI18n();
  if (usage.length === 0) return null;
  return (
    <section className="print-usage" data-testid="print-usage">
      <h4>{t('print.usage.title')}</h4>
      <ul>
        {usage.map((row, index) => {
          const papers = Object.entries(row.byPaper)
            .map(([paper, sheets]) => `${paperLabel(paper)}: ${sheets}`);
          const sources = [
            row.parties > 0 ? t('print.usage.parties', { count: row.parties }) : null,
            row.album > 0 ? t('print.usage.album', { count: row.album }) : null,
            row.tests > 0 ? t('print.usage.tests', { count: row.tests }) : null,
          ].filter((x): x is string => x !== null);
          return (
            <li key={row.isYou ? 'you' : `${index}-${row.name ?? ''}`} data-testid="print-usage-row">
              <strong>{row.isYou ? t('print.usage.you') : (row.name || t('print.usage.unknown'))}</strong>
              {' '}{t('print.usage.sheets', { sheets: row.sheets, completed: row.completed })}
              {[...papers, ...sources].length > 0 && (
                <span className="muted"> · {[...papers, ...sources].join(' · ')}</span>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
