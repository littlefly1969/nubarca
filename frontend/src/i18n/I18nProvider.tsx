import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { DEFAULT_LANGUAGE, LOCALE, toLanguage, type Language } from './types';
import it, { type MessageKey } from './it';
import en from './en';
import { I18nContext, type I18nContextValue, type PluralKey, type TranslateParams } from './I18nContext';
import { readStoredItem } from '../storage/brandedStorageKey';

const STORAGE_KEY = 'nubarca.lang';

const DICTIONARIES: Record<Language, Partial<Record<MessageKey, string>>> = {
  it,
  en,
};

// Fill {placeholder} tokens from params. Missing params are left as-is so a
// mistake surfaces visibly in dev rather than throwing at runtime.
function interpolate(template: string, params?: TranslateParams): string {
  if (!params) return template;
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : match,
  );
}

// Resolve the initial language once, at mount, from (in priority order):
// 1. an explicit ?lang= querystring override (public deep links / QR),
// 2. a previously persisted localStorage choice,
// 3. the Italian default.
// The authenticated user's server preference is applied afterwards by the
// AuthProvider bridge (which calls setLanguage on session resolve).
function resolveInitialLanguage(): Language {
  if (typeof window !== 'undefined') {
    const fromQuery = toLanguage(new URLSearchParams(window.location.search).get('lang'));
    if (fromQuery) return fromQuery;
    // readStoredItem swallows a blocked-storage failure and returns null.
    const stored = toLanguage(readStoredItem(STORAGE_KEY));
    if (stored) return stored;
  }
  return DEFAULT_LANGUAGE;
}

export function I18nProvider({ children }: { children: ReactNode }) {
  const [lang, setLang] = useState<Language>(resolveInitialLanguage);

  // Reflect the active language on <html lang> for accessibility / correct
  // hyphenation, and keep it in sync on every change.
  useEffect(() => {
    if (typeof document !== 'undefined') {
      document.documentElement.lang = lang;
    }
  }, [lang]);

  const setLanguage = useCallback((next: Language, options?: { persistLocal?: boolean }) => {
    setLang(next);
    if (options?.persistLocal !== false && typeof window !== 'undefined') {
      try {
        window.localStorage.setItem(STORAGE_KEY, next);
      } catch {
        // Ignore storage failures — the in-memory choice still applies.
      }
    }
  }, []);

  const value = useMemo<I18nContextValue>(() => {
    const lookup = (key: MessageKey): string =>
      DICTIONARIES[lang][key] ?? it[key] ?? key;

    const t = (key: MessageKey, params?: TranslateParams) => interpolate(lookup(key), params);

    const tn = (count: number, key: PluralKey, params?: TranslateParams) => {
      const variant = `${key}_${count === 1 ? 'one' : 'other'}` as MessageKey;
      return interpolate(lookup(variant), { count, ...params });
    };

    const formatDate = (v: string | number | Date, options?: Intl.DateTimeFormatOptions) => {
      const date = v instanceof Date ? v : new Date(v);
      if (Number.isNaN(date.getTime())) return '';
      return new Intl.DateTimeFormat(
        LOCALE[lang],
        options ?? { dateStyle: 'medium', timeStyle: 'short' },
      ).format(date);
    };

    const formatNumber = (v: number, options?: Intl.NumberFormatOptions) =>
      new Intl.NumberFormat(LOCALE[lang], options).format(v);

    return { lang, setLanguage, t, tn, formatDate, formatNumber };
  }, [lang, setLanguage]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}
