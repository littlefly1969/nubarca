import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  PartyGameConflict, partyGameYesPercent, submitPartyGameVote,
  type PartyGamePublicSnapshot, type PartyGameVoteValue,
} from '@nubarca/api-client';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import { usePartyGameSnapshot } from '../party/usePartyGameSnapshot';
import { useI18n } from '../i18n';
import './PartyGuestHub.css';
import './PartyGamePage.css';

// The guest's live game: one phone, one hand, one thing to do.
//
// Everything on this page is DERIVED from the server's snapshot. There is no
// client state machine, no optimistic phase, and nothing a guest's device
// decides — a phone that has been asleep for two activities shows the current
// scene the moment it wakes, because it renders whatever the last snapshot says
// rather than replaying what it missed.
//
// The owner's screen is a control room and the television is a show. This is
// neither: it is quiet between votes on purpose, because during an activity the
// interesting thing is happening in the room and not on anybody's phone.

type Scene = 'lobby' | 'watch' | 'vote' | 'waiting' | 'result' | 'finished';

/** The server's phase, as the one thing a guest is being asked to do. */
export function guestScene(snapshot: PartyGamePublicSnapshot | null): Scene {
  if (!snapshot) return 'lobby';
  if (snapshot.status === 'finished') return 'finished';
  switch (snapshot.phase) {
    case 'voting_open': return 'vote';
    case 'voting_closed': return 'waiting';
    case 'result': return 'result';
    case 'challenge_reveal':
    case 'challenge_active': return 'watch';
    default: return 'lobby';
  }
}

export function PartyGamePage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  const { snapshot, connection, stale, refresh, adopt } =
    usePartyGameSnapshot(token, { join: true });
  const [sending, setSending] = useState<PartyGameVoteValue | null>(null);
  const [voteError, setVoteError] = useState(false);

  const scene = guestScene(snapshot);
  const roundId = snapshot?.roundId ?? null;

  // A new round is a new question: whatever went wrong with the last one is not
  // this one's problem.
  useEffect(() => { setVoteError(false); }, [roundId]);

  const vote = useCallback(async (value: PartyGameVoteValue) => {
    if (!token || !roundId || sending) return;
    setSending(value);
    setVoteError(false);
    try {
      adopt(await submitPartyGameVote(token, roundId, value));
    } catch (error) {
      // A refusal carries the state it was measured against, so a phone that
      // fell behind ends the tap CORRECT rather than merely told off.
      if (error instanceof PartyGameConflict && error.snapshot) adopt(error.snapshot);
      else { setVoteError(true); refresh(); }
    } finally {
      setSending(null);
    }
  }, [token, roundId, sending, adopt, refresh]);

  if (connection === 'unavailable') {
    return (
      <main className="party-guest-hub party-game">
        <div className="party-game-notice">
          <h1>{t('partyGuestGame.unavailable')}</h1>
          {token && <Link className="party-game-back" to={`/party/${token}`}>{t('partyChallenges.back')}</Link>}
        </div>
      </main>
    );
  }

  if (connection === 'error') {
    return (
      <main className="party-guest-hub party-game">
        <div className="party-game-notice">
          <h1>{t('partyGuestGame.offline')}</h1>
          <p>{t('partyGuestGame.offlineHelp')}</p>
          <button type="button" className="party-game-retry" onClick={refresh}>
            {t('common.retry')}
          </button>
        </div>
      </main>
    );
  }

  if (connection === 'loading' || !snapshot) {
    return (
      <main className="party-guest-hub party-game" aria-busy="true">
        <span className="visually-hidden">{t('common.loading')}</span>
        <div className="party-skeleton party-skeleton-hero" />
      </main>
    );
  }

  const challenge = snapshot.challenge;
  const question = challenge?.voteQuestion?.trim() || t('partyGuestGame.defaultQuestion');
  const percent = partyGameYesPercent(snapshot.voting);

  return (
    <main className="party-guest-hub party-game" data-scene={scene} data-testid="party-game-page">
      <header className="party-game-top">
        <Link className="party-game-back" to={`/party/${token}`}>{t('partyChallenges.back')}</Link>
        <LanguageSwitcher className="language-switcher language-switcher-public" />
      </header>

      {/* A poll that failed is not a broken party: the scene stays, and this
          says quietly that it may be a moment behind. */}
      {stale && <p className="party-game-stale" role="status">{t('partyGuestGame.reconnecting')}</p>}

      <div className="party-game-stage">
        {scene === 'lobby' && (
          <section className="party-game-message">
            <p className="party-game-eyebrow">{snapshot.albumName}</p>
            <h1>{t('partyGuestGame.lobbyTitle')}</h1>
            <p>{t('partyGuestGame.lobbyBody')}</p>
          </section>
        )}

        {scene === 'watch' && (
          <section className="party-game-message">
            <p className="party-game-eyebrow">
              {t('partyActivity.round', { round: snapshot.roundNumber, total: snapshot.totalChallenges })}
            </p>
            <h1>{t('partyGuestGame.watchTitle')}</h1>
            {challenge && (
              <PartyChallengeCard
                mode="compact"
                testId="party-game-summary"
                challenge={{
                  kind: challenge.kind, title: challenge.title, body: challenge.body,
                  mediaUrl: challenge.mediaUrl, durationSeconds: challenge.durationSeconds,
                }}
              />
            )}
          </section>
        )}

        {scene === 'vote' && (
          <section className="party-game-vote">
            <p className="party-game-eyebrow">{challenge?.title}</p>
            <h1 className="party-game-question">{question}</h1>
            {snapshot.myVote && (
              <p className="party-game-confirmed" role="status" data-testid="party-game-confirmed">
                {t('partyGuestGame.voteRecorded')} · {t('partyGuestGame.voteChangeable')}
              </p>
            )}
            {voteError && (
              <p className="inline-error" role="alert">{t('partyGuestGame.voteFailed')}</p>
            )}
            {/* Two targets, thumb-sized, in the lower half of the screen, and
                nothing to scroll past to reach them. */}
            <div className="party-game-answers">
              {(['yes', 'no'] as PartyGameVoteValue[]).map((value) => (
                <button
                  key={value}
                  type="button"
                  className="party-game-answer"
                  data-value={value}
                  data-testid={`party-game-vote-${value}`}
                  aria-pressed={snapshot.myVote === value}
                  disabled={sending !== null}
                  onClick={() => void vote(value)}
                >
                  {t(value === 'yes' ? 'partyGuestGame.passed' : 'partyGuestGame.failed')}
                </button>
              ))}
            </div>
          </section>
        )}

        {scene === 'waiting' && (
          <section className="party-game-message">
            <h1>{t('partyGuestGame.waitingTitle')}</h1>
            <p>{t('partyGuestGame.waitingBody')}</p>
            {snapshot.myVote && (
              <p className="party-game-confirmed" role="status">{t('partyGuestGame.voteRecorded')}</p>
            )}
          </section>
        )}

        {scene === 'result' && (
          <section className="party-game-message party-game-result">
            <p className="party-game-eyebrow">{challenge?.title}</p>
            {percent === null ? (
              <h1>{t('partyGuestGame.resultNoVote')}</h1>
            ) : (
              <>
                <p className="party-game-percent" data-testid="party-game-percent">{percent}%</p>
                <h1>{t(snapshot.voting?.passed
                  ? 'partyGuestGame.resultPassed' : 'partyGuestGame.resultFailed')}</h1>
              </>
            )}
          </section>
        )}

        {scene === 'finished' && (
          <section className="party-game-message">
            <h1>{t('partyGuestGame.finishedTitle')}</h1>
            <p>{t('partyGuestGame.finishedBody')}</p>
            <Link className="party-game-retry" to={`/party/${token}`}>
              {t('partyGuestGame.backToParty')}
            </Link>
          </section>
        )}
      </div>
    </main>
  );
}
