import { createContext } from 'react';
import type { Language } from './types';
import type { MessageKey } from './it';

export type TranslateParams = Record<string, string | number>;

export interface I18nContextValue {
  lang: Language;
  // Change the active UI language. `persistLocal` (default true) writes the
  // choice to localStorage for unauthenticated/public reuse; authenticated
  // callers that persist server-side pass their own flow.
  setLanguage: (lang: Language, options?: { persistLocal?: boolean }) => void;
  // Translate a key with optional {placeholder} interpolation.
  t: (key: MessageKey, params?: TranslateParams) => string;
  // Plural-aware translate: picks `<key>_one` / `<key>_other` by count and
  // injects {count} automatically (plus any extra params).
  tn: (count: number, key: PluralKey, params?: TranslateParams) => string;
  // Locale-aware date/number formatting for visible values.
  formatDate: (value: string | number | Date, options?: Intl.DateTimeFormatOptions) => string;
  formatNumber: (value: number, options?: Intl.NumberFormatOptions) => string;
}

// Keys that have `_one`/`_other` plural variants. Derived from MessageKey so
// tn() only accepts real plural bases. The generic parameter is naked, so the
// conditional distributes over the MessageKey union (a bare `MessageKey extends
// ...` would not).
type PluralBase<K> = K extends `${infer B}_other` ? B : never;
export type PluralKey = PluralBase<MessageKey>;

export const I18nContext = createContext<I18nContextValue | null>(null);
