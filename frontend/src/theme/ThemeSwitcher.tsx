import { useRef, type KeyboardEvent } from 'react';
import { useI18n } from '../i18n';
import { useTheme } from './useTheme';
import { THEME_PREFERENCES, type ThemePreference } from './themePreference';

// Compact segmented choice control for the theme: Dark / Light / System.
//
// Native radio semantics (a real `radiogroup` of `radio`s) rather than a select
// or a row of toggle buttons, so the current selection is announced correctly,
// arrow keys move between options, and the selected state is conveyed by the
// checked state — not by colour alone. Each option additionally carries a
// visible check mark so the selection survives a monochrome / high-contrast
// rendering.

const LABEL_KEY: Record<ThemePreference, 'theme.dark' | 'theme.light' | 'theme.system'> = {
  dark: 'theme.dark',
  light: 'theme.light',
  system: 'theme.system',
};

const ICON: Record<ThemePreference, string> = {
  dark: '◐',
  light: '☀',
  system: '🖥',
};

export function ThemeSwitcher({ className }: { className?: string }) {
  const { t } = useI18n();
  const { preference, setPreference } = useTheme();
  const refs = useRef<(HTMLInputElement | null)[]>([]);

  // Roving arrow navigation. Native radios already do this inside one form, but
  // the explicit handler keeps behaviour identical wherever the group is
  // rendered (popover, settings page) and however the DOM is nested.
  function onKeyDown(e: KeyboardEvent<HTMLInputElement>, index: number) {
    let next = index;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (index + 1) % THEME_PREFERENCES.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
      next = (index - 1 + THEME_PREFERENCES.length) % THEME_PREFERENCES.length;
    } else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = THEME_PREFERENCES.length - 1;
    else return;
    e.preventDefault();
    setPreference(THEME_PREFERENCES[next]);
    refs.current[next]?.focus();
  }

  return (
    <div
      className={className ? `theme-switcher ${className}` : 'theme-switcher'}
      role="radiogroup"
      aria-label={t('theme.label')}
      data-testid="theme-switcher"
    >
      <span className="theme-switcher__label">{t('theme.label')}</span>
      <div className="theme-switcher__options">
        {THEME_PREFERENCES.map((option, i) => {
          const selected = option === preference;
          return (
            <label
              key={option}
              className={`theme-switcher__option${selected ? ' is-selected' : ''}`}
              data-testid={`theme-option-${option}`}
            >
              <input
                ref={(el) => { refs.current[i] = el; }}
                type="radio"
                name="nubarca-theme"
                value={option}
                checked={selected}
                onChange={() => setPreference(option)}
                onKeyDown={(e) => onKeyDown(e, i)}
              />
              <span className="theme-switcher__icon" aria-hidden="true">{ICON[option]}</span>
              <span className="theme-switcher__text">{t(LABEL_KEY[option])}</span>
              {/* Selection is not conveyed by colour alone. */}
              <span className="theme-switcher__check" aria-hidden="true">{selected ? '✓' : ''}</span>
            </label>
          );
        })}
      </div>
    </div>
  );
}
