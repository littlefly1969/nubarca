import React, { createContext, useCallback, useContext, useMemo, useState } from 'react';
import it, { type TvMessageKey } from './it';
import en from './en';

export type { TvMessageKey };

// Two supported UI languages, matching the backend UiLanguages catalog. Italian
// is the default when the owner language is unavailable.
export type Language = 'it' | 'en';

export function toLanguage(value: unknown): Language | null {
  return value === 'it' || value === 'en' ? value : null;
}

type PluralBase<K> = K extends `${infer B}_other` ? B : never;
type PluralKey = PluralBase<TvMessageKey>;
type Params = Record<string, string | number>;

const DICTIONARIES: Record<Language, Partial<Record<TvMessageKey, string>>> = { it, en };

function interpolate(template: string, params?: Params): string {
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (m, name: string) =>
    Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : m,
  );
}

interface I18nValue {
  lang: Language;
  setLanguage: (lang: Language) => void;
  t: (key: TvMessageKey, params?: Params) => string;
  tn: (count: number, key: PluralKey, params?: Params) => string;
}

const I18nContext = createContext<I18nValue | null>(null);

export function I18nProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const [lang, setLang] = useState<Language>('it');
  const setLanguage = useCallback((next: Language) => setLang(next), []);

  const value = useMemo<I18nValue>(() => {
    const lookup = (key: TvMessageKey): string => DICTIONARIES[lang][key] ?? it[key] ?? key;
    const t = (key: TvMessageKey, params?: Params) => interpolate(lookup(key), params);
    const tn = (count: number, key: PluralKey, params?: Params) => {
      const variant = `${key}_${count === 1 ? 'one' : 'other'}` as TvMessageKey;
      return interpolate(lookup(variant), { count, ...params });
    };
    return { lang, setLanguage, t, tn };
  }, [lang, setLanguage]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nValue {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error('useI18n must be used within an I18nProvider');
  return ctx;
}
