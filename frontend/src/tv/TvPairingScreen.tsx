import { useCallback, useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import {
  getTvPairingStatus,
  getTvSession,
  startTvPairing,
  type TvPairingStarted,
  type TvSessionStatus,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { tvLog } from './diagnostics';

// PAIRING — the one door into being a NubArca display, for a browser exactly
// as for the app: a short code and a QR, approved by the owner on their phone.
// There is no second, browser-only pairing and no second identity: what comes
// out of it is the same TV session, in an HTTP-only cookie scoped to /api/tv,
// that a Fire TV holds.
//
// It is also where a display lands when its session stops being valid — the
// owner revoked it, it expired, or its association is incomplete — and it says
// which, next to a fresh code, so a screen nobody is standing next to can be
// paired again from a phone without anyone touching it.

type State =
  | { kind: 'starting' }
  | { kind: 'pairing'; pairing: TvPairingStarted; qrSvg: string }
  | { kind: 'expired' }
  | { kind: 'error' };

export type PairingNotice = 'revoked' | 'incomplete' | null;

interface Props {
  readonly notice: PairingNotice;
  /** The approved session, as its first read returned it. */
  readonly onPaired: (session: TvSessionStatus) => void;
}

const STATUS_POLL_MS = 2_000;

export function TvPairingScreen({ notice, onPaired }: Props) {
  const { t } = useI18n();
  const [state, setState] = useState<State>({ kind: 'starting' });
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const begin = useCallback(async (signal?: AbortSignal) => {
    setState({ kind: 'starting' });
    try {
      const pairing = await startTvPairing(signal);
      const qrSvg = await QRCode.toString(pairing.approvalUrl, {
        type: 'svg', margin: 1, width: 320, color: { dark: '#111111', light: '#ffffff' },
      });
      if (mounted.current && !signal?.aborted) {
        tvLog('tv.pairing.started');
        setState({ kind: 'pairing', pairing, qrSvg });
      }
    } catch (error) {
      if (error instanceof DOMException && error.name === 'AbortError') return;
      if (mounted.current) setState({ kind: 'error' });
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void begin(controller.signal);
    return () => controller.abort();
  }, [begin]);

  const onPairedRef = useRef(onPaired);
  onPairedRef.current = onPaired;

  useEffect(() => {
    if (state.kind !== 'pairing') return;
    const { pairing } = state;
    let stopped = false;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const status = await getTvPairingStatus(pairing.publicCode, pairing.pairingSecret);
        if (stopped) return;
        if (status.status === 'paired') {
          const session = await getTvSession();
          if (!stopped) {
            tvLog('tv.pairing.completed');
            onPairedRef.current(session);
          }
          return;
        }
        if (status.status === 'expired') {
          setState({ kind: 'expired' });
          return;
        }
      } catch {
        // A transient failure is retried; the pairing deadline stays authoritative on the server.
      }
      if (!stopped) timer = window.setTimeout(poll, STATUS_POLL_MS);
    };
    timer = window.setTimeout(poll, 500);
    return () => {
      stopped = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [state]);

  return (
    <main className="tv-page">
      <div className="tv-card">
        <h1>{t('tv.title')}</h1>
        {notice === 'revoked' && <p role="alert" data-testid="tv-revoked">{t('tv.sessionRevoked')}</p>}
        {notice === 'incomplete' && (
          <p role="alert" data-testid="tv-incomplete">{t('tv.pairingIncomplete')}</p>
        )}
        {state.kind === 'starting' && <p>{t('tv.preparing')}</p>}
        {state.kind === 'pairing' && (
          <>
            <p>{t('tv.scanInstructions')}</p>
            <div
              className="tv-qr"
              aria-label={t('tv.qrLabel')}
              dangerouslySetInnerHTML={{ __html: state.qrSvg }}
            />
            <p className="tv-code-label">{t('tv.pairingCode')}</p>
            <div className="tv-code" aria-label={t('tv.pairingCode')} data-testid="tv-pairing-code">
              {state.pairing.publicCode}
            </div>
            <p className="muted">{t('tv.codeExpiresSoon')}</p>
          </>
        )}
        {(state.kind === 'expired' || state.kind === 'error') && (
          <>
            <p role="alert">{state.kind === 'expired' ? t('tv.codeExpired') : t('tv.pairingUnavailable')}</p>
            <button type="button" onClick={() => void begin()}>{t('common.tryAgain')}</button>
          </>
        )}
      </div>
    </main>
  );
}
