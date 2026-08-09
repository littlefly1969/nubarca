import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getTvPersonalPinStatus,
  isCompleteTvCode,
  listTvDevices,
  revokeTvDevice,
  setTvPersonalCode,
  type TvDevice,
  type TvPersonalPinStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { TvCodeInput } from '../tv/TvCodeInput';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; devices: TvDevice[] }
  | { kind: 'error' };

interface Banner {
  tone: 'info' | 'error';
  text: string;
}

const STATUS_LABEL_KEY: Record<TvDevice['status'], MessageKey> = {
  active: 'tvDevices.statusActive',
  expired: 'tvDevices.statusExpired',
  revoked: 'tvDevices.statusRevoked',
};

// Owner-facing management of paired TV sessions. Lists this owner's TV devices
// and lets them revoke one — which immediately terminates that limited TV
// session server-side. No tokens/hashes/secrets are shown.
export function TvDevicesPanel() {
  const { state, invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [status, setStatus] = useState<LoadState>({ kind: 'loading' });
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(new Set());
  const [banner, setBanner] = useState<Banner | null>(null);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      try {
        const devices = await listTvDevices(signal);
        setStatus({ kind: 'ready', devices });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setStatus({ kind: 'error' });
      }
    },
    [invalidateAuth],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function onRevoke(device: TvDevice) {
    if (busyIds.has(device.id)) return;
    const name = device.deviceLabel ?? t('tvDevices.thisTv');
    if (!window.confirm(t('tvDevices.confirmRevoke', { name }))) {
      return;
    }

    setBusyIds((prev) => new Set(prev).add(device.id));
    setBanner(null);
    try {
      await revokeTvDevice(device.id);
      setBanner({ tone: 'info', text: t('tvDevices.revoked') });
      await load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 404) {
        setBanner({ tone: 'info', text: t('tvDevices.gone') });
        await load();
        return;
      }
      setBanner({ tone: 'error', text: t('tvDevices.revokeError') });
    } finally {
      setBusyIds((prev) => {
        const next = new Set(prev);
        next.delete(device.id);
        return next;
      });
    }
  }

  if (state.status !== 'authed') {
    // ProtectedRoute already enforces this; keeps TypeScript happy.
    return null;
  }

  return (
    <section className="tv-devices-page" aria-busy={status.kind === 'loading'}>
      {/* The Cloud Functions hub owns the tool title + description; only the
          panel's own refresh control stays here. */}
      <header className="tv-devices-header">
        <button
          type="button"
          className="refresh-button"
          onClick={() => void load()}
          disabled={status.kind === 'loading'}
        >
          {t('common.refresh')}
        </button>
      </header>
      <p className="muted">{t('tvDevices.intro')}</p>

      {banner !== null && (
        <p
          className={`shares-banner shares-banner-${banner.tone}`}
          role={banner.tone === 'error' ? 'alert' : 'status'}
        >
          {banner.text}
        </p>
      )}

      {status.kind === 'loading' && (
        <p className="muted" role="status">{t('tvDevices.loading')}</p>
      )}

      {status.kind === 'error' && (
        <div className="folder-error" role="alert">
          {t('tvDevices.loadError')}
          <button type="button" className="retry-button" onClick={() => void load()}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {status.kind === 'ready' && status.devices.length === 0 && (
        <p className="muted" data-testid="tv-devices-empty">
          {t('tvDevices.empty')}
        </p>
      )}

      <TvPersonalPinPanel />

      {status.kind === 'ready' && status.devices.length > 0 && (
        <ul className="tv-devices-list" aria-label={t('tvDevices.listLabel')}>
          {status.devices.map((device) => (
            <li key={device.id} className={`tv-device-row tv-device-row-${device.status}`}>
              <div className="tv-device-main">
                <span className="tv-device-name">{device.deviceLabel ?? t('tvDevices.deviceFallback')}</span>
                {device.userAgent && (
                  <span className="tv-device-agent muted" title={device.userAgent}>
                    {device.userAgent}
                  </span>
                )}
                <span className="tv-device-meta muted">
                  {t('tvDevices.paired', { date: formatDate(device.createdAt) })}
                  {' · '}{t('tvDevices.lastSeen', { date: formatDate(device.lastSeenAt) })}
                  {' · '}{t('tvDevices.expires', { date: formatDate(device.expiresAt) })}
                </span>
              </div>
              <div className="tv-device-side">
                <span className={`tv-device-badge tv-device-badge-${device.status}`}>
                  {t(STATUS_LABEL_KEY[device.status])}
                </span>
                {device.status === 'active' && (
                  <button
                    type="button"
                    className="destructive-button"
                    onClick={() => void onRevoke(device)}
                    disabled={busyIds.has(device.id)}
                  >
                    {busyIds.has(device.id) ? t('tvDevices.revoking') : t('tvDevices.revoke')}
                  </button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// Owner-side Personal Area credential management ("Codice TV area personale").
// Configure a missing code, or change/reset the current one — no old code
// required: the authenticated owner session IS the authorization. Saving
// revokes every outstanding TV unlock grant (all TVs re-ask for the new code);
// the pairings themselves stay valid and Party keeps working. The entered code
// lives only in component state and is cleared after every submit, success or
// failure — never persisted, never logged.
//
// "Change" and "Reset" are the same operation and deliberately so: the server
// holds only a hash, so there is nothing to "reset to". Both open this editor
// and replace the credential. There is no state in which an account has no
// credential at all — that would strand every paired television, because a TV
// with no configured secret reads as an incomplete pairing and tears itself
// down.
//
// An account still on the retired numeric PIN lands here too: saving a
// directional code replaces the scheme in the same transaction, which is the
// only supported crossover.
function TvPersonalPinPanel() {
  const { invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [pinStatus, setPinStatus] = useState<TvPersonalPinStatus | null | 'error'>(null);
  const [code, setCode] = useState('');
  const [confirmCode, setConfirmCode] = useState('');
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: 'info' | 'error'; text: string } | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    getTvPersonalPinStatus(controller.signal)
      .then(setPinStatus)
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setPinStatus('error');
      });
    return () => controller.abort();
  }, [invalidateAuth]);

  async function submit() {
    if (!isCompleteTvCode(code)) {
      setMessage({ tone: 'error', text: t('tvPin.invalid') });
      return;
    }
    if (code !== confirmCode) {
      setMessage({ tone: 'error', text: t('tvPin.mismatch') });
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      const updated = await setTvPersonalCode(code, confirmCode);
      setPinStatus(updated);
      setEditing(false);
      setMessage({ tone: 'info', text: t('tvPin.saved') });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      const body = err instanceof ApiError ? (err.body as { error?: string } | null) : null;
      setMessage({
        tone: 'error',
        text: body?.error === 'invalid_code'
          ? t('tvPin.invalid')
          : body?.error === 'code_mismatch' ? t('tvPin.mismatch') : t('tvPin.error'),
      });
    } finally {
      // The code never outlives the submit.
      setCode('');
      setConfirmCode('');
      setBusy(false);
    }
  }

  const loaded = pinStatus !== null && pinStatus !== 'error' ? pinStatus : null;
  const configured = loaded?.configured === true;
  // A configured-but-legacy account is the one case that needs a call to
  // action rather than a plain status line: its televisions still unlock, but
  // the current TV app has no numeric entry surface to offer them.
  const legacy = configured && loaded?.scheme === 'pin-v1';

  return (
    <section className="tv-pin-panel" data-testid="tv-pin-panel">
      <h3>{t('tvPin.title')}</h3>
      <p className="muted">{t('tvPin.intro')}</p>
      {pinStatus === 'error' && <p role="alert">{t('tvPin.loadError')}</p>}
      {pinStatus !== null && pinStatus !== 'error' && (
        <p className="muted" data-testid="tv-pin-status">
          {pinStatus.configured && pinStatus.updatedAt
            ? t('tvPin.statusConfigured', { date: formatDate(pinStatus.updatedAt) })
            : t('tvPin.statusUnconfigured')}
        </p>
      )}
      {legacy && (
        <p className="shares-banner shares-banner-info" role="status" data-testid="tv-pin-legacy">
          {t('tvPin.statusLegacy')}
        </p>
      )}
      {!editing ? (
        <div className="tv-pin-actions">
          <button type="button" onClick={() => { setMessage(null); setEditing(true); }}>
            {configured && !legacy ? t('tvPin.changeButton') : t('tvPin.setButton')}
          </button>
          {configured && (
            <button type="button" onClick={() => { setMessage(null); setEditing(true); }}>
              {t('tvPin.resetButton')}
            </button>
          )}
        </div>
      ) : (
        <form
          className="tv-pin-form tv-pin-form-inline"
          noValidate
          onSubmit={(e) => {
            e.preventDefault();
            void submit();
          }}
        >
          <TvCodeInput
            id="tv-personal-code"
            label={t('tvPin.newCode')}
            value={code}
            onChange={setCode}
            disabled={busy}
          />
          <TvCodeInput
            id="tv-personal-code-confirm"
            label={t('tvPin.confirmCode')}
            value={confirmCode}
            onChange={setConfirmCode}
            disabled={busy}
          />
          <button type="submit" disabled={busy}>
            {busy ? t('tvPin.saving') : configured ? t('tvPin.changeButton') : t('tvPin.setButton')}
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => { setEditing(false); setCode(''); setConfirmCode(''); }}
          >
            {t('tvPin.cancelButton')}
          </button>
        </form>
      )}
      {message !== null && (
        <p
          className={`shares-banner shares-banner-${message.tone}`}
          role={message.tone === 'error' ? 'alert' : 'status'}
        >
          {message.text}
        </p>
      )}
    </section>
  );
}
