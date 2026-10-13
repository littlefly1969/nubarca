import { useState } from 'react';
import { setPrintAgentSharing, type PrintAgentBundle } from '@nubarca/api-client';
import { useI18n } from '../i18n';

/**
 * The administrator's switch for the Print Agent bundle this installation
 * hands out: which version is published, and whether a Print Box may download
 * it now. Shown only to whoever may switch it.
 */
export function PrintAgentBundleCard({ bundle, onChanged }: {
  bundle: PrintAgentBundle;
  onChanged: (bundle: PrintAgentBundle) => void;
}) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  async function toggle() {
    setBusy(true); setError(false);
    try { onChanged(await setPrintAgentSharing(!bundle.sharing)); }
    catch { setError(true); }
    finally { setBusy(false); }
  }

  return (
    <section className="print-bundle-card" data-testid="print-bundle">
      <div className="print-bundle-text">
        <h3>{t('print.bundle.title')}</h3>
        {bundle.available ? (
          <>
            <p>{t('print.bundle.version', { version: bundle.version ?? '' })}</p>
            <p className="muted" data-testid="print-bundle-state">
              {t(bundle.sharing ? 'print.bundle.sharingOn' : 'print.bundle.sharingOff')}
            </p>
          </>
        ) : (
          <p className="muted">{t('print.bundle.none')}</p>
        )}
        {error && <p className="folder-error" role="alert">{t('print.bundle.error')}</p>}
      </div>
      {bundle.available && (
        <button type="button" className={bundle.sharing ? '' : 'primary-button'} disabled={busy}
          aria-pressed={bundle.sharing} onClick={() => void toggle()}>
          {t(bundle.sharing ? 'print.bundle.turnOff' : 'print.bundle.turnOn')}
        </button>
      )}
    </section>
  );
}
