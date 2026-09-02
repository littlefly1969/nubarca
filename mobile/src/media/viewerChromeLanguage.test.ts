import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const VIEWER = code(readFileSync(resolve(ROOT, 'app', 'media', '[id].tsx'), 'utf8'));

// BRAND-APP-03 §F touches the CHROME. The viewer's mechanics — pager maths,
// re-anchoring, gesture and video ownership, cleanup — are asserted in
// viewerRouteLifecycle.test.ts and videoSlideLifecycle.test.ts and did not
// move. What follows is the chrome, plus a restatement of the boundaries this
// commit was not allowed to cross.

test('the viewer is dark in both themes, and takes no theme palette', () => {
  // The only colours over an unknown photograph are the media ones; a themed
  // surface here would make the chrome light for half the users.
  assert.match(VIEWER, /const styles = StyleSheet\.create/);
  assert.doesNotMatch(VIEWER, /themed\(|useColors\(/);
  assert.doesNotMatch(VIEWER, /colors\.\w/);
});

test('the chrome sits on the real inset, not a guessed status bar', () => {
  assert.match(VIEWER, /useSafeAreaInsets\(\)/);
  assert.match(VIEWER, /paddingTop: insets\.top \+ spacing\.s/);
  assert.doesNotMatch(VIEWER, /paddingTop: spacing\.xl \+ spacing\.l/);
});

test('the title and counter use brand roles, and the counter does not shuffle', () => {
  assert.match(VIEWER, /title: \{ \.\.\.typography\.label/);
  assert.match(VIEWER, /counter: \{\s*\.\.\.typography\.badge/);
  assert.match(VIEWER, /fontVariant: \['tabular-nums'\]/);
  assert.doesNotMatch(VIEWER, /fontSize: \d|fontWeight: '[67]00'/);
});

test('back is a full touch target, and nothing up here is ornamental', () => {
  assert.match(VIEWER, /backBtn: \{\s*width: touch\.minSize,\s*height: touch\.minSize/);
  // One quiet translucent region; no accent ornament over the media.
  assert.match(VIEWER, /chromeTop: \{[\s\S]*?backgroundColor: media\.chrome/);
  assert.doesNotMatch(VIEWER, /accentStrong|signalConnected|signalIntelligence/);
});

test('the mechanics this commit must not touch are still here', () => {
  assert.match(VIEWER, /getItemLayout/);
  assert.match(VIEWER, /onContentSizeChange/);
  assert.match(VIEWER, /applySystemBars/);
  assert.match(VIEWER, /systemBarsFor/);
  assert.match(VIEWER, /forgetAllPositions/);
  assert.match(VIEWER, /<Redirect/);
});
