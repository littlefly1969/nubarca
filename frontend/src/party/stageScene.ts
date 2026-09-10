import { useEffect, useRef, useState } from 'react';
import type { PartyGamePublicSnapshot } from '@nubarca/api-client';

// The projection of the server's phase onto a scene, and the one beat the
// client owns. Extracted so BOTH television routes — the public party TV and
// the paired-display surface — share exactly one of each. Two copies of
// `stageScene` would be two state machines, which is the thing this feature
// has said from the beginning it does not have.

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

/**
 * The between-rounds beat.
 *
 * Deliberately not state about the game: it fires only on an OBSERVED change of
 * round into a reveal, never on the first snapshot after mount. That is what
 * makes a reload land straight on the current scene instead of replaying a
 * flourish for an activity the room has already seen.
 */
export function useRoundIntro(roundId: string | null, scene: StageScene, ready: boolean): boolean {
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
