// Supported UI languages. Italian is the canonical default; English is the
// optional second language. These must stay in sync with the backend
// UiLanguages catalog ("it" | "en").
export type Language = 'it' | 'en';

export const LANGUAGES: readonly Language[] = ['it', 'en'] as const;

export const DEFAULT_LANGUAGE: Language = 'it';

// Narrows an arbitrary value (querystring, localStorage, server field) to a
// supported Language. Anything else (unsupported code, region-tagged locale,
// null) is rejected — callers fall back to the Italian default.
export function toLanguage(value: unknown): Language | null {
  return value === 'it' || value === 'en' ? value : null;
}

// BCP-47 locale used for Intl date/number formatting per language.
export const LOCALE: Record<Language, string> = {
  it: 'it-IT',
  en: 'en-GB',
};
