import { useCallback, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import {
  ApiError,
  completePartyCrewPairing,
  getPartyCrewChallenge,
  listPartyCrewPairingDevices,
  resendPartyCrewCode,
  revokePartyCrewPairingDevice,
  startPartyCrewPairing,
  verifyPartyCrewCode,
  type PartyCrewChallengeStarted,
  type PartyCrewDevice,
  type PartyCrewVerifyResult,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { crewRoleLabelKey } from '../party/crew/crewModel';
import './PartyCrew.css';

// BECOMING A DEVICE — the link, the code, and the two-device limit.
//
// THE TOKEN IS IN THE FRAGMENT, AND LEAVES IMMEDIATELY. A fragment is never
// sent to a server, never written to an access log and never put in a Referer,
// which is why the invite link puts it there rather than in a query string.
// This page reads it once, rewrites the address bar before anything else
// happens, and posts it in a body. It is never stored: no localStorage, no
// sessionStorage, no state that outlives the call.
//
// THREE REAL URLS, AND THEY SURVIVE A REFRESH. The challenge lives in an
// HttpOnly cookie, so /verify and /devices re-read what they are about from the
// server rather than depending on what the previous screen happened to hold in
// memory. Somebody who reloads the code screen sees the code screen.
//
// THE DEVICE LIMIT IS NOT AN ERROR. A correct code with two devices already
// paired lands on a screen that lists those two and lets one go — and then
// finishes WITHOUT a second code, because the challenge is already verified.
// Sending another code would be charging the person for the product's
// inability to count to two.

type Phase = 'invite' | 'verify' | 'devices';

const PHASE: Record<string, Phase> = {
  '/party/crew/invite': 'invite',
  '/party/crew/verify': 'verify',
  '/party/crew/devices': 'devices',
};

export function PartyCrewPairingPage() {
  const location = useLocation();
  const phase = PHASE[location.pathname] ?? 'invite';
  const { t } = useI18n();

  return (
    <main className="crew-pair" data-testid="party-crew-pairing" data-phase={phase}>
      <div className="crew-pair-card">
        <p className="crew-pair-eyebrow">{t('crew.pair.eyebrow')}</p>
        {phase === 'invite' && <InvitePhase />}
        {phase === 'verify' && <VerifyPhase />}
        {phase === 'devices' && <DevicesPhase />}
      </div>
    </main>
  );
}

/* ── 1. The link ──────────────────────────────────────────────────────────── */

function InvitePhase() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [state, setState] = useState<'working' | 'unusable'>('working');
  // React 18 mounts twice in StrictMode and the token is single-use in the
  // fragment: read it once, and only once.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;

    const token = readTokenFromFragment();
    if (token === null) { setState('unusable'); return; }

    void (async () => {
      try {
        await startPartyCrewPairing(token);
        navigate('/party/crew/verify', { replace: true });
      } catch {
        setState('unusable');
      }
    })();
  }, [navigate]);

  if (state === 'working') {
    return <p className="crew-pair-lede" role="status">{t('crew.pair.opening')}</p>;
  }

  return (
    <>
      <h1 className="crew-pair-title">{t('crew.pair.unusable.title')}</h1>
      <p className="crew-pair-lede">{t('crew.pair.unusable.body')}</p>
    </>
  );
}

/**
 * The token, taken out of the address bar before anything else.
 *
 * `replaceState` rather than a navigation, so there is no history entry holding
 * it and the back button cannot restore it. Returns null when the link carried
 * nothing usable, which is the same answer a wrong link gets.
 */
function readTokenFromFragment(): string | null {
  const raw = window.location.hash.startsWith('#') ? window.location.hash.slice(1) : '';
  const token = new URLSearchParams(raw).get('token');
  if (raw !== '') {
    window.history.replaceState(
      null, '', `${window.location.pathname}${window.location.search}`);
  }
  return token && token.length > 0 ? token : null;
}

/* ── 2. The code ──────────────────────────────────────────────────────────── */

function VerifyPhase() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [load, setLoad] = useState<'loading' | 'ready' | 'unusable'>('loading');
  const [challenge, setChallenge] = useState<PartyCrewChallengeStarted | null>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  const [resent, setResent] = useState(false);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const view = await getPartyCrewChallenge();
        if (!live) return;
        setChallenge(view);
        setLoad('ready');
      } catch {
        if (live) setLoad('unusable');
      }
    })();
    return () => { live = false; };
  }, []);

  async function submit() {
    if (code.length !== 6 || busy) return;
    setBusy(true); setNotice(null);
    try {
      settle(await verifyPartyCrewCode(code), navigate);
    } catch (err) {
      setNotice(pairingFailure(err));
      // A wrong code is a wrong code: clear it rather than leave the person
      // editing six digits they already know are not right.
      if (err instanceof ApiError && err.status === 400) setCode('');
    } finally { setBusy(false); }
  }

  async function resend() {
    setBusy(true); setNotice(null); setResent(false);
    try {
      await resendPartyCrewCode();
      setResent(true);
    } catch (err) {
      setNotice(pairingFailure(err));
    } finally { setBusy(false); }
  }

  if (load === 'loading') {
    return <p className="crew-pair-lede" role="status">{t('crew.pair.opening')}</p>;
  }

  if (load === 'unusable' || challenge === null) {
    return (
      <>
        <h1 className="crew-pair-title">{t('crew.pair.expired.title')}</h1>
        <p className="crew-pair-lede">{t('crew.pair.expired.body')}</p>
      </>
    );
  }

  return (
    <>
      <h1 className="crew-pair-title">{challenge.partyTitle}</h1>
      <p className="crew-pair-lede">
        {t('crew.pair.role', { role: t(crewRoleLabelKey(challenge.roleKey)) })}
      </p>

      <form
        className="crew-pair-form"
        onSubmit={(event) => { event.preventDefault(); void submit(); }}
      >
        <label className="crew-pair-field">
          <span className="crew-pair-label">{t('crew.pair.code.label')}</span>
          <input
            className="crew-pair-code"
            // A numeric keypad on a phone, and a browser that offers the code
            // straight from the message on iOS and Android.
            inputMode="numeric"
            autoComplete="one-time-code"
            pattern="[0-9]*"
            maxLength={6}
            value={code}
            autoFocus
            data-testid="crew-code"
            onChange={(event) => setCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
          />
          <span className="crew-pair-help">
            {t('crew.pair.code.sentTo', { email: challenge.maskedEmail })}
          </span>
        </label>

        <button
          type="submit"
          className="crew-pair-primary"
          disabled={code.length !== 6 || busy}
          data-testid="crew-code-submit"
        >
          {t('crew.pair.code.submit')}
        </button>
      </form>

      <button
        type="button"
        className="crew-pair-quiet"
        disabled={busy}
        data-testid="crew-code-resend"
        onClick={() => void resend()}
      >
        {t('crew.pair.code.resend')}
      </button>

      <p aria-live="polite" className="crew-pair-notice">
        {resent && t('crew.pair.code.resent')}
        {notice && t(notice)}
      </p>
    </>
  );
}

/* ── 3. The limit ─────────────────────────────────────────────────────────── */

function DevicesPhase() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [load, setLoad] = useState<'loading' | 'ready' | 'unusable'>('loading');
  const [devices, setDevices] = useState<PartyCrewDevice[]>([]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<MessageKey | null>(null);

  const reload = useCallback(async () => {
    try {
      setDevices(await listPartyCrewPairingDevices());
      setLoad('ready');
    } catch {
      setLoad('unusable');
    }
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  async function drop(grantId: string) {
    setBusy(true); setNotice(null);
    try {
      await revokePartyCrewPairingDevice(grantId);
      // The slot is free. Finishing now needs no second code: the challenge
      // was verified before the limit was ever reached.
      settle(await completePartyCrewPairing(), navigate);
    } catch (err) {
      setNotice(pairingFailure(err));
      await reload();
    } finally { setBusy(false); }
  }

  if (load === 'loading') {
    return <p className="crew-pair-lede" role="status">{t('crew.pair.opening')}</p>;
  }

  if (load === 'unusable') {
    return (
      <>
        <h1 className="crew-pair-title">{t('crew.pair.expired.title')}</h1>
        <p className="crew-pair-lede">{t('crew.pair.expired.body')}</p>
      </>
    );
  }

  return (
    <>
      <h1 className="crew-pair-title">{t('crew.pair.limit.title')}</h1>
      <p className="crew-pair-lede">{t('crew.pair.limit.body')}</p>

      <ul className="crew-pair-devices" data-testid="crew-limit-devices">
        {devices.map((device) => (
          <li key={device.grantId} className="crew-pair-device">
            <span className="crew-pair-device-text">
              <span className="crew-pair-device-name">{device.label}</span>
              <span className="crew-pair-device-when">
                {t('crew.pair.limit.lastUsed', { when: formatWhen(device.lastUsedAt ?? device.pairedAt) })}
              </span>
            </span>
            <button
              type="button"
              className="crew-pair-drop"
              disabled={busy}
              data-testid={`crew-limit-drop-${device.grantId}`}
              onClick={() => void drop(device.grantId)}
            >
              {t('crew.pair.limit.drop')}
            </button>
          </li>
        ))}
      </ul>

      <p aria-live="polite" className="crew-pair-notice">{notice && t(notice)}</p>
    </>
  );
}

/* ── Shared ───────────────────────────────────────────────────────────────── */

/** Where a pairing attempt's answer sends the browser next. */
function settle(result: PartyCrewVerifyResult, navigate: (to: string, o?: object) => void) {
  if (result.outcome === 'Paired' && result.partyId) {
    navigate(`/party/crew/${result.partyId}`, { replace: true });
    return;
  }
  navigate('/party/crew/devices', { replace: true });
}

/** A refusal, as one sentence. The server says as little as it can; so does this. */
function pairingFailure(err: unknown): MessageKey {
  if (!(err instanceof ApiError)) return 'crew.pair.failed';
  if (err.status === 429) return 'crew.pair.tooMany';
  const code = (err.body as { error?: string } | null)?.error;
  if (code === 'invalid_code') return 'crew.pair.wrongCode';
  if (code === 'mail_unavailable') return 'crew.pair.noMail';
  if (code === 'device_limit') return 'crew.pair.limit.body';
  return 'crew.pair.failed';
}

function formatWhen(iso: string): string {
  const parsed = new Date(iso);
  return Number.isNaN(parsed.getTime())
    ? iso
    : parsed.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}
