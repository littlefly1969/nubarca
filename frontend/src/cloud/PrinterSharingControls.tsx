import { useState, type FormEvent } from 'react';
import {
  ApiError, PRINTER_SHARE_MAX_SHEETS, revokePrinterShare, sharePrinter, updatePrinterShare,
  type PrintDevice, type PrinterShare, type PrinterShareError,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';

const SHARE_ERRORS: Record<PrinterShareError, MessageKey> = {
  recipient_not_found: 'print.share.errorRecipientNotFound',
  recipient_is_owner: 'print.share.errorRecipientIsOwner',
  already_shared: 'print.share.errorAlreadyShared',
  invalid_ceiling: 'print.share.errorInvalidCeiling',
  ceiling_below_used: 'print.share.errorCeilingBelowUsed',
};

/** A ceiling as typed: empty is none, anything else must be a whole number. */
function parseCeiling(value: string): number | null | undefined {
  const text = value.trim();
  if (text === '') return null;
  if (!/^\d+$/.test(text)) return undefined;
  return Number(text);
}

function errorKey(err: unknown): MessageKey {
  const code = err instanceof ApiError ? (err.body as { error?: string } | null)?.error : undefined;
  return (code && code in SHARE_ERRORS) ? SHARE_ERRORS[code as PrinterShareError] : 'print.share.error';
}

/**
 * Lending one printer: to a person, by the email of their account, with an
 * optional ceiling of sheets that starts again on every new loan. The person
 * may set its paper and print a test page; its colours, pause and loans stay
 * the owner's. Ending a loan lets what is queued print and accepts nothing new.
 */
export function PrinterSharingControls({ stationId, device, onChanged }: {
  stationId: string;
  device: PrintDevice;
  onChanged: () => Promise<void>;
}) {
  const { t } = useI18n();
  const shares = device.shares ?? [];
  const [open, setOpen] = useState(false);
  const [email, setEmail] = useState('');
  const [ceiling, setCeiling] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ text: string; error: boolean } | null>(null);

  async function run(operation: () => Promise<unknown>, done: MessageKey): Promise<boolean> {
    setBusy(true); setMessage(null);
    try {
      await operation();
      await onChanged();
      setMessage({ text: t(done), error: false });
      return true;
    } catch (err) {
      setMessage({ text: t(errorKey(err)), error: true });
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function share(event: FormEvent) {
    event.preventDefault();
    const maxSheets = parseCeiling(ceiling);
    if (!email.trim()) return;
    if (maxSheets === undefined) { setMessage({ text: t('print.share.errorInvalidCeiling'), error: true }); return; }
    if (await run(() => sharePrinter(stationId, device.id, email.trim(), maxSheets), 'print.share.created')) {
      setEmail(''); setCeiling('');
    }
  }

  return (
    <section className="print-share" data-testid="print-share">
      <button type="button" className="print-calibration-toggle" aria-expanded={open}
        onClick={() => setOpen((value) => !value)}>
        {shares.length > 0 ? t('print.share.toggleCount', { count: shares.length }) : t('print.share.toggle')}
      </button>
      {open && (
        <div className="print-share-body">
          <p className="muted">{t('print.share.help')}</p>
          {shares.length > 0 && (
            <ul className="print-share-list" aria-label={t('print.share.listLabel')}>
              {shares.map((item) => (
                <ShareRow key={item.id} share={item} busy={busy}
                  onUpdate={(maxSheets) => run(() => updatePrinterShare(item.id, maxSheets), 'print.share.updated')}
                  onRevoke={() => window.confirm(t('print.share.revokeConfirm', { name: item.granteeName }))
                    && void run(() => revokePrinterShare(item.id), 'print.share.revoked')}
                  onInvalid={() => setMessage({ text: t('print.share.errorInvalidCeiling'), error: true })} />
              ))}
            </ul>
          )}
          <form className="print-share-form" onSubmit={(event) => void share(event)}>
            <label>
              <span>{t('print.share.email')}</span>
              <input type="email" value={email} maxLength={320} autoComplete="off"
                onChange={(event) => setEmail(event.target.value)} />
            </label>
            <label>
              <span>{t('print.share.ceiling')}</span>
              <input type="number" inputMode="numeric" min={1} max={PRINTER_SHARE_MAX_SHEETS}
                value={ceiling} placeholder={t('print.share.noCeiling')}
                onChange={(event) => setCeiling(event.target.value)} />
            </label>
            <button type="submit" className="primary-button" disabled={busy || !email.trim()}>
              {t('print.share.submit')}
            </button>
          </form>
          {message && (
            <p role={message.error ? 'alert' : 'status'} className={message.error ? 'folder-error' : 'muted'}>
              {message.text}
            </p>
          )}
        </div>
      )}
    </section>
  );
}

function ShareRow({ share, busy, onUpdate, onRevoke, onInvalid }: {
  share: PrinterShare;
  busy: boolean;
  onUpdate: (maxSheets: number | null) => Promise<boolean>;
  onRevoke: () => void;
  onInvalid: () => void;
}) {
  const { t, formatDate } = useI18n();
  const [ceiling, setCeiling] = useState(share.maxSheets === null ? '' : String(share.maxSheets));
  const saved = share.maxSheets === null ? '' : String(share.maxSheets);

  function save() {
    const maxSheets = parseCeiling(ceiling);
    if (maxSheets === undefined) { onInvalid(); return; }
    void onUpdate(maxSheets);
  }

  return (
    <li className="print-share-row" data-testid="print-share-row">
      <div>
        <strong>{share.granteeName}</strong> <span className="muted">{share.granteeEmail}</span>
        <p className="muted">
          {share.maxSheets === null
            ? t('print.share.usedNoCeiling', { used: share.usedSheets })
            : t('print.share.usedOf', { used: share.usedSheets, max: share.maxSheets })}
          {' · '}{t('print.share.since', { date: formatDate(share.createdAt) })}
        </p>
      </div>
      <div className="print-share-row-actions">
        <label>
          <span className="visually-hidden">{t('print.share.ceilingFor', { name: share.granteeName })}</span>
          <input type="number" inputMode="numeric" min={Math.max(1, share.usedSheets)} max={PRINTER_SHARE_MAX_SHEETS}
            value={ceiling} placeholder={t('print.share.noCeiling')}
            onChange={(event) => setCeiling(event.target.value)} />
        </label>
        <button type="button" disabled={busy || ceiling.trim() === saved} onClick={save}>
          {t('print.share.updateCeiling')}
        </button>
        <button type="button" className="destructive-button" disabled={busy} onClick={onRevoke}>
          {t('print.share.revoke')}
        </button>
      </div>
    </li>
  );
}
