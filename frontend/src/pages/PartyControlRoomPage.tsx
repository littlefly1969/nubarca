import { useState } from 'react';
import { Link, useParams } from 'react-router';
import QRCode from 'qrcode';
import { useEffect } from 'react';
import {
  partyGameDisplayState, type PartyGameCommand, type PartyGameSnapshot,
} from '@nubarca/api-client';
import { Modal } from '../components/Overlay';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import { usePartyGameControl } from '../party/usePartyGameControl';
import { useCountdown, formatCountdown } from '../party/useCountdown';
import { useI18n, type MessageKey } from '../i18n';
import '../party/PartyControlRoom.css';

// Conducting the party.
//
// The composer is where a host PREPARES; this is where they run the evening,
// usually standing up, holding a phone, in front of people who are waiting. Two
// things follow from that and shape everything below.
//
// THERE IS ONE PRIMARY ACTION, and the server says which. availableCommands is
// the server's own answer to "what may I do now", so an illegal command is
// ABSENT here rather than present-and-disabled — the same rule shared albums
// already apply to actions a caller may not perform. The state machine is not
// re-implemented in TypeScript; it is quoted.
//
// NOTHING IS OPTIMISTIC AND NOTHING IS LOST. Every command quotes the version on
// screen; a refusal carries the state it was measured against, so a second tab
// or a double tap ends with this screen correct rather than with a duplicate
// advance.

const COMMAND_LABEL: Record<PartyGameCommand, MessageKey> = {
  start: 'partyControl.start',
  start_challenge: 'partyControl.startChallenge',
  open_voting: 'partyControl.openVoting',
  close_voting: 'partyControl.closeVoting',
  reveal_result: 'partyControl.revealResult',
  next_challenge: 'partyControl.nextChallenge',
  skip_challenge: 'partyControl.skip',
  finish: 'partyControl.finish',
};

const REFUSAL_LABEL: Record<string, MessageKey> = {
  version_conflict: 'partyControl.refusedStale',
  illegal_transition: 'partyControl.refusedIllegal',
  no_challenges: 'partyControl.refusedEmpty',
  game_disabled: 'partyControl.refusedDisabled',
  conflict: 'partyControl.refusedGeneric',
};

const PHASE_LABEL: Record<PartyGameSnapshot['phase'], MessageKey> = {
  lobby: 'partyControl.phaseLobby',
  challenge_reveal: 'partyControl.phaseReveal',
  challenge_active: 'partyControl.phaseActive',
  voting_open: 'partyControl.phaseVoting',
  voting_closed: 'partyControl.phaseClosed',
  result: 'partyControl.phaseResult',
  finished: 'partyControl.phaseFinished',
};

export function PartyControlRoomPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const { t } = useI18n();
  const { snapshot, connection, stale, pending, refusal, run, refresh } =
    usePartyGameControl(albumId);
  const [confirmFinish, setConfirmFinish] = useState(false);
  const remaining = useCountdown(
    snapshot?.phase === 'challenge_active' ? snapshot.phaseEndsAt : null);
  const qr = useQr(snapshot?.guestUrl ?? null);

  if (connection === 'unavailable') {
    return (
      <div className="page-container">
        <BackLink albumId={albumId} />
        <p className="page-error">{t('partyControl.unavailable')}</p>
      </div>
    );
  }
  if (connection === 'loading' || !snapshot) {
    return (
      <div className="page-container" aria-busy="true">
        <BackLink albumId={albumId} />
        <p className="empty-state">{t('common.loading')}</p>
      </div>
    );
  }

  // The server named these, in order: the phase-advancing one first.
  const [primary, ...secondary] = snapshot.availableCommands;
  const display = partyGameDisplayState(snapshot.displaySeenSecondsAgo);

  const runCommand = (command: PartyGameCommand) => {
    if (command === 'finish') { setConfirmFinish(true); return; }
    void run(command);
  };

  return (
    <div className="page-container party-control" data-testid="party-control-room">
      <BackLink albumId={albumId} />
      <div className="admin-page__head">
        <div>
          <h2>{t('partyControl.title')}</h2>
          <p className="muted">{t('partyControl.subtitle')}</p>
        </div>
        <span
          className={`status-badge status-badge--${snapshot.status === 'live' ? 'on' : 'off'}`}
          data-testid="party-control-status"
        >
          {t(snapshot.status === 'live' ? 'partyControl.live'
            : snapshot.status === 'finished' ? 'partyControl.over' : 'partyControl.notStarted')}
        </span>
      </div>

      {connection === 'error' && (
        <p className="page-error" role="alert">
          {t('partyControl.offline')}{' '}
          <button type="button" className="party-control-inline" onClick={refresh}>
            {t('common.retry')}
          </button>
        </p>
      )}
      {stale && <p className="muted" role="status">{t('partyControl.reconnecting')}</p>}

      {/* The room: who is in it, and whether a screen is showing the game.
          Always visible, because a host who cannot see this is conducting an
          evening they cannot see. */}
      <div className="party-control-room" data-testid="party-control-room-state">
        <p className="party-control-stat">
          <strong>{snapshot.guestsPresent}</strong>
          <span>{t('partyControl.guests')}</span>
        </p>
        <p className="party-control-stat">
          <span
            className={`status-badge status-badge--${display === 'connected' ? 'on' : 'off'}`}
            data-testid="party-control-tv"
            data-state={display}
          >
            {t(display === 'connected' ? 'partyControl.tvConnected'
              : display === 'stale' ? 'partyControl.tvStale' : 'partyControl.tvDisconnected')}
          </span>
          {snapshot.tvUrl && (
            <a className="party-control-link" href={snapshot.tvUrl} target="_blank" rel="noreferrer">
              {t('partyControl.openTv')}
            </a>
          )}
        </p>
        <p className="party-control-stat">
          <strong>{snapshot.playedRounds}</strong>
          <span>{t('partyControl.played', { total: snapshot.totalChallenges })}</span>
        </p>
      </div>

      <section className="party-control-now">
        <div className="party-control-current">
          <p className="party-control-eyebrow">
            {t(PHASE_LABEL[snapshot.phase])}
            {snapshot.status === 'live' && snapshot.currentChallenge && (
              <> · {t('partyActivity.round', {
                round: snapshot.roundNumber, total: snapshot.totalChallenges,
              })}</>
            )}
            {remaining !== null && (
              <> · <span data-testid="party-control-timer">{formatCountdown(remaining)}</span></>
            )}
          </p>

          {snapshot.currentChallenge ? (
            <PartyChallengeCard
              mode="compact"
              testId="party-control-current"
              challenge={{
                kind: snapshot.currentChallenge.kind,
                title: snapshot.currentChallenge.title,
                body: snapshot.currentChallenge.body,
                mediaUrl: snapshot.currentChallenge.mediaUrl,
                durationSeconds: snapshot.currentChallenge.durationSeconds,
              }}
              voting={snapshot.phase === 'voting_open' ? 'open'
                : snapshot.phase === 'voting_closed' ? 'closed' : null}
            />
          ) : (
            <p className="empty-state">{t('partyControl.nothingRunning')}</p>
          )}

          {snapshot.voting && (
            <p className="party-control-votes" data-testid="party-control-votes">
              <strong>{snapshot.voting.received}</strong>
              <span> / {snapshot.voting.eligible} </span>
              {t('partyControl.votesReceived')}
              {snapshot.voting.yes !== null && (
                <span className="party-control-split" data-testid="party-control-split">
                  {' · '}{t('partyControl.split', {
                    yes: snapshot.voting.yes, no: snapshot.voting.no ?? 0,
                  })}
                </span>
              )}
            </p>
          )}
        </div>

        <aside className="party-control-next">
          <h3>{t('partyControl.nextUp')}</h3>
          {snapshot.nextChallenge ? (
            <PartyChallengeCard
              mode="compact"
              testId="party-control-next"
              challenge={{
                kind: snapshot.nextChallenge.kind,
                title: snapshot.nextChallenge.title,
                body: snapshot.nextChallenge.body,
                mediaUrl: snapshot.nextChallenge.mediaUrl,
                durationSeconds: snapshot.nextChallenge.durationSeconds,
              }}
            />
          ) : (
            <p className="empty-state">{t('partyControl.nothingNext')}</p>
          )}
          {qr && snapshot.status !== 'finished' && (
            <div className="party-control-qr">
              <div aria-hidden="true" data-testid="party-control-qr"
                dangerouslySetInnerHTML={{ __html: qr }} />
              <p className="muted">{t('partyControl.guestQr')}</p>
            </div>
          )}
        </aside>
      </section>

      {/* A refusal is stated where the action is, and the screen behind it is
          already correct: the server handed back the state it measured. */}
      {refusal && (
        <p className="inline-error" role="alert" data-testid="party-control-refusal">
          {t(REFUSAL_LABEL[refusal] ?? 'partyControl.refusedGeneric')}
        </p>
      )}

      <div className="party-control-commands">
        {primary && (
          <button
            type="button"
            className="party-control-primary"
            data-testid="party-control-primary"
            data-command={primary}
            disabled={pending !== null}
            onClick={() => runCommand(primary)}
          >
            {t(COMMAND_LABEL[primary])}
          </button>
        )}
        {secondary.map((command) => (
          <button
            key={command}
            type="button"
            className={command === 'finish' ? 'btn-danger' : 'party-control-secondary'}
            data-testid={`party-control-${command}`}
            disabled={pending !== null}
            onClick={() => runCommand(command)}
          >
            {t(COMMAND_LABEL[command])}
          </button>
        ))}
        {snapshot.availableCommands.length === 0 && (
          <p className="empty-state">{t('partyControl.nothingLeft')}</p>
        )}
      </div>

      {confirmFinish && (
        <Modal
          title={t('partyControl.finishTitle')}
          onClose={() => setConfirmFinish(false)}
          dismissable={pending === null}
          testId="party-control-finish-dialog"
          footer={(
            <div className="party-composer-actions">
              <button type="button" disabled={pending !== null}
                onClick={() => setConfirmFinish(false)}>{t('common.cancel')}</button>
              <button type="button" className="btn-danger" data-testid="party-control-finish-confirm"
                disabled={pending !== null}
                onClick={() => { setConfirmFinish(false); void run('finish'); }}>
                {t('partyControl.finish')}
              </button>
            </div>
          )}
        >
          <p>{t('partyControl.finishBody')}</p>
        </Modal>
      )}
    </div>
  );
}

function BackLink({ albumId }: { albumId: string | undefined }) {
  const { t } = useI18n();
  return <Link className="back-link" to={`/albums/${albumId ?? ''}`}>{t('partyControl.back')}</Link>;
}

function useQr(url: string | null): string | null {
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    if (!url) { setSvg(null); return; }
    let cancelled = false;
    void QRCode.toString(`${window.location.origin}${url}`, { type: 'svg', margin: 1, width: 160 })
      .then((value) => { if (!cancelled) setSvg(value); })
      .catch(() => { if (!cancelled) setSvg(null); });
    return () => { cancelled = true; };
  }, [url]);
  return svg;
}
