import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { I18nProvider } from './I18nProvider';
import { useI18n } from './useI18n';

// The pre-mount language bootstrap in index.html duplicates the provider's
// resolution on purpose: it runs before any module loads, so it cannot import
// it. These tests pin the duplication down — the script is extracted from the
// real index.html and executed, then the provider resolves the same inputs, and
// the two must agree. A difference would show up as <html lang> changing under
// the reader at mount.

const here = dirname(fileURLToPath(import.meta.url));
const INDEX_HTML = readFileSync(resolve(here, '../../index.html'), 'utf8');
const STORAGE_KEY = 'nubarca.lang';

function bootstrapSource(): string {
  const scripts = [...INDEX_HTML.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
  const source = scripts.find((s) => s.includes(STORAGE_KEY));
  if (source === undefined) throw new Error('index.html no longer carries the language bootstrap.');
  return source;
}

function runBootstrap(): string {
  document.documentElement.lang = '';
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function(bootstrapSource())();
  return document.documentElement.lang;
}

function Probe() {
  const { lang } = useI18n();
  return <p data-testid="lang">{lang}</p>;
}

/** What the provider settles on for the same inputs, and what it puts on <html>. */
function providerLanguage(): { active: string; html: string } {
  document.documentElement.lang = '';
  render(<I18nProvider><Probe /></I18nProvider>);
  const active = screen.getByTestId('lang').textContent ?? '';
  const html = document.documentElement.lang;
  cleanup();
  return { active, html };
}

interface Inputs {
  query?: string;
  stored?: string;
  languages?: readonly string[] | undefined;
  language?: string;
}

function arrange({ query, stored, languages, language }: Inputs): void {
  window.history.replaceState({}, '', query === undefined ? '/' : `/?lang=${query}`);
  if (stored !== undefined) window.localStorage.setItem(STORAGE_KEY, stored);
  vi.stubGlobal('navigator', { ...window.navigator, languages, language });
}

afterEach(() => {
  cleanup();
  // Unstub FIRST: a test may have replaced localStorage with a throwing stub.
  vi.unstubAllGlobals();
  window.localStorage.clear();
  window.history.replaceState({}, '', '/');
  document.documentElement.lang = '';
});

describe('index.html: the browser is told not to translate', () => {
  it('marks the whole document as not translatable', () => {
    expect(INDEX_HTML).toMatch(/<html\b[^>]*\btranslate="no"/);
    expect(INDEX_HTML).toContain('<meta name="google" content="notranslate" />');
  });

  it('starts from the Italian default, not English', () => {
    expect(INDEX_HTML).toMatch(/<html\b[^>]*\blang="it"/);
  });

  it('sets the language in the head, before the application module loads', () => {
    const source = bootstrapSource();
    const at = INDEX_HTML.indexOf(source);
    expect(at).toBeGreaterThan(-1);
    expect(at).toBeLessThan(INDEX_HTML.indexOf('</head>'));
    expect(at).toBeLessThan(INDEX_HTML.indexOf('<script type="module"'));
  });
});

describe('index.html language bootstrap', () => {
  const cases: Array<{ name: string; inputs: Inputs; expected: string }> = [
    { name: '?lang=it', inputs: { query: 'it', languages: ['en-US'] }, expected: 'it' },
    { name: '?lang=en', inputs: { query: 'en', languages: ['it-IT'] }, expected: 'en' },
    { name: '?lang= beats storage', inputs: { query: 'de', stored: 'es' }, expected: 'de' },
    { name: 'storage beats the browser', inputs: { stored: 'es', languages: ['de-DE'] }, expected: 'es' },
    { name: 'de-DE from the browser', inputs: { languages: ['de-DE'] }, expected: 'de' },
    { name: 'the first SUPPORTED browser language', inputs: { languages: ['fr-FR', 'es-AR'] }, expected: 'es' },
    { name: 'fr-FR alone falls back', inputs: { languages: ['fr-FR'] }, expected: 'it' },
    { name: 'nothing said at all', inputs: { languages: [] }, expected: 'it' },
    { name: 'an unsupported ?lang= is ignored', inputs: { query: 'fr', stored: 'en' }, expected: 'en' },
    { name: 'an unsupported stored value is ignored', inputs: { stored: 'fr', languages: ['de'] }, expected: 'de' },
    // Region tags are an intent read from the BROWSER only; ?lang= and storage
    // take the bare code, exactly as the provider does.
    { name: '?lang=it-IT is not a code', inputs: { query: 'it-IT', languages: ['en-US'] }, expected: 'en' },
    {
      name: 'navigator.language when languages is missing',
      inputs: { languages: undefined, language: 'de-DE' },
      expected: 'de',
    },
  ];

  for (const { name, inputs, expected } of cases) {
    it(`agrees with the provider: ${name}`, () => {
      arrange(inputs);
      const painted = runBootstrap();
      const provider = providerLanguage();

      expect(painted).toBe(expected);
      expect(provider.active).toBe(expected);
      expect(provider.html).toBe(expected);
    });
  }

  it('never writes to storage', () => {
    const setItem = vi.fn();
    vi.stubGlobal('localStorage', { getItem: () => null, setItem, removeItem: vi.fn() });
    arrange({ languages: ['en-GB'] });
    expect(runBootstrap()).toBe('en');
    expect(setItem).not.toHaveBeenCalled();
  });

  it('does not stop the application when storage is blocked', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
    });
    arrange({ languages: ['es-ES'] });
    expect(runBootstrap()).toBe('es');
    // And the application still mounts, on the same answer.
    expect(providerLanguage().active).toBe('es');
  });

  it('falls back to Italian when the navigator itself throws', () => {
    vi.stubGlobal('navigator', {
      get languages(): never { throw new Error('embedded browser'); },
      get language(): never { throw new Error('embedded browser'); },
    });
    expect(runBootstrap()).toBe('it');
  });
});
