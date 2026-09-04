// Which half of the contribution page a guest lands on.
//
// The party surface has ONE contribution destination — the upload token's page,
// which the backend hands out as `contributionUrl` — and two things a guest can
// leave there: media, or a written dedication. The mode is a query parameter on
// that same URL rather than a second route, because the two share a token, a
// session and an enablement flag; splitting them into two routes would mean
// duplicating all three.
//
// So: "share a moment" opens the page as it comes (media), and "leave a
// dedication" opens the same page already on the composer.

export type ContributionMode = 'media' | 'message';

export const CONTRIBUTION_MODE_PARAM = 'mode';

/**
 * The mode a query value asks for.
 *
 * Anything that is not a mode this page has — absent, misspelt, a stale link
 * from a future version — resolves to media, which is the page's default and
 * always safe.
 */
export function contributionModeFrom(value: string | null | undefined): ContributionMode {
  return value === 'message' ? 'message' : 'media';
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
