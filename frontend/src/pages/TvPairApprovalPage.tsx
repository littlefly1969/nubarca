import { useEffect, useState } from 'react';
import {
  ApiError,
  approveTvPairing,
  getTvPersonalPinStatus,
} from '@nubarca/api-client';
import { useLocation, useNavigate, useSearchParams } from 'react-router';
import { useI18n } from '../i18n';

const PIN_PATTERN = /^\d{6}$/;

// Owner-side pairing approval — ONE atomic flow. For an owner without a
// Personal Area PIN the create+confirm fields are part of the SAME form as the
// approval: the server commits the PIN and the approval together, so an
// abandoned or failed PIN step leaves the pairing pending (never a paired TV
// without a PIN). An owner with a PIN approves with one tap and is never asked
// for a PIN here (an existing PIN is never replaced from this flow).
export function TvPairApprovalPage() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const [credentials] = useState(() => ({
    code: (params.get('code') ?? '').trim().toUpperCase(),
    secret: new URLSearchParams(location.hash.slice(1)).get('secret') ?? '',
  }));
  const { code, secret } = credentials;
  const valid = /^[23456789A-HJ-NP-Z]{8}$/.test(code) && secret.length >= 32;
  const [state, setState] = useState<
    'loading' | 'ready' | 'submitting' | 'approved' | 'statusError'
  >('loading');
  const [needsPin, setNeedsPin] = useState(false);
  const [pin, setPin] = useState('');
  const [confirmPin, setConfirmPin] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [reloadNonce, setReloadNonce] = useState(0);

  // Once authentication has returned to this route, retain the one-time secret
  // only in component memory and remove it from browser history/address bar.
  useEffect(() => {
    if (secret) navigate(`/tv/pair?code=${encodeURIComponent(code)}`, { replace: true });
  }, [code, navigate, secret]);

  // The form depends on whether the owner already has a PIN, so resolve that
  // BEFORE showing the approve action (never an approve that half-succeeds).
  useEffect(() => {
    if (!valid) return;
    let cancelled = false;
    setState('loading');
    getTvPersonalPinStatus()
      .then((status) => {
        if (cancelled) return;
        setNeedsPin(!status.configured);
        setState('ready');
      })
      .catch(() => {
        if (!cancelled) setState('statusError');
      });
    return () => {
      cancelled = true;
    };
  }, [valid, reloadNonce]);

  async function approve() {
    if (needsPin) {
      if (!PIN_PATTERN.test(pin)) {
        setError(t('tvPair.pinInvalid'));
        return;
      }
      if (pin !== confirmPin) {
        setError(t('tvPair.pinMismatch'));
        return;
      }
    }
    setState('submitting');
    setError(null);
    try {
      await approveTvPairing(
        code, secret,
        needsPin ? pin : undefined,
        needsPin ? confirmPin : undefined,
      );
      setState('approved');
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as { error?: string } | null) : null;
      if (body?.error === 'invalid_pin') setError(t('tvPair.pinInvalid'));
      else if (body?.error === 'pin_mismatch') setError(t('tvPair.pinMismatch'));
      else if (body?.error === 'pin_required') {
        // Stale status (PIN removed meanwhile): show the PIN fields.
        setNeedsPin(true);
        setError(t('tvPair.pinInvalid'));
      } else setError(t('tvPair.approveError'));
      setState('ready');
    } finally {
      // The PIN never outlives the flow.
      setPin('');
      setConfirmPin('');
    }
  }

  return (
    <main className="tv-page tv-approval-page">
      <div className="tv-card">
        <h1>{t('tvPair.title')}</h1>
        {!valid ? (
          <p role="alert">{t('tvPair.invalidLink')}</p>
        ) : state === 'approved' ? (
          <>
            <div className="tv-paired-mark" aria-hidden="true">✓</div>
            <h2>{t('tvPair.approvedTitle')}</h2>
            <p>{t('tvPair.approvedBody')}</p>
          </>
        ) : state === 'loading' ? (
          <p role="status">{t('tvPair.preparing')}</p>
        ) : state === 'statusError' ? (
          <>
            <p role="alert">{t('tvPair.statusError')}</p>
            <button type="button" onClick={() => setReloadNonce((n) => n + 1)}>
              {t('common.tryAgain')}
            </button>
          </>
        ) : (
          <form
            className="tv-pin-form"
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              void approve();
            }}
          >
            <p>{t('tvPair.approvePrompt')}</p>
            <div className="tv-code" aria-label={t('tv.pairingCode')}>{code}</div>
            <p className="muted">{t('tvPair.onlyApproveSeen')}</p>
            {needsPin && (
              <>
                <p>{t('tvPair.pinIntro')}</p>
                <label>
                  {t('tvPair.pinLabel')}
                  <input
                    type="password"
                    inputMode="numeric"
                    autoComplete="off"
                    pattern="\d{6}"
                    maxLength={6}
                    value={pin}
                    onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
                    disabled={state === 'submitting'}
                  />
                </label>
                <label>
                  {t('tvPair.pinConfirmLabel')}
                  <input
                    type="password"
                    inputMode="numeric"
                    autoComplete="off"
                    pattern="\d{6}"
                    maxLength={6}
                    value={confirmPin}
                    onChange={(e) => setConfirmPin(e.target.value.replace(/\D/g, ''))}
                    disabled={state === 'submitting'}
                  />
                </label>
              </>
            )}
            <button type="submit" disabled={state === 'submitting'}>
              {state === 'submitting'
                ? t('tvPair.approving')
                : needsPin ? t('tvPair.approveWithPinButton') : t('tvPair.approveButton')}
            </button>
            {error && <p role="alert">{error}</p>}
          </form>
        )}
      </div>
    </main>
  );
}
