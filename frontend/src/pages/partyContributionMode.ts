// Which half of the contribution page a guest lands on.
//
// The party surface has ONE contribution destination — the upload token's page,
// which the backend hands out as `contributionUrl` — and THREE things a guest
// can leave there: photographs, a greeting for the screen, or a dedication for
// the book. The mode is a query parameter on that same URL rather than three
// routes, because all three share a token and a session; splitting them would
// mean duplicating both.
//
// The page is reachable whenever ANY of the three is open, and each half
// renders only when its own switch is on — which is what makes them three
// independent decisions rather than three names for one.

export type ContributionMode = 'media' | 'message' | 'guestbook';

export const CONTRIBUTION_MODE_PARAM = 'mode';

/**
 * The mode a query value asks for.
 *
 * Anything that is not a mode this page has — absent, misspelt, a stale link
 * from a future version — resolves to media, which is the page's default and
 * always safe.
 */
export function contributionModeFrom(value: string | null | undefined): ContributionMode {
  if (value === 'message') return 'message';
  if (value === 'guestbook') return 'guestbook';
  return 'media';
}

/**
 * `url` with the mode set, keeping everything else it already carries.
 *
 * The URL comes from the backend, so it is used as given and never rebuilt from
 * parts: a relative path stays relative, an absolute one stays absolute, and any
 * query or fragment it already has survives.
 */
export function withContributionMode(url: string, mode: ContributionMode): string {
  if (url.trim() === '') return url;
  // A base only so relative paths parse; it is dropped again below.
  const BASE = 'https://party.invalid';
  let parsed: URL;
  try {
    parsed = new URL(url, BASE);
  } catch {
    return url;  // not a URL we can reason about: hand it back untouched
  }
  parsed.searchParams.set(CONTRIBUTION_MODE_PARAM, mode);
  return parsed.origin === BASE
    ? `${parsed.pathname}${parsed.search}${parsed.hash}`
    : parsed.toString();
}
