import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from '../i18n';
import { ThemeProvider } from './ThemeProvider';
import { ThemeSwitcher } from './ThemeSwitcher';
import { useTheme } from './useTheme';
import { THEME_STORAGE_KEY } from './themePreference';

// Registered media-query listeners, so a test can simulate an OS theme change.
let listeners: Array<(e: { matches: boolean }) => void> = [];

function stubMatchMedia(prefersLight: boolean) {
  listeners = [];
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(prefers-color-scheme: light)' ? prefersLight : false,
    media: query,
    addEventListener: (_type: string, cb: (e: { matches: boolean }) => void) => { listeners.push(cb); },
    removeEventListener: (_type: string, cb: (e: { matches: boolean }) => void) => {
      listeners = listeners.filter((l) => l !== cb);
    },
  }));
}

function Probe() {
  const { preference, effective } = useTheme();
  return <span data-testid="probe">{`${preference}/${effective}`}</span>;
}

function renderTheme() {
  return render(
    <I18nProvider>
      <ThemeProvider>
        <Probe />
        <ThemeSwitcher />
      </ThemeProvider>
    </I18nProvider>,
  );
}

afterEach(() => {
  cleanup();
  window.localStorage.clear();
  vi.unstubAllGlobals();
  delete document.documentElement.dataset.theme;
});

describe('ThemeProvider', () => {
  it('uses dark as the first-run default and stamps it on <html>', () => {
    stubMatchMedia(false);
    renderTheme();
    expect(screen.getByTestId('probe')).toHaveTextContent('dark/dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
    // No preference was invented in storage just by rendering.
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('adopts a stored light preference without consulting the OS', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light');
    stubMatchMedia(false); // OS says dark; the explicit choice wins.
    renderTheme();
    expect(screen.getByTestId('probe')).toHaveTextContent('light/light');
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('resolves a stored system preference from the OS signal', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'system');
    stubMatchMedia(true);
    renderTheme();
    expect(screen.getByTestId('probe')).toHaveTextContent('system/light');
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('persists an explicit choice and repaints', async () => {
    stubMatchMedia(false);
    renderTheme();
    const user = userEvent.setup();

    await user.click(screen.getByTestId('theme-option-light'));
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(screen.getByTestId('probe')).toHaveTextContent('light/light');

    await user.click(screen.getByTestId('theme-option-dark'));
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  it('follows a live OS change while the preference is system', async () => {
    stubMatchMedia(false);
    renderTheme();
    await userEvent.setup().click(screen.getByTestId('theme-option-system'));
    expect(document.documentElement.dataset.theme).toBe('dark');

    // The OS switches to light.
    act(() => { listeners.forEach((l) => l({ matches: true })); });
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(screen.getByTestId('probe')).toHaveTextContent('system/light');

    // …and back.
    act(() => { listeners.forEach((l) => l({ matches: false })); });
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  it('ignores OS changes once an explicit preference is chosen', async () => {
    stubMatchMedia(false);
    renderTheme();
    await userEvent.setup().click(screen.getByTestId('theme-option-dark'));

    act(() => { listeners.forEach((l) => l({ matches: true })); });
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByTestId('probe')).toHaveTextContent('dark/dark');
  });

  it('does not change the theme already painted by the bootstrap on mount', () => {
    // The bootstrap paints from the same stored preference; mounting must be a
    // no-op rather than a visible flip.
    window.localStorage.setItem(THEME_STORAGE_KEY, 'light');
    document.documentElement.dataset.theme = 'light';
    stubMatchMedia(false);

    const seen: string[] = [];
    const observer = new MutationObserver(() => {
      seen.push(document.documentElement.dataset.theme ?? '');
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    renderTheme();
    observer.disconnect();

    // Whatever writes happened, the value never left 'light'.
    expect(new Set(seen.filter((v) => v !== 'light')).size).toBe(0);
    expect(document.documentElement.dataset.theme).toBe('light');
  });
});

describe('ThemeSwitcher', () => {
  it('exposes an accessible radio group with the three explicit labels', () => {
    stubMatchMedia(false);
    renderTheme();
    const group = screen.getByRole('radiogroup', { name: 'Tema' });
    expect(group).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /Scuro/ })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /Chiaro/ })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /Sistema/ })).toBeInTheDocument();
  });

  it('marks the current selection as checked, not by colour alone', () => {
    stubMatchMedia(false);
    renderTheme();
    expect(screen.getByRole('radio', { name: /Scuro/ })).toBeChecked();
    expect(screen.getByRole('radio', { name: /Chiaro/ })).not.toBeChecked();
    expect(screen.getByTestId('theme-option-dark').className).toContain('is-selected');
  });

  it('moves the selection with arrow keys', async () => {
    stubMatchMedia(false);
    renderTheme();
    const user = userEvent.setup();
    const dark = screen.getByRole('radio', { name: /Scuro/ });
    dark.focus();

    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('radio', { name: /Chiaro/ })).toBeChecked();
    expect(document.documentElement.dataset.theme).toBe('light');

    await user.keyboard('{End}');
    expect(screen.getByRole('radio', { name: /Sistema/ })).toBeChecked();
  });
});
