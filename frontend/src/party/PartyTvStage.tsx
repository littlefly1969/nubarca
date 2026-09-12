import { partyGameYesPercent, type PartyGamePublicSnapshot } from '@nubarca/api-client';
import { PartyChallengeCard } from './PartyChallengeCard';
import { useCountdown, formatCountdown } from './useCountdown';
import { useRoundIntro, stageScene, type StageScene } from './stageScene';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import './PartyTvStage.css';

// THE party show, and there is exactly one of it.
//
// Two routes render this component: the public party television
// (/party/{token}/tv) and the display surface a paired NubArca TV mounts
// (/party-display/stage). They differ in how they are AUTHORISED and in
// nothing else — same scenes, same markup, same CSS, same PartyChallengeCard.
// A second implementation for the television would be a second place for the
// room and the monitor in the corner to disagree about what the party is
// doing.
//
// So this component knows nothing about tokens, grants or polling. It is
// handed a snapshot and told how the connection is doing; everything else it
// derives. `lobbyQr` is passed in rather than built here because the two routes
// obtain it differently — one holds the party token, the other deliberately
// does not — and that difference must not reach the presentation.

export interface PartyTvStageProps {
  snapshot: PartyGamePublicSnapshot | null;
  connection: 'loading' | 'ready' | 'unavailable' | 'error';
  stale: boolean;
  /** Ready-to-inject SVG for the lobby's join code, or null. */
  lobbyQr: string | null;
  /**
   * The activity photograph, already resolved to something an <img> can load.
   * The display surface hands over an object URL it fetched with its grant,
   * because a header cannot be attached to an <img>; the public route passes
   * nothing and the card uses the snapshot's own URL.
   */
  mediaUrlOverride?: string | null;
}

export function PartyTvStage({
  snapshot, connection, stale, lobbyQr, mediaUrlOverride = null,
}: PartyTvStageProps) {
  const { t } = useI18n();
  const scene = stageScene(snapshot);
  const intro = useRoundIntro(snapshot?.roundId ?? null, scene, snapshot !== null);
  const remaining = useCountdown(scene === 'active' ? snapshot?.phaseEndsAt : null);
  const qr = lobbyQr;

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
        // The one scene where the room has to DO something — scan — so it is
        // composed from the top of the safe area down: the words at the size
        // every scene uses, then the code, which takes all the height they
        // leave (see .party-stage-lobby).
        <div className="party-stage-lobby" data-testid="party-stage-lobby">
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
              mediaUrl: mediaUrlOverride ?? challenge.mediaUrl, durationSeconds: challenge.durationSeconds,
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

export type { StageScene };
