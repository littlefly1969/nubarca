import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, mintTvPartyDisplayGrant } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { PartyDisplayStage } from '../../pages/PartyDisplayStagePage';
import { classifyMintFailure, mintRetryDelayMs, renewDelayMs } from '../semantics/partyDisplayGrant';
import { useLatest } from '../platform/hooks';
import { tvLog } from '../diagnostics';
import { TvPartySurface } from './TvPartyOverlays';

// The party GAME on a browser display — mounted only while the SERVER's
// presentation for the assigned party is `game`.
//
// The browser's counterpart of the app's PartyDisplayScreen, without the
// WebView: the canonical stage renders in this very document, from a display
// grant this display minted from its OWN session. Nothing here can name a
// party: the grant endpoint takes no body, and the server answers for whatever
// this display is assigned to. No party token, no guest cookie, no participant.
//
// The grant's lifecycle is the app's (semantics/partyDisplayGrant): minted when
// the game takes the screen, replaced before it lapses, minted again when the
// stage reports a refusal, retried on a capped backoff while the server keeps
// saying `game`. A 404 fails closed and asks the control plane what the screen
// is for now; a 401 is the session, and goes to pairing.
//
// The shell keys this component by the assignment key, so Party A → Party B
// unmounts it: A's grant and A's frame leave in the same commit that accepts B.

interface Props {
  readonly albumName: string | null;
  readonly onSessionInvalid: () => void;
  /** Ask the control plane for an immediate re-read: the capability says the party moved. */
  readonly onRequestAssignment: () => void;
  readonly refreshKey: unknown;
}

type GrantState =
  | { kind: 'minting' }
  | { kind: 'ready'; token: string; generation: number }
  | { kind: 'waiting'; failure: 'not-assigned' | 'transient' };

export function TvAssignedGame({ albumName, onSessionInvalid, onRequestAssignment, refreshKey }: Props) {
  const { t } = useI18n();
  const [grant, setGrant] = useState<GrantState>({ kind: 'minting' });
  const [mintRequest, setMintRequest] = useState(0);
  const [stageReady, setStageReady] = useState(false);
  const [refused, setRefused] = useState(false);
  const callbacks = useLatest({ onSessionInvalid, onRequestAssignment });
  const failures = useRef(0);
  const minting = useRef(false);
  const retryPending = useRef(false);
  const authRetry = useRef<ReturnType<typeof setTimeout> | null>(null);
  const authFailures = useRef(0);
  const generation = useRef(0);

  useEffect(() => {
    let cancelled = false;
    let next: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    minting.current = true;
    retryPending.current = false;
    mintTvPartyDisplayGrant(controller.signal)
      .then((minted) => {
        if (cancelled) return;
        minting.current = false;
        failures.current = 0;
        generation.current += 1;
        // A new grant is a new stage, from nothing, behind the cover.
        setStageReady(false);
        setRefused(false);
        setGrant({ kind: 'ready', token: minted.grant, generation: generation.current });
        next = setTimeout(() => setMintRequest((n) => n + 1), renewDelayMs(minted, Date.now()));
      })
      .catch((error: unknown) => {
        if (cancelled || controller.signal.aborted) return;
        minting.current = false;
        const failure = classifyMintFailure(error instanceof ApiError ? error.status : null);
        tvLog('tv.game.grant.failed', { failure });
        if (failure === 'session-invalid') {
          callbacks.current.onSessionInvalid();
          return;
        }
        if (failure === 'not-assigned') callbacks.current.onRequestAssignment();
        // A RENEWAL that did not get through leaves a working stage alone.
        setGrant((current) => (current.kind === 'ready' && failure === 'transient'
          ? current
          : { kind: 'waiting', failure }));
        retryPending.current = true;
        next = setTimeout(() => setMintRequest((n) => n + 1), mintRetryDelayMs(failure, failures.current++));
      });
    return () => {
      cancelled = true;
      minting.current = false;
      controller.abort();
      if (next) clearTimeout(next);
    };
  }, [mintRequest, callbacks]);

  // A resume: a grant that lapsed while the machine slept is replaced now.
  const firstRefresh = useRef(true);
  useEffect(() => {
    if (firstRefresh.current) {
      firstRefresh.current = false;
      return;
    }
    if (!minting.current) setMintRequest((n) => n + 1);
  }, [refreshKey]);

  useEffect(() => () => {
    if (authRetry.current) clearTimeout(authRetry.current);
  }, []);

  // The stage says its grant was refused. The cover goes up at once, the
  // control plane is asked (the game has probably handed the screen back) and
  // a new grant is minted, backing off if the refusals keep coming.
  const onAuthFailed = useCallback(() => {
    setRefused(true);
    if (minting.current || retryPending.current || authRetry.current !== null) return;
    callbacks.current.onRequestAssignment();
    authRetry.current = setTimeout(() => {
      authRetry.current = null;
      setMintRequest((n) => n + 1);
    }, mintRetryDelayMs('transient', authFailures.current++));
  }, [callbacks]);

  const onReady = useCallback(() => {
    authFailures.current = 0;
    setStageReady(true);
  }, []);

  const visible = grant.kind === 'ready' && stageReady && !refused;
  const coverMessage = refused ? null : grant.kind === 'waiting'
    ? t(grant.failure === 'not-assigned' ? 'tv.partyUnavailable' : 'tv.partyReconnecting')
    : t('tv.partyStarting');

  return (
    <div className="tvd-wall tvd-game" data-testid="tv-party-game" data-visible={visible}>
      {grant.kind === 'ready' && (
        <PartyDisplayStage
          key={grant.generation}
          grant={grant.token}
          onReady={onReady}
          onAuthFailed={onAuthFailed}
          refreshKey={refreshKey}
        />
      )}
      {!visible && (
        <TvPartySurface
          overlay
          albumName={albumName}
          message={coverMessage}
          busy={grant.kind !== 'waiting' || grant.failure === 'transient'}
          testId="tv-party-game-cover"
        />
      )}
    </div>
  );
}
