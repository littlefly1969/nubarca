import { Link } from 'react-router';
import { usePartyGameSnapshot } from './usePartyGameSnapshot';
import { useI18n, type MessageKey } from '../i18n';
import './PartyGameAffordance.css';

// THE WAY IN, for somebody who is already at the party.
//
// A guest standing on `/party/{token}` has already scanned the QR. Asking them
// to scan a second one when the game starts is asking them to find the card on
// the table again, in the dark, while something is happening — so the party
// itself says what the game is doing and offers one tap. The television's QR
// stays exactly what it is: the way IN for somebody not yet at the party.
//
// It is NOT a takeover. Nothing here redirects, and nothing here interrupts a
// guest who is reading the menu or looking at photographs: the bar appears, it
// says what is happening, and the guest decides. Once they are inside `/game`
// the server's snapshots drive everything and they never come back here by
// accident.
//
// It reads the PUBLIC snapshot and deliberately does not join — a hub that
// minted a participant would count everybody who ever opened the party as being
// in the room, which is the number the control room reads out loud.

/** Quiet by design: the hub is not the game, and this is a signpost. */
const POLL_MS = 10_000;

type Beat = 'choose' | 'live' | 'vote' | 'paused';

const BEAT_LABEL: Record<Beat, MessageKey> = {
  choose: 'partyHub.gameChoose',
  live: 'partyHub.gameLive',
  vote: 'partyHub.gameVote',
  paused: 'partyHub.gamePaused',
};

/**
 * What the party is doing, in the four words a guest needs — or null when there
 * is nothing to walk into.
 *
 * Pure and exported so the rule is testable without a DOM. `finished` answers
 * null on purpose: a match that is over has no door, and a bar inviting
 * somebody into it would be the dead CTA every other Party surface refuses to
 * render.
 */
export function partyGameBeat(
  snapshot: { status: string; phase: string; preferences: { open: boolean } | null } | null,
): Beat | null {
  if (!snapshot || snapshot.status === 'finished') return null;
  if (snapshot.phase === 'voting_open') return 'vote';
  if (snapshot.phase === 'intermission') return 'paused';
  // The lobby is "choose" only while there is something to choose. A party with
  // preferences switched off, or one whose host has already started, is simply
  // a game in progress.
  if (snapshot.phase === 'lobby') return snapshot.preferences?.open ? 'choose' : 'live';
  return 'live';
}

export function PartyGameAffordance({ token, gameUrl }: { token: string; gameUrl: string }) {
  const { t } = useI18n();
  const { snapshot } = usePartyGameSnapshot(token, { join: false, pollMs: POLL_MS });
  const beat = partyGameBeat(snapshot);
  if (beat === null) return null;

  return (
    <Link
      className="party-game-affordance"
      data-beat={beat}
      data-testid="party-game-affordance"
      to={gameUrl}
    >
      <span className="party-game-affordance-dot" aria-hidden="true" />
      <span className="party-game-affordance-text">{t(BEAT_LABEL[beat])}</span>
      <span className="party-game-affordance-go">{t('partyHub.gameEnter')}</span>
    </Link>
  );
}
