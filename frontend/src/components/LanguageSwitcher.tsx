import { useI18n, LANGUAGES, type Language } from '../i18n';

interface LanguageSwitcherProps {
  // When provided, the switcher delegates the change (e.g. persist server-side
  // for an authenticated user) instead of only updating the local provider.
  onSelect?: (lang: Language) => void;
  disabled?: boolean;
  className?: string;
  /**
   * Compact presentation: a globe, the active language CODE and a chevron,
   * for a surface where "Lingua: Italiano" is more chrome than the design can
   * carry (the party guest hero sits on a photograph).
   *
   * The real <select> is still the control — it is laid over the decoration at
   * full size, transparent — so the native picker, the keyboard behaviour, the
   * accessible name and the full language names in the option list are all
   * exactly what they are everywhere else. Only the painted state is ours.
   */
  compact?: boolean;
}

function GlobeIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <circle cx="12" cy="12" r="8.5" />
      <path d="M3.5 12h17M12 3.5c2.2 2.3 3.3 5.2 3.3 8.5s-1.1 6.2-3.3 8.5c-2.2-2.3-3.3-5.2-3.3-8.5S9.8 5.8 12 3.5Z" />
    </svg>
  );
}

function ChevronDownIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="m7 10 5 5 5-5" />
    </svg>
  );
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
export function LanguageSwitcher({
  onSelect, disabled, className, compact = false,
}: LanguageSwitcherProps) {
  const { lang, setLanguage, t } = useI18n();

  const handleChange = (next: Language) => {
    if (next === lang) return;
    if (onSelect) {
      onSelect(next);
    } else {
      setLanguage(next);
    }
  };

  const select = (
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
  );

  const classes = [className ?? 'language-switcher', compact && 'language-switcher--compact']
    .filter(Boolean).join(' ');

  return (
    <label className={classes}>
      <span className="language-switcher-label">{t('language.label')}</span>
      {compact ? (
        <span className="language-switcher-face">
          <GlobeIcon />
          {/* Decoration: the select underneath carries the real value and name. */}
          <span className="language-switcher-code" aria-hidden="true">
            {lang.toUpperCase()}
          </span>
          <ChevronDownIcon />
          {select}
        </span>
      ) : select}
    </label>
  );
}
