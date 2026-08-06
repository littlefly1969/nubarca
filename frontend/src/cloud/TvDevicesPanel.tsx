import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  getTvPersonalPinStatus,
  listTvDevices,
  revokeTvDevice,
  setTvPersonalPin,
  type TvDevice,
  type TvPersonalPinStatus,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

const PIN_PATTERN = /^\d{6}$/;

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

// Owner-side Personal Area PIN management ("PIN Area personale"). Create a
// missing PIN (legacy/incomplete data) or change/reset the current one — no
// old PIN required: the authenticated owner session IS the authorization.
// Changing it revokes every outstanding TV unlock grant (all TVs re-ask for
// the new PIN); the pairings themselves stay valid and Party keeps working.
// The entered PIN lives only in component state and is cleared after every
// submit, success or failure — never persisted, never logged.
function TvPersonalPinPanel() {
  const { invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [pinStatus, setPinStatus] = useState<TvPersonalPinStatus | null | 'error'>(null);
  const [pin, setPin] = useState('');
  const [confirmPin, setConfirmPin] = useState('');
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
    if (!PIN_PATTERN.test(pin)) {
      setMessage({ tone: 'error', text: t('tvPin.invalid') });
      return;
    }
    if (pin !== confirmPin) {
      setMessage({ tone: 'error', text: t('tvPin.mismatch') });
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      const updated = await setTvPersonalPin(pin, confirmPin);
      setPinStatus(updated);
      setMessage({ tone: 'info', text: t('tvPin.saved') });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      const body = err instanceof ApiError ? (err.body as { error?: string } | null) : null;
      setMessage({
        tone: 'error',
        text: body?.error === 'invalid_pin'
          ? t('tvPin.invalid')
          : body?.error === 'pin_mismatch' ? t('tvPin.mismatch') : t('tvPin.error'),
      });
    } finally {
      // The PIN never outlives the submit.
      setPin('');
      setConfirmPin('');
      setBusy(false);
    }
  }

  const configured = pinStatus !== null && pinStatus !== 'error' && pinStatus.configured;

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
      <form
        className="tv-pin-form tv-pin-form-inline"
        noValidate
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
      >
        <label>
          {t('tvPin.newPin')}
          <input
            type="password"
            inputMode="numeric"
            autoComplete="off"
            pattern="\d{6}"
            maxLength={6}
            value={pin}
            onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
            disabled={busy}
          />
        </label>
        <label>
          {t('tvPin.confirmPin')}
          <input
            type="password"
            inputMode="numeric"
            autoComplete="off"
            pattern="\d{6}"
            maxLength={6}
            value={confirmPin}
            onChange={(e) => setConfirmPin(e.target.value.replace(/\D/g, ''))}
            disabled={busy}
          />
        </label>
        <button type="submit" disabled={busy}>
          {busy ? t('tvPin.saving') : configured ? t('tvPin.changeButton') : t('tvPin.setButton')}
        </button>
      </form>
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
