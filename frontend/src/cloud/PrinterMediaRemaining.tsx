import { useI18n, type I18nContextValue } from '../i18n';

// THE PRINTS LEFT ON A PRINTER'S MEDIA, as the printer itself reports them.
//
// Three different numbers live near each other on these surfaces and must
// never be read as one another: this one (physical media, the printer's own
// estimate), a party's prints left (its budget) and a loan's sheets (the
// owner's ceiling). This one is telemetry — never a budget, never a gate: a
// printer that says 0 still gets its job, and the printer decides.
//
// An offline printer's last count is not presented as current: it is labelled
// as the last reading, with the time it was taken.

/** Whether a printer cannot be printed on now, by the same rule the server refuses a print. */
export function isPrinterOffline(stationStatus: string, observedState: string): boolean {
  return stationStatus === 'offline' || stationStatus === 'revoked' || observedState === 'offline';
}

/**
 * The same reading as one line, for a printer offered as a choice: the live
 * count, the last one with its time when the printer is offline, or that there
 * is no number.
 */
export function mediaRemainingLine(
  { t, formatDate }: Pick<I18nContextValue, 't' | 'formatDate'>,
  remaining: number | null | undefined, observedAt: string | null | undefined, offline: boolean,
): string {
  if (typeof remaining !== 'number') return t('print.media.inlineUnavailable');
  if (!offline) return t('print.media.inline', { count: remaining });
  const time = observedAt ? formatDate(observedAt, { hour: '2-digit', minute: '2-digit' }) : '—';
  return t('print.media.inlineLast', { count: remaining, time });
}

/** One `dt`/`dd` pair, for a `dl` beside the printer's other facts. */
export function PrinterMediaRemaining({
  remaining, observedAt, offline, testId = 'print-media-remaining',
}: {
  remaining: number | null | undefined;
  observedAt: string | null | undefined;
  offline: boolean;
  testId?: string;
}) {
  const { t, formatDate } = useI18n();
  const known = typeof remaining === 'number';
  let value: string;
  let note: string;
  if (known && offline) {
    const time = observedAt ? formatDate(observedAt, { hour: '2-digit', minute: '2-digit' }) : '—';
    value = t('print.media.last', { count: remaining, time });
    note = t('print.media.estimate');
  } else if (known) {
    value = String(remaining);
    note = t('print.media.estimate');
  } else {
    value = '—';
    note = t('print.media.unavailable');
  }
  return (
    <div className="print-media-remaining" data-testid={testId} data-state={!known ? 'unknown' : offline ? 'last' : 'live'}>
      <dt>{t('print.media.remaining')}</dt>
      <dd>
        <span className="print-media-remaining-value" data-testid={`${testId}-value`}>{value}</span>
        <span className="print-media-remaining-note muted">{note}</span>
        {known && !offline && remaining === 0 && (
          <span className="print-media-remaining-empty" role="status">{t('print.media.empty')}</span>
        )}
      </dd>
    </div>
  );
}
