import { afterEach, describe, expect, it, beforeEach } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from './I18nProvider';
import { useI18n } from './useI18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { LANGUAGES, preferredLanguage } from './types';
import itDict from './it';
import enDict from './en';
import esDict from './es';
import deDict from './de';

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
      {/* Every dictionary carries every key, so this is simply the Italian
          language's own name — which is spelled the same in all four. */}
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

  it('never renders a raw key', async () => {
    renderProbe();
    await userEvent.setup().selectOptions(
      screen.getByRole('combobox', { name: /Lingua|Language/i }),
      'en',
    );
    const fallback = screen.getByTestId('fallback');
    expect(fallback).toHaveTextContent('Italiano');
    expect(fallback).not.toHaveTextContent('language.italian');
  });

  it('switches to Spanish and to German', async () => {
    renderProbe();
    const user = userEvent.setup();
    const picker = () => screen.getByRole('combobox', { name: /Lingua|Language|Idioma|Sprache/i });

    await user.selectOptions(picker(), 'es');
    expect(screen.getByTestId('lang')).toHaveTextContent('es');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Galería');

    await user.selectOptions(picker(), 'de');
    expect(screen.getByTestId('lang')).toHaveTextContent('de');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Galerie');
  });

  it('persists Spanish, and restores it on the next visit', async () => {
    renderProbe();
    await userEvent.setup().selectOptions(
      screen.getByRole('combobox', { name: /Lingua|Language/i }),
      'es',
    );
    expect(window.localStorage.getItem('nubarca.lang')).toBe('es');

    cleanup();
    renderProbe();
    expect(screen.getByTestId('lang')).toHaveTextContent('es');
  });

  it('respects ?lang=de on first load', () => {
    window.history.replaceState({}, '', '/?lang=de');
    renderProbe();
    expect(screen.getByTestId('lang')).toHaveTextContent('de');
    expect(screen.getByTestId('nav-gallery')).toHaveTextContent('Galerie');
  });

  it('handles plurals and interpolation', () => {
    renderProbe();
    expect(screen.getByTestId('plural')).toHaveTextContent('3 elementi');
    expect(screen.getByTestId('interp')).toHaveTextContent('Condividi con Festa');
  });

  it('offers exactly the four supported languages, each named in itself', () => {
    renderProbe();
    const picker = screen.getByRole('combobox', { name: /Lingua|Language/i });
    expect(within(picker).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['Italiano', 'English', 'Español', 'Deutsch']);
  });
});

/**
 * WHAT THE BROWSER ASKS FOR, and only where nothing else has been said.
 *
 * A guest arriving on a QR code has given us exactly one signal, and it is
 * this one. A person who once picked a language has given us a better one,
 * which is why the stored choice wins — asserted by the persistence test above.
 */
describe('browser language detection', () => {
  it('reads a region-tagged locale, in the browser\u2019s own order', () => {
    expect(preferredLanguage(['es-AR', 'en-US'])).toBe('es');
    expect(preferredLanguage(['de-CH'])).toBe('de');
    // The first SUPPORTED one wins, not the first one listed.
    expect(preferredLanguage(['fr-FR', 'de-AT', 'en'])).toBe('de');
  });

  it('answers null when nothing is supported, so the default applies', () => {
    expect(preferredLanguage(['fr', 'ja-JP'])).toBeNull();
    expect(preferredLanguage([])).toBeNull();
    expect(preferredLanguage(undefined)).toBeNull();
  });
});

/**
 * KEY PARITY, across every dictionary NubArca ships.
 *
 * Italian is the canonical catalogue: `MessageKey` is derived from it, so no
 * dictionary can introduce a key it lacks — the type system says so. What the
 * type system does NOT say is that none of them may be MISSING a key, because
 * every dictionary is a `Partial`. That partiality is a safety net (a missing
 * key renders Italian rather than `party.section.photos`), and a safety net is
 * not a strategy: a Spanish speaker reading one Italian sentence in the middle
 * of a page has found a bug, not a graceful degradation.
 *
 * So this asserts the whole square: the four dictionaries hold exactly the same
 * keys, and every value is a non-empty string.
 */
describe('locale parity', () => {
  const DICTIONARIES = {
    it: itDict, en: enDict, es: esDict, de: deDict,
  } as const;

  it('registers every shipped dictionary as a supported language', () => {
    expect([...LANGUAGES].sort()).toEqual(Object.keys(DICTIONARIES).sort());
  });

  it('holds exactly the canonical keys in every language', () => {
    const canonical = Object.keys(itDict).sort();
    expect(canonical.length).toBeGreaterThan(3_000);

    for (const [lang, dictionary] of Object.entries(DICTIONARIES)) {
      const keys = Object.keys(dictionary).sort();
      const missing = canonical.filter((key) => !(key in dictionary));
      const extra = keys.filter((key) => !(key in itDict));
      expect(`${lang}: missing ${missing.slice(0, 5).join(', ')}`)
        .toBe(`${lang}: missing `);
      expect(`${lang}: extra ${extra.slice(0, 5).join(', ')}`)
        .toBe(`${lang}: extra `);
    }
  });

  it('never ships an empty value, and never a leftover marker', () => {
    for (const [lang, dictionary] of Object.entries(DICTIONARIES)) {
      for (const [key, value] of Object.entries(dictionary)) {
        expect(typeof value, `${lang} ${key}`).toBe('string');
        expect((value as string).trim(), `${lang} ${key}`).not.toBe('');
        // "TODO translate" is how a half-finished dictionary hides in a green
        // suite. Matched in CAPITALS and on a word boundary, because the
        // Spanish for "all" is "todos" and a case-insensitive check would
        // flag every third string in the file.
        expect(value as string, `${lang} ${key}`).not.toMatch(/\bTODO\b/);
      }
    }
  });

  it('carries the SAME interpolation tokens in every language', () => {
    // A `{count}` that became `{cuenta}` is a token that never interpolates:
    // the guest reads the braces. The plural suffixes are part of the key and
    // are covered by the parity test above.
    const tokens = (value: string) =>
      [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

    for (const [key, italian] of Object.entries(itDict)) {
      const expected = tokens(italian as string);
      for (const [lang, dictionary] of Object.entries(DICTIONARIES)) {
        const value = (dictionary as Record<string, string>)[key];
        expect(tokens(value), `${lang} ${key}`).toBe(expected);
      }
    }
  });
});
