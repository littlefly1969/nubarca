import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * NO NEW SURFACE SHIPS IN ONE LANGUAGE.
 *
 * The interface is translated by looking a key up in a catalogue, which means
 * an untranslated string is not a failing test anywhere — it is simply a
 * sentence that never changes when somebody picks another language. This walks
 * the components and fails when one appears outside the two areas that have
 * never been wired to the catalogue at all.
 *
 * <b>The allowlist is debt, written down.</b> Those files predate the
 * catalogue and are English-only end to end — not a few stray labels but every
 * sentence in them, including the ones this detector cannot see. Translating
 * them is its own slice, and listing them here is what keeps that a decision
 * somebody makes rather than something the repository forgets. Nothing may be
 * added to this list to make a new surface pass.
 */

// Files that are English-only end to end and predate the message catalogue.
// Each is a surface, not a stray string: see the docblock above.
const UNTRANSLATED: readonly string[] = [
  // The file browser, which is the Home page. Its toolbar, breadcrumb,
  // selection bar, details panel, destination pickers and dialogs all write
  // their own English.
  'components/Breadcrumb.tsx',
  'components/CreateFolderForm.tsx',
  'components/FolderBrowser.tsx',
  'components/MovePicker.tsx',
  'components/VideoModal.tsx',
  'components/files/DestinationPicker.tsx',
  'components/files/DetailsPanel.tsx',
  'components/files/FileItemCard.tsx',
  'components/files/FileItemRow.tsx',
  'components/files/FilesToolbar.tsx',
  'components/files/MoveToVaultModal.tsx',
  'components/files/SelectionBar.tsx',
  // The administrator's import wizard.
  'pages/AdminImportPage.tsx',
];

// `src/`, resolved from this file rather than from the runner's cwd.
const SRC = dirname(dirname(fileURLToPath(import.meta.url)));

function componentFiles(dir: string): string[] {
  const found: string[] = [];
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      found.push(...componentFiles(full));
      continue;
    }
    if (!name.endsWith('.tsx') || name.includes('.test.')) continue;
    found.push(full);
  }
  return found;
}

/**
 * Words a person reads, as opposed to the many quoted strings in a component
 * that nobody ever sees.
 *
 * Deliberately narrow. It looks at the three attributes a screen reader speaks
 * and at text written directly between tags — the two places an untranslated
 * sentence actually reaches somebody. A string that is a class name, a route,
 * a test id or a union member is not user-facing and is not its business.
 */
function userFacingStrings(source: string): string[] {
  const found: string[] = [];
  for (const raw of source.split('\n')) {
    const line = raw.trim();
    // Comments and the prose in doc blocks are not shipped.
    if (line.startsWith('//') || line.startsWith('*') || line.startsWith('/*')) continue;

    for (const m of raw.matchAll(/\b(?:aria-label|placeholder|title)="([^"]{2,})"/g)) {
      if (isProse(m[1])) found.push(m[1]);
    }
    // Text between tags: `>Ciao<`. Anything with a brace in it is an
    // expression, which is how a translated string is written.
    for (const m of raw.matchAll(/>([^<>{}\n]+)</g)) {
      const text = m[1].trim();
      if (isProse(text)) found.push(text);
    }
  }
  return found;
}

/**
 * Whether a captured string is something a PERSON reads.
 *
 * TypeScript shares its angle brackets with JSX, so a naive reader of
 * `>…<` also finds `Promise<void>`, `Record<string, unknown>` and `x >= 0 &&
 * y`. Rather than parse, this asks what prose looks like: letters, no
 * punctuation that only appears in code, and not a bare identifier.
 *
 * It also lets through the handful of strings that are deliberately the same
 * in every language — an IANA time zone, a CLI command, an example telephone
 * number — because translating those would be a mistranslation.
 */
function isProse(text: string): boolean {
  if (text.length < 3) return false;
  // No letters at all: a separator, an arrow, an example number.
  if (!/[A-Za-zÀ-ÿ]{3,}/.test(text)) return false;
  // Punctuation that belongs to code and never to a sentence.
  if (/[={}|&;`$\\]|=>|::/.test(text)) return false;
  // A call: an identifier with an opening parenthesis against it. Prose that
  // uses brackets puts a space first — "Crescente (passa a decrescente)".
  if (/[A-Za-z_$][\w$.]*\(/.test(text)) return false;
  if (/^&[a-z]+;$/.test(text)) return false;
  // A generic argument list or a type annotation that survived the above.
  if (/^[,;]?\s*[A-Za-z?]+\s*[:,]/.test(text)) return false;
  if (/^(Promise|Record|Readonly|Partial|Array|Map|Set|Loaded|Response)\b/.test(text)) return false;
  // A lone identifier — a union member, a class name, a key.
  if (!/\s/.test(text) && !/^[A-ZÀ-Ý]/.test(text)) return false;
  // Names that are the same word everywhere: a time zone, a CLI verb, a
  // configuration key. Translating one of these would be a mistranslation.
  if (/^[A-Z][a-z]+\/[A-Z]/.test(text)) return false;
  if (/^[a-z]+([ :][a-z-]+){1,4}\*?$/.test(text)) return false;
  if (/^[A-Za-z]+:[A-Za-z:*]+$/.test(text)) return false;
  return true;
}

describe('the interface speaks the catalogue’s languages', () => {
  it('has no user-facing string written directly into a component', () => {
    const offenders: string[] = [];
    for (const file of componentFiles(SRC)) {
      const rel = relative(SRC, file);
      if (UNTRANSLATED.includes(rel)) continue;
      for (const text of userFacingStrings(readFileSync(file, 'utf8'))) {
        offenders.push(`${rel}: ${text}`);
      }
    }

    // A failure here is not a style complaint: it is a sentence that will stay
    // Italian (or English) for a guest who chose Spanish.
    expect(offenders).toEqual([]);
  });

  it('keeps the debt list honest', () => {
    // A file that has been translated must LEAVE this list, or the list stops
    // describing anything and starts excusing whatever is added to it.
    //
    // The test is whether the file ASKS THE CATALOGUE ANYTHING, rather than
    // whether the detector above can still find a string in it: most of the
    // copy in these files sits inside expressions, which is exactly what the
    // detector cannot see and exactly why they are listed by name.
    for (const rel of UNTRANSLATED) {
      const source = readFileSync(join(SRC, rel), 'utf8');
      expect(
        /from '[^']*i18n'/.test(source),
        `${rel} now uses the message catalogue — take it off the list`,
      ).toBe(false);
    }
  });
});
