// The UI languages NubArca ships. These must stay in sync with the backend's
// UiLanguages catalogue — the server persists an authenticated user's choice,
// and a code one side accepts and the other rejects is a preference that
// silently reverts on the next sign-in.
//
// ITALIAN IS THE CANONICAL CATALOGUE AND THE FALLBACK. `it.ts` defines
// `MessageKey`; every other dictionary is typed against it, so a language can
// never introduce a key Italian lacks, and a key missing anywhere resolves to
// the Italian text rather than to a raw key on somebody's screen. `i18n.test`
// then requires every dictionary to carry every key, so that fallback is a
// safety net rather than a strategy.
export type Language = 'it' | 'en' | 'es' | 'de';

export const LANGUAGES: readonly Language[] = ['it', 'en', 'es', 'de'] as const;

export const DEFAULT_LANGUAGE: Language = 'it';

// Narrows an arbitrary value (querystring, localStorage, server field) to a
// supported Language. Anything else (unsupported code, region-tagged locale,
// null) is rejected — callers fall back to the Italian default.
export function toLanguage(value: unknown): Language | null {
  return value === 'it' || value === 'en' || value === 'es' || value === 'de'
    ? value
    : null;
}

/**
 * The language a BROWSER is asking for, or null.
 *
 * Region tags are accepted here and nowhere else: `navigator.language` is
 * almost always region-tagged ("es-AR", "de-CH", "en-US"), and refusing those
 * would mean browser detection never matched anybody. What is stored and what
 * travels on the wire stays the bare code — this only reads an intent.
 *
 * The list is walked in the browser's own order of preference, so a person
 * whose languages are ["fr", "es"] gets Spanish rather than the default.
 */
export function preferredLanguage(
  requested: readonly string[] | undefined,
): Language | null {
  for (const raw of requested ?? []) {
    if (typeof raw !== 'string') continue;
    const bare = raw.trim().toLowerCase().split('-')[0];
    const match = toLanguage(bare);
    if (match) return match;
  }
  return null;
}

// BCP-47 locale used for Intl date/number formatting per language.
export const LOCALE: Record<Language, string> = {
  it: 'it-IT',
  en: 'en-GB',
  es: 'es-ES',
  de: 'de-DE',
};
