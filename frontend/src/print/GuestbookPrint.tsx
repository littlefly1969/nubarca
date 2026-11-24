import { useRef, useState } from 'react';
import { ApiError, type PartyPrintAccepted, type PartyPrintManifest, submitPartyPrint } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { newIdempotencyKey } from './studio/PrintStudio';

/** A published memory is one fixed composition, with no second text editor. */
export function GuestbookPrint({ token, entryId, manifest, onAccepted, onRefresh }: {
  token: string; entryId: string; manifest: PartyPrintManifest; onAccepted(result: PartyPrintAccepted): void;
  onRefresh?(): void;
}) {
  const { t } = useI18n();
  const key = useRef(newIdempotencyKey());
  const [shape, setShape] = useState<{ width: number; height: number } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const format = manifest.formats.find((f) => f.type === 'photo' && f.enabled);
  const allowed = format && format.remaining > 0 && (format.remainingForYou === null || format.remainingForYou > 0);
  const print = async () => {
    if (!shape || busy) return;
    setBusy(true); setError(null);
    try {
      onAccepted(await submitPartyPrint(token, {
        product: 'photo', theme: 'pure', guestbookEntryId: entryId,
        slots: [{ itemId: entryId, placement: { centerX: .5, centerY: .5, zoom: 1 } }],
        orientation: shape.height >= shape.width ? 'portrait' : 'landscape',
        paperSize: manifest.paperSize ?? '10x15',
      }, key.current));
    } catch (err) {
      const code = err instanceof ApiError ? (err.body as { error?: string } | null)?.error : null;
      const errors: Record<string, MessageKey> = {
        budget_exhausted: 'partyPrint.error.budget', guest_budget_exhausted: 'partyPrint.error.guestBudget',
        paper_changed: 'partyPrint.error.paperChanged', invalid_source: 'partyPrint.error.source',
        printer_unavailable: 'partyPrint.error.printer', share_exhausted: 'partyPrint.error.shareExhausted',
      };
      setError(code && errors[code] ? errors[code] : 'partyPrint.error.render');
      if (code === 'invalid_source' || (err instanceof ApiError && err.status === 404)) setUnavailable(true);
      onRefresh?.();
    }
    finally { setBusy(false); }
  };
  return <section className="party-print-step" aria-label={t('photoEdit.printMemory')}>
    <h2>{t('photoEdit.printMemory')}</h2>
    <p>{t('photoEdit.memoryPrintHelp')}</p>
    {!unavailable && <img className="guestbook-print-preview"
      src={`/api/party/${encodeURIComponent(token)}/print/guestbook/${encodeURIComponent(entryId)}/preview`}
      alt={t('photoEdit.memoryPreview')}
      onLoad={(e) => setShape({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })}
      onError={() => setUnavailable(true)} />}
    {unavailable && <p role="alert">{t('photoEdit.loadError')}</p>}
    {!allowed && <p>{t('partyPrint.unavailable')}</p>}
    {error && <p role="alert">{t(error)}</p>}
    <button type="button" className="party-print-primary" disabled={busy || !allowed || !shape || unavailable}
      onClick={() => void print()}>{t(busy ? 'partyPrint.submitting' : 'photoEdit.printMemory')}</button>
  </section>;
}
