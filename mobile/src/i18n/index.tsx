import React, { createContext, useCallback, useContext, useMemo, useState } from 'react';
import it, { type MobileMessageKey } from './it';
import en from './en';

// Two supported UI languages, matching the backend UiLanguages catalog. Italian
// is the default until the signed-in user's language is adopted.
export type Language = 'it' | 'en';

export function toLanguage(value: unknown): Language | null {
  return value === 'it' || value === 'en' ? value : null;
}

type Params = Record<string, string | number>;

const DICTIONARIES: Record<Language, Partial<Record<MobileMessageKey, string>>> = { it, en };

function interpolate(template: string, params?: Params): string {
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (m, name: string) =>
    Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : m,
  );
}

interface I18nValue {
  lang: Language;
  setLanguage: (lang: Language) => void;
  t: (key: MobileMessageKey, params?: Params) => string;
}

const I18nContext = createContext<I18nValue | null>(null);

export function I18nProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const [lang, setLang] = useState<Language>('it');
  const setLanguage = useCallback((next: Language) => setLang(next), []);

  const value = useMemo<I18nValue>(() => {
    const lookup = (key: MobileMessageKey): string => DICTIONARIES[lang][key] ?? it[key] ?? key;
    const t = (key: MobileMessageKey, params?: Params) => interpolate(lookup(key), params);
    return { lang, setLanguage, t };
  }, [lang, setLanguage]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error('useI18n must be used within an I18nProvider');
  return ctx;
}
