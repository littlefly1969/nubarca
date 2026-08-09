import { useEffect, useState } from 'react';
import {
  ApiError,
  approveTvPairing,
  getTvPersonalPinStatus,
  isCompleteTvCode,
} from '@nubarca/api-client';
import { useLocation, useNavigate, useSearchParams } from 'react-router';
import { useI18n } from '../i18n';
import { TvCodeInput } from '../tv/TvCodeInput';

// Owner-side pairing approval — ONE atomic flow. For an owner without a
// Personal Area credential the create+confirm fields are part of the SAME form
// as the approval: the server commits the credential and the approval together,
// so an abandoned or failed code step leaves the pairing pending (never a paired
// TV without a credential). An owner who already has one approves with one tap
// and is never asked for a code here (an existing credential is never replaced
// from this flow — the account page owns that).
//
// The credential is the DIRECTIONAL remote code. It is shown while it is being
// chosen because this is the owner's own authenticated device; the television
// itself never renders a symbol. See TvCodeInput.
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
  const [needsCode, setNeedsCode] = useState(false);
  const [code_, setCode] = useState('');
  const [confirmCode, setConfirmCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [reloadNonce, setReloadNonce] = useState(0);

  // Once authentication has returned to this route, retain the one-time secret
  // only in component memory and remove it from browser history/address bar.
  useEffect(() => {
    if (secret) navigate(`/tv/pair?code=${encodeURIComponent(code)}`, { replace: true });
  }, [code, navigate, secret]);

  // The form depends on whether the owner already has a credential, so resolve
  // that BEFORE showing the approve action (never an approve that
  // half-succeeds). A LEGACY numeric row still counts as configured: this flow
  // never replaces an existing credential, and upgrading it is the account
  // page's job, not a pairing side effect.
  useEffect(() => {
    if (!valid) return;
    let cancelled = false;
    setState('loading');
    getTvPersonalPinStatus()
      .then((status) => {
        if (cancelled) return;
        setNeedsCode(!status.configured);
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
    if (needsCode) {
      if (!isCompleteTvCode(code_)) {
        setError(t('tvPair.codeInvalid'));
        return;
      }
      if (code_ !== confirmCode) {
        setError(t('tvPair.codeMismatch'));
        return;
      }
    }
    setState('submitting');
    setError(null);
    try {
      await approveTvPairing(
        code, secret,
        needsCode ? code_ : undefined,
        needsCode ? confirmCode : undefined,
      );
      setState('approved');
    } catch (err) {
      const body = err instanceof ApiError ? (err.body as { error?: string } | null) : null;
      if (body?.error === 'invalid_code') setError(t('tvPair.codeInvalid'));
      else if (body?.error === 'code_mismatch') setError(t('tvPair.codeMismatch'));
      else if (body?.error === 'code_required') {
        // Stale status (credential removed meanwhile): show the code fields.
        setNeedsCode(true);
        setError(t('tvPair.codeInvalid'));
      } else setError(t('tvPair.approveError'));
      setState('ready');
    } finally {
      // The code never outlives the flow.
      setCode('');
      setConfirmCode('');
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
            {needsCode && (
              <>
                <p>{t('tvPair.codeIntro')}</p>
                <TvCodeInput
                  id="tv-pair-code"
                  label={t('tvPair.codeLabel')}
                  value={code_}
                  onChange={setCode}
                  disabled={state === 'submitting'}
                />
                <TvCodeInput
                  id="tv-pair-code-confirm"
                  label={t('tvPair.codeConfirmLabel')}
                  value={confirmCode}
                  onChange={setConfirmCode}
                  disabled={state === 'submitting'}
                />
              </>
            )}
            <button type="submit" disabled={state === 'submitting'}>
              {state === 'submitting'
                ? t('tvPair.approving')
                : needsCode ? t('tvPair.approveWithCodeButton') : t('tvPair.approveButton')}
            </button>
            {error && <p role="alert">{error}</p>}
          </form>
        )}
      </div>
    </main>
  );
}
