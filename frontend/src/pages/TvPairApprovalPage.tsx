import { useEffect, useState } from 'react';
import {
  ApiError,
  approveTvPairing,
  getTvPairedDevice,
  getTvPersonalPinStatus,
  isCompleteTvCode,
  listTvAssignableParties,
  setTvDeviceAssignment,
  type TvAssignableParty,
} from '@nubarca/api-client';
import { useLocation, useNavigate, useSearchParams } from 'react-router';
import { useI18n } from '../i18n';
import { TvCodeInput } from '../tv/TvCodeInput';

// How long to wait for the television to claim the pairing before giving up on
// asking what it is for. It polls its own status every couple of seconds, so
// this is generous; the TV panel is the answer for anything slower.
const WAIT_FOR_DEVICE_MS = 120_000;

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
//
// After the approval there is a SECOND question — "how do you want to use this
// TV?" — and it is deliberately not part of the approval. The approval is about
// identity and commits a credential; the answer to this one is ordinary mutable
// state on the paired device, changeable later from the TV panel without any of
// this. The page waits for the television to claim the pairing (the session does
// not exist until it polls), then sets the assignment on it. Skipping, closing
// the tab or losing the network all leave the television GENERAL, which is the
// safe default and exactly what it would have been before this existed.
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
  // The device this pairing produced, once the television has claimed it.
  const [sessionId, setSessionId] = useState<string | null>(null);
  const [parties, setParties] = useState<TvAssignableParty[]>([]);
  const [assigning, setAssigning] = useState(false);
  const [assigned, setAssigned] = useState<string | null>(null);
  const [assignError, setAssignError] = useState(false);
  const [waitedOut, setWaitedOut] = useState(false);
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

  // The session is minted when the TELEVISION polls, not when the owner
  // approves, so this waits for it rather than assuming it.
  //
  // Bounded three ways: it is only armed after a successful approval, it stops
  // the moment the device appears, and it gives up after WAIT_FOR_DEVICE_MS so
  // an abandoned tab does not poll for the rest of the afternoon. Giving up
  // costs nothing — the television is GENERAL, which is what it would have been
  // anyway, and the TV panel can point it anywhere later.
  useEffect(() => {
    if (state !== 'approved' || sessionId !== null || waitedOut) return;
    let cancelled = false;
    const deadline = Date.now() + WAIT_FOR_DEVICE_MS;
    const read = () => {
      if (Date.now() > deadline) { setWaitedOut(true); return; }
      // `secret` comes from the one-time useState initializer, so it is stable
      // for the life of the page even after the fragment is stripped from the
      // address bar.
      getTvPairedDevice(code, secret)
        .then((device) => { if (!cancelled && device.sessionId) setSessionId(device.sessionId); })
        .catch(() => { /* the television has not claimed it yet, or cannot be asked */ });
    };
    read();
    const timer = setInterval(read, 2_000);
    return () => { cancelled = true; clearInterval(timer); };
  }, [code, secret, state, sessionId, waitedOut]);

  // The parties this television could be pointed at. An empty list is a correct
  // answer — an owner with no live party simply gets the general choice.
  useEffect(() => {
    if (state !== 'approved') return;
    const controller = new AbortController();
    listTvAssignableParties(controller.signal)
      .then(setParties)
      .catch(() => { /* offering no parties is a correct answer here */ });
    return () => controller.abort();
  }, [state]);

  async function assign(albumId: string | null) {
    if (!sessionId || assigning) return;
    setAssigning(true);
    setAssignError(false);
    try {
      const assignment = await setTvDeviceAssignment(sessionId, albumId);
      setAssigned(assignment.kind === 'party'
        ? (assignment.albumName ?? t('tvPair.usePartyFallback'))
        : t('tvPair.useGeneral'));
    } catch {
      setAssignError(true);
    } finally {
      setAssigning(false);
    }
  }

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

            {assigned !== null ? (
              <p role="status" data-testid="tv-pair-assigned">
                {t('tvPair.useSaved', { name: assigned })}
              </p>
            ) : sessionId === null ? (
              <p className="muted" role="status" data-testid="tv-pair-waiting">
                {t(waitedOut ? 'tvPair.useLater' : 'tvPair.useWaiting')}
              </p>
            ) : (
              <div className="tv-pair-use" data-testid="tv-pair-use">
                <h3>{t('tvPair.useTitle')}</h3>
                <p className="muted">{t('tvPair.useIntro')}</p>
                <div className="tv-pair-use-options">
                  <button
                    type="button"
                    disabled={assigning}
                    data-testid="tv-pair-use-general"
                    onClick={() => void assign(null)}
                  >
                    {t('tvPair.useGeneral')}
                  </button>
                  {parties.map((party) => (
                    <button
                      key={party.albumId}
                      type="button"
                      disabled={assigning}
                      onClick={() => void assign(party.albumId)}
                    >
                      {t('tvPair.useParty', { name: party.albumName })}
                    </button>
                  ))}
                </div>
                {parties.length === 0 && (
                  <p className="muted">{t('tvPair.useNoParties')}</p>
                )}
                {assignError && <p role="alert">{t('tvPair.useError')}</p>}
              </div>
            )}
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
