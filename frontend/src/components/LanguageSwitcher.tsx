import { useI18n, LANGUAGES, type Language } from '../i18n';

interface LanguageSwitcherProps {
  // When provided, the switcher delegates the change (e.g. persist server-side
  // for an authenticated user) instead of only updating the local provider.
  onSelect?: (lang: Language) => void;
  disabled?: boolean;
  className?: string;
}

const LABEL_KEY: Record<Language, 'language.italian' | 'language.english'> = {
  it: 'language.italian',
  en: 'language.english',
};

// Compact language selector. Public/unauthenticated surfaces use the default
// behavior (updates the i18n provider + localStorage); authenticated surfaces
// pass `onSelect` to also persist the choice to the user's profile — unchanged
// by this slice.
//
// Readability: the native <select> previously inherited its text colour while
// hard-coding a dark background, so in a light theme it rendered dark-on-dark
// and the selected language was invisible. It now takes explicit foreground and
// background tokens plus its own `color-scheme`, which is what makes the popped
// OPTION list readable too (options are drawn by the OS, not by our CSS — the
// declared color-scheme is the only lever we have there).
export function LanguageSwitcher({ onSelect, disabled, className }: LanguageSwitcherProps) {
  const { lang, setLanguage, t } = useI18n();

  const handleChange = (next: Language) => {
    if (next === lang) return;
    if (onSelect) {
      onSelect(next);
    } else {
      setLanguage(next);
    }
  };

  return (
    <label className={className ?? 'language-switcher'}>
      <span className="language-switcher-label">{t('language.label')}</span>
      <select
        aria-label={t('language.label')}
        value={lang}
        disabled={disabled}
        onChange={(e) => handleChange(e.target.value as Language)}
      >
        {LANGUAGES.map((code) => (
          <option key={code} value={code}>
            {t(LABEL_KEY[code])}
          </option>
        ))}
      </select>
    </label>
  );
}
