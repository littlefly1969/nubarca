import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const SHEET = code(
  readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), 'MediaFilterSheet.tsx'), 'utf8'),
);

// The draft/apply behaviour is asserted in mediaFilterWiring.test.ts and is
// unchanged. This file is about the sheet remaining a dense precision tool.

test('a choice is a choice, not a row of primary actions', () => {
  // Selected takes the accent wash and an accent border. A row of filled blue
  // buttons would claim that picking a sort order is the dominant action on
  // the screen — and there would then be nothing left to say Apply with.
  assert.match(SHEET, /optionOn: \{ backgroundColor: colors\.accentSubtle, borderColor: colors\.accent \}/);
  assert.match(SHEET, /optionTextOn: \{ color: colors\.accent \}/);
  assert.doesNotMatch(SHEET, /optionOn: \{ backgroundColor: colors\.accentStrong/);
});

test('radio semantics survive the restyle', () => {
  assert.match(SHEET, /accessibilityRole="radio"/);
  assert.match(SHEET, /accessibilityState=\{\{ selected: on \}\}/);
  // Tapping the selected option still turns it off.
  assert.match(SHEET, /onChange\(on \? null : option\.value\)/);
});

test('the sheet is safe-area aware instead of guessing at a status bar', () => {
  assert.match(SHEET, /useSafeAreaInsets\(\)/);
  assert.match(SHEET, /paddingTop: insets\.top/);
  assert.match(SHEET, /paddingBottom: spacing\.l \+ insets\.bottom/);
  assert.doesNotMatch(SHEET, /paddingTop: 48/);
});

test('inputs and actions are the shared primitives', () => {
  assert.match(SHEET, /<TextField/);
  assert.match(SHEET, /<Button\s+label=\{t\('filters\.apply'\)\}/);
  assert.match(SHEET, /<IconButton accessibilityLabel=\{t\('filters\.close'\)\}/);
  // A raw TextInput here would be a screen re-deciding what a field looks like.
  assert.doesNotMatch(SHEET, /<TextInput\b/);
});

test('groups are separated by rhythm, not by boxes', () => {
  assert.match(SHEET, /body: \{[^}]*gap: spacing\.xl/);
  assert.doesNotMatch(SHEET, /group: \{[^}]*borderWidth/);
  assert.doesNotMatch(SHEET, /BlurView|blurRadius|shadow(Color|Opacity|Radius)|elevation:/);
});

test('labels are sentence case and typography comes from the contract', () => {
  assert.doesNotMatch(SHEET, /textTransform: 'uppercase'/);
  assert.match(SHEET, /groupLabel: \{ \.\.\.typography\.label/);
  assert.match(SHEET, /title: \{ \.\.\.typography\.pageTitle/);
});

test('no colour, deprecated alias or raw type of its own', () => {
  assert.doesNotMatch(SHEET, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/);
  assert.doesNotMatch(SHEET, /\bradii\.|colors\.surfaceMuted/);
  assert.doesNotMatch(SHEET, /fontSize: \d|fontWeight: '[67]00'/);
});
