import { useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import QRCode from 'qrcode';
import { partyGameYesPercent, type PartyGamePublicSnapshot } from '@nubarca/api-client';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import { usePartyGameSnapshot } from '../party/usePartyGameSnapshot';
import { useCountdown, formatCountdown } from '../party/useCountdown';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import '../party/PartyTvStage.css';

// The party show, on whatever screen the host put in the corner of the room.
//
// It is a DISPLAY. There is not one interactive control on this page — no
// buttons, no links, nothing focusable — because the game is conducted from the
// control room and a television that could be nudged is a television somebody
// will nudge.
//
// Every scene is derived from the server's snapshot, so a reload fetches and
// renders the right one without touching the game. The only thing the client
// decides on its own is the brief "next activity" flourish between rounds, and
// even that is a presentation timer over the same snapshot: it yields the
// instant the phase moves, and it never plays on a fresh load, so a television
// that was unplugged and switched back on lands straight on the current scene.

// How long the between-rounds title card holds. Short and deliberate: this is a
// beat, not an interstitial.
const INTRO_MS = 2_200;

export type StageScene =
  | 'lobby' | 'reveal' | 'active' | 'vote' | 'closed' | 'result' | 'finished';

/** A pure projection of the server's phase. No second state machine. */
export function stageScene(snapshot: PartyGamePublicSnapshot | null): StageScene {
  if (!snapshot) return 'lobby';
  if (snapshot.status === 'finished') return 'finished';
  switch (snapshot.phase) {
    case 'challenge_reveal': return 'reveal';
    case 'challenge_active': return 'active';
    case 'voting_open': return 'vote';
    case 'voting_closed': return 'closed';
    case 'result': return 'result';
    default: return 'lobby';
  }
}

export function PartyTvStagePage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  // A television never joins — a display that mints a participant inflates the
  // very count it is showing — but it does SAY it is a display, which is what
  // lets the control room answer "is a screen showing this".
  const { snapshot, connection, stale } = usePartyGameSnapshot(token, {
    join: false, asDisplay: true,
  });
  const scene = stageScene(snapshot);
  const intro = useRoundIntro(snapshot?.roundId ?? null, scene, snapshot !== null);
  const remaining = useCountdown(scene === 'active' ? snapshot?.phaseEndsAt : null);
  const qr = useGameQr(token, scene === 'lobby');

  if (connection === 'unavailable' || connection === 'error') {
    return (
      <main className="party-stage" data-scene="lobby" data-testid="party-tv-stage">
        <div className="party-stage-centre">
          <p className="party-stage-eyebrow">{PRODUCT_NAME}</p>
          <h1 className="party-stage-headline">
            {t(connection === 'unavailable' ? 'partyStage.unavailable' : 'partyStage.offline')}
          </h1>
        </div>
      </main>
    );
  }

  if (!snapshot) {
    return (
      <main className="party-stage" data-scene="lobby" aria-busy="true" data-testid="party-tv-stage">
        <span className="visually-hidden">{t('common.loading')}</span>
      </main>
    );
  }

  const challenge = snapshot.challenge;
  const voting = snapshot.voting;
  const percent = partyGameYesPercent(voting);
  const round = t('partyActivity.round', {
    round: snapshot.roundNumber, total: snapshot.totalChallenges,
  });

  return (
    <main
      className="party-stage"
      data-scene={intro ? 'intro' : scene}
      data-testid="party-tv-stage"
      aria-live="polite"
    >
      {stale && <p className="party-stage-stale">{t('partyStage.reconnecting')}</p>}

      {intro && (
        // Scene 1. A beat between activities, over the same snapshot.
        <div className="party-stage-centre party-stage-intro" data-testid="party-stage-intro">
          <p className="party-stage-eyebrow">{round}</p>
          <h1 className="party-stage-headline">{t('partyStage.nextActivity')}</h1>
        </div>
      )}

      {!intro && scene === 'lobby' && (
        <div className="party-stage-centre">
          <p className="party-stage-eyebrow">{`${PRODUCT_NAME} · ${snapshot.albumName}`}</p>
          <h1 className="party-stage-headline">{t('partyStage.lobbyTitle')}</h1>
          <p className="party-stage-sub">{t('partyStage.lobbyBody')}</p>
          {/* The way in. A television that shows a game nobody can join is a
              television showing a game nobody joins. */}
          {qr && (
            <div className="party-stage-qr" data-testid="party-stage-qr"
              aria-hidden="true" dangerouslySetInnerHTML={{ __html: qr }} />
          )}
        </div>
      )}

      {!intro && (scene === 'reveal' || scene === 'active') && challenge && (
        <>
          {/* Scenes 2 and 3: THE card, filling the screen, with the chrome
              inside the same safe area rather than shrinking it. */}
          <PartyChallengeCard
            mode="tv"
            testId="party-stage-card"
            className="party-stage-card"
            challenge={{
              kind: challenge.kind, title: challenge.title, body: challenge.body,
              mediaUrl: challenge.mediaUrl, durationSeconds: challenge.durationSeconds,
            }}
            context={{ round: snapshot.roundNumber, total: snapshot.totalChallenges }}
          />
          {scene === 'active' && remaining !== null && (
            <p className="party-stage-timer" data-testid="party-stage-timer">
              {formatCountdown(remaining)}
            </p>
          )}
        </>
      )}

      {!intro && scene === 'vote' && (
        // Scene 4. How many have answered — never what they answered.
        <div className="party-stage-centre">
          <p className="party-stage-eyebrow">{challenge?.title ?? round}</p>
          <h1 className="party-stage-headline party-stage-call">{t('partyStage.voteNow')}</h1>
          <p className="party-stage-sub">
            {challenge?.voteQuestion?.trim() || t('partyGuestGame.defaultQuestion')}
          </p>
          {voting && (
            <p className="party-stage-tally" data-testid="party-stage-tally">
              <strong>{voting.received}</strong>
              <span> / {voting.eligible} </span>
              {t('partyStage.haveVoted')}
            </p>
          )}
        </div>
      )}

      {!intro && scene === 'closed' && (
        <div className="party-stage-centre">
          <p className="party-stage-eyebrow">{challenge?.title ?? round}</p>
          <h1 className="party-stage-headline">{t('partyStage.votingClosed')}</h1>
          {voting && (
            <p className="party-stage-tally">
              <strong>{voting.received}</strong>
              <span> / {voting.eligible} </span>
              {t('partyStage.haveVoted')}
            </p>
          )}
        </div>
      )}

      {!intro && scene === 'result' && (
        // Scene 5. The headline lands, then the number, then the verdict.
        <div className="party-stage-centre party-stage-result">
          <p className="party-stage-eyebrow">{challenge?.title ?? round}</p>
          {percent === null ? (
            <h1 className="party-stage-headline">{t('partyStage.resultNoVote')}</h1>
          ) : (
            <>
              <h1 className="party-stage-headline party-stage-verdict-intro">
                {t('partyStage.audienceDecided')}
              </h1>
              <p className="party-stage-percent" data-testid="party-stage-percent">{percent}%</p>
              <p
                className="party-stage-verdict"
                data-passed={voting?.passed ? 'true' : 'false'}
                data-testid="party-stage-verdict"
              >
                {t(voting?.passed ? 'partyStage.passed' : 'partyStage.failed')}
              </p>
            </>
          )}
        </div>
      )}

      {!intro && scene === 'finished' && (
        <div className="party-stage-centre">
          <p className="party-stage-eyebrow">{`${PRODUCT_NAME} · ${snapshot.albumName}`}</p>
          <h1 className="party-stage-headline">{t('partyStage.finishedTitle')}</h1>
          <p className="party-stage-sub">
            {t('partyStage.finishedBody', { count: snapshot.roundNumber })}
          </p>
        </div>
      )}
    </main>
  );
}

/**
 * The between-rounds beat.
 *
 * Deliberately not state about the game: it fires only on an OBSERVED change of
 * round into a reveal, never on the first snapshot after mount. That is what
 * makes a reload land straight on the current scene instead of replaying a
 * flourish for an activity the room has already seen.
 */
function useRoundIntro(roundId: string | null, scene: StageScene, ready: boolean): boolean {
  const [showing, setShowing] = useState(false);
  // `undefined` means no snapshot has been observed yet, which is not the same
  // as a snapshot whose round is null. Nothing is recorded until the first one
  // arrives, so the first thing a television sees is never treated as a change.
  const previous = useRef<string | null | undefined>(undefined);

  useEffect(() => {
    if (!ready) return;
    const seen = previous.current;
    previous.current = roundId;
    if (seen === undefined || roundId === null || seen === roundId) return;
    setShowing(true);
    const timer = setTimeout(() => setShowing(false), INTRO_MS);
    return () => clearTimeout(timer);
  }, [roundId, ready]);

  // The snapshot always wins: the moment the host moves past the reveal, the
  // beat is over whether or not its timer has run.
  return showing && scene === 'reveal';
}

function useGameQr(token: string | undefined, wanted: boolean): string | null {
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    if (!token || !wanted) { setSvg(null); return; }
    let cancelled = false;
    void QRCode.toString(`${window.location.origin}/party/${token}/game`,
      { type: 'svg', margin: 1, width: 260 })
      .then((value) => { if (!cancelled) setSvg(value); })
      .catch(() => { if (!cancelled) setSvg(null); });
    return () => { cancelled = true; };
  }, [token, wanted]);
  return svg;
}
