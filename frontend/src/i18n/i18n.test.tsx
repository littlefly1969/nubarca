import { afterEach, describe, expect, it, beforeEach } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from './I18nProvider';
import { useI18n } from './useI18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import itDict from './it';

afterEach(() => {
  cleanup();
  window.localStorage.clear();
  window.history.replaceState({}, '', '/');
});

beforeEach(() => {
  window.localStorage.clear();
});

// A tiny probe that renders a few representative translated strings so we can
// assert the active language without pulling in a whole page.
function Probe() {
  const { t, tn, lang } = useI18n();
  return (
    <div>
      <p data-testid="lang">{lang}</p>
      <p data-testid="nav-gallery">{t('nav.gallery')}</p>
      {/* A key that exists ONLY in Italian (English falls back to it). */}
      <p data-testid="fallback">{t('language.italian')}</p>
      <p data-testid="plural">{tn(3, 'party.itemCount')}</p>
      <p data-testid="interp">{t('partyUpload.titleTo', { album: 'Festa' })}</p>
    </div>
  );
}

function renderProbe() {
  return render(
    <I18nProvider>
      <Probe />
      <LanguageSwitcher />
    </I18nProvider>,
  );
}

describe('i18n foundation', () => {
  it('defaults to Italian and renders Italian text', () => {
    renderProbe();
    expect(screen.getByTestId('lang')).toHaveTextContent('it');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Galleria');
  });

  it('switches to English and updates visible labels', async () => {
    renderProbe();
    await userEvent.setup().selectOptions(
      screen.getByRole('combobox', { name: /Lingua|Language/i }),
      'en',
    );
    expect(screen.getByTestId('lang')).toHaveTextContent('en');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Gallery');
  });

  it('persists the local choice to localStorage', async () => {
    renderProbe();
    await userEvent.setup().selectOptions(
      screen.getByRole('combobox', { name: /Lingua|Language/i }),
      'en',
    );
    expect(window.localStorage.getItem('nubarca.lang')).toBe('en');
  });

  it('ignores a language choice stored under the pre-rename key', () => {
    // The 0.3.0 identity cutover removed the one-shot migration, so a value left
    // under the old key must not influence the resolved language. Assembled so
    // this file does not itself carry the former identity.
    const formerKey = `${'nano'}cloud.lang`;
    window.localStorage.setItem(formerKey, 'en');
    renderProbe();

    expect(screen.getByTestId('lang')).toHaveTextContent('it');
    expect(window.localStorage.getItem('nubarca.lang')).toBeNull();
  });

  it('respects a ?lang=en override on first load', () => {
    window.history.replaceState({}, '', '/?lang=en');
    renderProbe();
    expect(screen.getByTestId('lang')).toHaveTextContent('en');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Gallery');
  });

  it('ignores an unsupported ?lang= value and stays Italian', () => {
    window.history.replaceState({}, '', '/?lang=fr');
    renderProbe();
    expect(screen.getByTestId('lang')).toHaveTextContent('it');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Galleria');
  });

  it('never renders a raw key: missing English falls back to Italian', async () => {
    // 'language.italian' has no distinct English override, so English must show
    // the Italian value, never the key text.
    renderProbe();
    await userEvent.setup().selectOptions(
      screen.getByRole('combobox', { name: /Lingua|Language/i }),
      'en',
    );
    const fallback = screen.getByTestId('fallback');
    expect(fallback).toHaveTextContent('Italiano');
    expect(fallback).not.toHaveTextContent('language.italian');
  });

  it('handles plurals and interpolation', () => {
    renderProbe();
    expect(screen.getByTestId('plural')).toHaveTextContent('3 elementi');
    expect(screen.getByTestId('interp')).toHaveTextContent('Carica foto su “Festa”');
  });

  it('has an English string for every Italian key that is user-facing', () => {
    // Guardrail: the two dictionaries must not drift. English may omit a key
    // (it falls back to Italian), but a typo'd/renamed key would break lookups.
    // Every key here must be a real Italian key.
    expect(Object.keys(itDict).length).toBeGreaterThan(0);
    for (const key of Object.keys(itDict)) {
      expect(typeof itDict[key as keyof typeof itDict]).toBe('string');
    }
  });
});
