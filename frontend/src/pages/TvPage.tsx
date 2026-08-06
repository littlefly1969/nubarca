import { useCallback, useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import {
  ApiError,
  getTvPairingStatus,
  getTvSession,
  heartbeatTvSession,
  startTvPairing,
  type TvPairingStarted,
} from '@nubarca/api-client';
import { TvPairedExperience } from './TvPairedExperience';
import { useI18n, toLanguage } from '../i18n';

type TvState =
  | { kind: 'loading' }
  | { kind: 'pairing'; pairing: TvPairingStarted; qrSvg: string }
  | { kind: 'paired'; expiresAt: string }
  | { kind: 'expired' }
  | { kind: 'revoked' }
  // Paired session whose owner has no Personal Area PIN — legacy/corrupted
  // data the atomic pairing flow can no longer produce. Recovery: pair again
  // (which forcibly creates the PIN).
  | { kind: 'incomplete' }
  | { kind: 'error' };

export function TvPage() {
  const { t, setLanguage } = useI18n();
  const [state, setState] = useState<TvState>({ kind: 'loading' });
  const mounted = useRef(true);

  // Adopt the paired owner's UI language so the 10-foot TV UI localizes in the
  // owner's language. Never persisted locally (the TV is a shared surface).
  const adoptOwnerLanguage = useCallback((language: string) => {
    const lang = toLanguage(language);
    if (lang) setLanguage(lang, { persistLocal: false });
  }, [setLanguage]);

  const beginPairing = useCallback(async (signal?: AbortSignal) => {
    setState({ kind: 'loading' });
    try {
      const pairing = await startTvPairing(signal);
      const qrSvg = await QRCode.toString(pairing.approvalUrl, {
        type: 'svg',
        margin: 1,
        width: 320,
        color: { dark: '#111111', light: '#ffffff' },
      });
      if (mounted.current && !signal?.aborted) setState({ kind: 'pairing', pairing, qrSvg });
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError') && mounted.current) {
        setState({ kind: 'error' });
      }
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    const controller = new AbortController();
    getTvSession(controller.signal)
      .then((session) => {
        adoptOwnerLanguage(session.language);
        setState({ kind: 'paired', expiresAt: session.expiresAt });
      })
      .catch((error: unknown) => {
        if (error instanceof ApiError && error.status === 401) {
          void beginPairing(controller.signal);
        } else if (!(error instanceof DOMException && error.name === 'AbortError')) {
          setState({ kind: 'error' });
        }
      });
    return () => {
      mounted.current = false;
      controller.abort();
    };
  }, [beginPairing, adoptOwnerLanguage]);

  useEffect(() => {
    if (state.kind !== 'pairing') return;
    const pairing = state.pairing;
    let stopped = false;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const status = await getTvPairingStatus(pairing.publicCode, pairing.pairingSecret);
        if (stopped) return;
        if (status.status === 'paired') {
          const session = await getTvSession();
          if (!stopped) {
            adoptOwnerLanguage(session.language);
            setState({ kind: 'paired', expiresAt: session.expiresAt });
          }
          return;
        }
        if (status.status === 'expired') {
          setState({ kind: 'expired' });
          return;
        }
      } catch {
        // A transient polling failure is retried; the pairing deadline remains
        // authoritative on the server.
      }
      if (!stopped) timer = window.setTimeout(poll, 2000);
    };
    timer = window.setTimeout(poll, 500);
    return () => {
      stopped = true;
      if (timer !== undefined) window.clearTimeout(timer);
    };
  }, [state, adoptOwnerLanguage]);

  useEffect(() => {
    if (state.kind !== 'paired') return;
    // Heartbeat keeps the session fresh; a 401 means it was revoked (by the
    // owner) or expired — surface a clear revoked state instead of silently
    // continuing to show a dead session.
    const beat = () =>
      void heartbeatTvSession().catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401 && mounted.current) {
          setState({ kind: 'revoked' });
        }
      });
    const timer = window.setInterval(beat, 60_000);
    return () => window.clearInterval(timer);
  }, [state.kind]);

  // Once paired, every start lands on the Party / Personal-area mode selector
  // (TvPairedExperience owns that state machine; the unlock grant lives only in
  // its memory). If the session is revoked in any mode (owner action), it
  // reports it so we drop to the revoked state on the next API call.
  if (state.kind === 'paired') {
    return (
      <main className="tv-page tv-page-browse">
        <h1 className="tv-browse-title">{t('tv.title')}</h1>
        <TvPairedExperience
          onSessionInvalid={() => setState({ kind: 'revoked' })}
          onAssociationIncomplete={() => setState({ kind: 'incomplete' })}
        />
      </main>
    );
  }

  return (
    <main className="tv-page">
      <div className="tv-card">
        <h1>{t('tv.title')}</h1>
        {state.kind === 'loading' && <p>{t('tv.preparing')}</p>}
        {state.kind === 'pairing' && (
          <>
            <p>{t('tv.scanInstructions')}</p>
            <div
              className="tv-qr"
              aria-label={t('tv.qrLabel')}
              dangerouslySetInnerHTML={{ __html: state.qrSvg }}
            />
            <p className="tv-code-label">{t('tv.pairingCode')}</p>
            <div className="tv-code" aria-label={t('tv.pairingCode')}>{state.pairing.publicCode}</div>
            <p className="muted">{t('tv.codeExpiresSoon')}</p>
          </>
        )}
        {state.kind === 'revoked' && (
          <>
            <p role="alert">{t('tv.sessionRevoked')}</p>
            <button type="button" onClick={() => void beginPairing()}>{t('tv.pairAgain')}</button>
          </>
        )}
        {state.kind === 'incomplete' && (
          <>
            <p role="alert" data-testid="tv-incomplete">{t('tv.pairingIncomplete')}</p>
            <button type="button" onClick={() => void beginPairing()}>{t('tv.pairAgain')}</button>
          </>
        )}
        {(state.kind === 'expired' || state.kind === 'error') && (
          <>
            <p role="alert">
              {state.kind === 'expired' ? t('tv.codeExpired') : t('tv.pairingUnavailable')}
            </p>
            <button type="button" onClick={() => void beginPairing()}>{t('common.tryAgain')}</button>
          </>
        )}
      </div>
    </main>
  );
}
