import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const CHIPS = code(
  readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), 'MediaFilterChips.tsx'), 'utf8'),
);

test('the strip still describes the APPLIED query and nothing else', () => {
  // It renders what was committed, never a draft: there is no local state here
  // and no filter model of its own.
  assert.doesNotMatch(CHIPS, /useState|useReducer|useMediaFilters/);
  assert.match(CHIPS, /chips\.length === 0/);
  assert.match(CHIPS, /<ScrollView[\s\S]*?horizontal/);
});

test('an inert filter stays inert, visibly and audibly', () => {
  assert.match(CHIPS, /inert !== undefined && inert\.includes\(chip\.kind\)/);
  assert.match(CHIPS, /`\$\{label\(chip\)\} — \$\{t\('chips\.inert'\)\}`/);
  // Strike-through carries the meaning without colour.
  assert.match(CHIPS, /textDecorationLine: 'line-through'/);
});

test('removal and clearing still address the same things', () => {
  assert.match(CHIPS, /onRemove\(chip\.kind\)/);
  assert.match(CHIPS, /onPress=\{onClearAll\}/);
});

test('people labels still resolve from ids', () => {
  assert.match(CHIPS, /function personLabel\(/);
  assert.match(CHIPS, /people/);
});

test('applied is accent, inert is a quiet recess, and neither is surfaceMuted', () => {
  assert.match(CHIPS, /backgroundColor: colors\.accentSubtle/);
  assert.match(CHIPS, /borderColor: colors\.accent,/);
  assert.match(CHIPS, /inert: \{ backgroundColor: colors\.surfaceSubtle/);
  assert.doesNotMatch(CHIPS, /surfaceMuted/);
});

test('Clear All is tertiary, and nothing is shouted', () => {
  assert.match(CHIPS, /clearAllText: \{[\s\S]*?color: colors\.textTertiary/);
  assert.doesNotMatch(CHIPS, /textTransform: 'uppercase'/);
});

test('no colour, radius or type of its own', () => {
  assert.doesNotMatch(CHIPS, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/);
  assert.doesNotMatch(CHIPS, /\bradii\.|\btype\.(title|sectionTitle|body|secondary|badge)\b/);
  assert.doesNotMatch(CHIPS, /fontSize: \d|fontWeight: '[67]00'/);
  assert.match(CHIPS, /borderRadius: radius\.pill/);
});
