import { useContext } from 'react';
import { I18nContext, type I18nContextValue } from './I18nContext';

// Access the active language, the translate helpers (t / tn), and locale-aware
// formatters. Throws if used outside <I18nProvider> so a missing provider is a
// loud, early failure rather than silent English.
export function useI18n(): I18nContextValue {
  const ctx = useContext(I18nContext);
  if (!ctx) {
    throw new Error('useI18n must be used within an I18nProvider');
  }
  return ctx;
}
