import { useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import QRCode from 'qrcode';
import { useEffect } from 'react';
import {
  partyGameDisplayState, type PartyGameCommand, type PartyGamePlanAction,
  type PartyGamePlanEntry, type PartyGameSnapshot,
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
  restart_game: 'partyControl.restart',
  return_to_party: 'partyControl.returnToParty',
};

// The two commands a host must not send by accident: one ends the evening, the
// other throws away what the room just voted. Both are confirmed, and the
// dialog names exactly what is lost and what is not.
const CONFIRMED: Partial<Record<PartyGameCommand, { title: MessageKey; body: MessageKey }>> = {
  finish: { title: 'partyControl.finishTitle', body: 'partyControl.finishBody' },
  restart_game: { title: 'partyControl.restartTitle', body: 'partyControl.restartBody' },
};

const REFUSAL_LABEL: Record<string, MessageKey> = {
  version_conflict: 'partyControl.refusedStale',
  illegal_transition: 'partyControl.refusedIllegal',
  no_challenges: 'partyControl.refusedEmpty',
  game_disabled: 'partyControl.refusedDisabled',
  invalid_plan: 'partyControl.refusedPlan',
  conflict: 'partyControl.refusedGeneric',
};

const PHASE_LABEL: Record<PartyGameSnapshot['phase'], MessageKey> = {
  lobby: 'partyControl.phaseLobby',
  challenge_reveal: 'partyControl.phaseReveal',
  challenge_active: 'partyControl.phaseActive',
  voting_open: 'partyControl.phaseVoting',
  voting_closed: 'partyControl.phaseClosed',
  result: 'partyControl.phaseResult',
  intermission: 'partyControl.phaseIntermission',
  finished: 'partyControl.phaseFinished',
};

export function PartyControlRoomPage() {
  const { albumId } = useParams<{ albumId: string }>();
  const { t } = useI18n();
  const { snapshot, connection, stale, pending, planning, refusal, run, plan, refresh } =
    usePartyGameControl(albumId);
  const [confirming, setConfirming] = useState<PartyGameCommand | null>(null);
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
    if (command in CONFIRMED) { setConfirming(command); return; }
    void run(command);
  };
  const confirmation = confirming ? CONFIRMED[confirming] : undefined;

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
          <strong data-testid="party-control-guests">{snapshot.guestsPresent}</strong>
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
          <strong data-testid="party-control-played">{snapshot.playedRounds}</strong>
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

      {/* THE PLAN. What is left to play, in the order it will be played, with
          what the room asked for beside it. The host reorders the future and
          sets aside what there is no time for; the past and the present carry
          no controls at all, because neither is plannable. */}
      <PartyPlanPanel
        plan={snapshot.plan ?? []}
        priorityVotingEnabled={snapshot.priorityVotingEnabled}
        preferencesOpen={snapshot.preferencesOpen}
        busy={planning || pending !== null}
        onPlan={(action, id, position) => void plan(action, id, position)}
      />

      {confirming && confirmation && (
        <Modal
          title={t(confirmation.title)}
          onClose={() => setConfirming(null)}
          dismissable={pending === null}
          testId={`party-control-${confirming}-dialog`}
          footer={(
            <div className="party-composer-actions">
              <button type="button" disabled={pending !== null}
                onClick={() => setConfirming(null)}>{t('common.cancel')}</button>
              <button type="button" className="btn-danger"
                data-testid={`party-control-${confirming}-confirm`}
                disabled={pending !== null}
                onClick={() => { setConfirming(null); void run(confirming); }}>
                {t(COMMAND_LABEL[confirming])}
              </button>
            </div>
          )}
        >
          <p>{t(confirmation.body)}</p>
        </Modal>
      )}
    </div>
  );
}

/**
 * The evening, as something the host can still change.
 *
 * Three rules are visible in the markup rather than stated in a comment
 * somewhere else. A `played` or `current` row has no controls — the server
 * refuses to move either, so offering the button would be offering a refusal.
 * An excluded activity KEEPS its preference count, because "the room wanted
 * this and there was no time" is worth seeing. And the order is the server's:
 * every control sends one action and re-renders from the snapshot that comes
 * back, so nothing here decides what is played next.
 */
function PartyPlanPanel({
  plan, priorityVotingEnabled, preferencesOpen, busy, onPlan,
}: {
  plan: PartyGamePlanEntry[];
  priorityVotingEnabled: boolean;
  preferencesOpen: boolean;
  busy: boolean;
  onPlan(action: PartyGamePlanAction, challengeId: string, position?: number): void;
}) {
  const { t } = useI18n();
  if (plan.length === 0) return null;
  // Positions are 1-based on the wire and 0-based in a move, and only the rows
  // that actually have one take part in a reorder.
  const queued = plan.filter((x) => x.position !== null);

  return (
    <section className="party-control-plan" data-testid="party-control-plan">
      <div className="party-control-plan-head">
        <h3>{t('partyPlan.title')}</h3>
        <p className="muted">
          {priorityVotingEnabled
            ? t(preferencesOpen ? 'partyPlan.preferencesOpen' : 'partyPlan.preferencesClosed')
            : t('partyPlan.preferencesOff')}
        </p>
      </div>
      <ol className="party-control-plan-list">
        {plan.map((entry) => {
          const at = entry.position === null ? -1 : queued.findIndex((x) => x.id === entry.id);
          const movable = entry.state === 'remaining' && entry.position !== null;
          return (
            <li
              key={entry.id}
              data-state={entry.state}
              data-excluded={entry.excluded ? 'true' : undefined}
              data-testid={`party-plan-${entry.id}`}
            >
              <span className="party-control-plan-position">
                {entry.position ?? '—'}
              </span>
              {entry.mediaUrl && <img src={entry.mediaUrl} alt="" loading="lazy" />}
              <span className="party-control-plan-copy">
                <strong>{entry.title}</strong>
                <span className="party-control-plan-meta">
                  <span data-testid={`party-plan-state-${entry.id}`}>
                    {t(entry.state === 'played' ? 'partyPlan.played'
                      : entry.state === 'current' ? 'partyPlan.current'
                      : entry.excluded ? 'partyPlan.excluded'
                      : !entry.isEnabled ? 'partyPlan.off'
                      : 'partyPlan.remaining')}
                  </span>
                  {priorityVotingEnabled && (
                    <span
                      className="party-control-plan-votes"
                      data-testid={`party-plan-votes-${entry.id}`}
                    >
                      {t('partyPlan.preferences', { count: entry.preferenceVotes })}
                    </span>
                  )}
                </span>
              </span>
              {/* Absent, never disabled, for anything the server would refuse. */}
              {movable && (
                <span className="party-control-plan-actions">
                  <button
                    type="button" disabled={busy || at === 0}
                    data-testid={`party-plan-next-${entry.id}`}
                    onClick={() => onPlan('move', entry.id, 0)}
                  >
                    {t('partyPlan.playNext')}
                  </button>
                  <button
                    type="button" aria-label={t('partyGame.moveUp')}
                    disabled={busy || at <= 0}
                    onClick={() => onPlan('move', entry.id, at - 1)}
                  >↑</button>
                  <button
                    type="button" aria-label={t('partyGame.moveDown')}
                    disabled={busy || at < 0 || at === queued.length - 1}
                    onClick={() => onPlan('move', entry.id, at + 1)}
                  >↓</button>
                </span>
              )}
              {entry.state === 'remaining' && (
                <button
                  type="button"
                  className="party-control-plan-toggle"
                  disabled={busy}
                  data-testid={`party-plan-toggle-${entry.id}`}
                  onClick={() => onPlan(entry.excluded ? 'include' : 'exclude', entry.id)}
                >
                  {t(entry.excluded ? 'partyPlan.include' : 'partyPlan.exclude')}
                </button>
              )}
            </li>
          );
        })}
      </ol>
    </section>
  );
}

// The control room is reached FROM a party, and its way out returns there
// rather than to an album the host may never have opened. An old bookmark with
// no party in it still lands on the album.
function BackLink({ albumId }: { albumId: string | undefined }) {
  const { t } = useI18n();
  const [searchParams] = useSearchParams();
  const partyId = searchParams.get('party');
  return partyId
    ? (
      <Link className="back-link" to={`/parties/${partyId}?section=activities`}>
        {t('partyUploads.backToParty')}
      </Link>
    )
    : <Link className="back-link" to={`/albums/${albumId ?? ''}`}>{t('partyControl.back')}</Link>;
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
