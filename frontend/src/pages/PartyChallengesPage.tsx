import { Navigate, useParams } from 'react-router';

/**
 * THE SECOND GAME IS GONE. This route is a redirect.
 *
 * A guest used to meet two entries in the hub — "Game" and "Vote the
 * challenges" — which were two different votes wearing one word. One of them
 * chose which activity the slideshow would interrupt with; the other decided
 * whether an activity had been done. There is now ONE Party Game, and it holds
 * both halves in the phases the server owns: the lobby is where a guest says
 * which activities they would like to see, and `voting_open` is where the room
 * says whether this one counted.
 *
 * The route survives because printed material and a guest's own browser history
 * may still name it. It is deliberately a client redirect and not a token
 * migration: the token is unchanged, nothing about the capability moves, and
 * `replace` keeps the dead address out of the back button.
 */
export function PartyChallengesPage() {
  const { token } = useParams<{ token: string }>();
  return <Navigate to={token ? `/party/${token}/game` : '/'} replace />;
}
