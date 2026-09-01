// Viewer gesture WIRING contract (MOBILE-FIRST-CLASS-PARITY-01 §3-6, §47).
//
// The mobile harness has no component renderer, so the parts that are wiring
// rather than math are pinned against the source. The MATH itself — clamps,
// bounds, double-tap target, reset, pager ownership — is proven for real in
// media/zoomTransform.test.ts, and this file must never restate it.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const here = dirname(fileURLToPath(import.meta.url));
const cache = new Map<string, string>();

/** Source with comments STRIPPED — see testing/sourceText.ts for why the
 * negative assertions below would otherwise be unfalsifiable. */
async function sourceOf(relativePath: string): Promise<string> {
  const cached = cache.get(relativePath);
  if (cached !== undefined) return cached;
  const text = code(await readFile(join(here, relativePath), 'utf8'));
  cache.set(relativePath, text);
  return text;
}

test('gestures use the standard primitives, not a hand-rolled touch engine', async () => {
  // §6: the previous implementation drove pinch and pan from raw PanResponder
  // touch bookkeeping on the JS thread. That is exactly what the spec asks not
  // to maintain, and it could not keep up with a drag while the pager rendered.
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /from 'react-native-gesture-handler'/);
  assert.match(slide, /from 'react-native-reanimated'/);
  assert.match(slide, /Gesture\.Pinch\(\)/);
  assert.match(slide, /Gesture\.Pan\(\)/);
  assert.match(slide, /Gesture\.Tap\(\)[\s\S]*?\.numberOfTaps\(2\)/);
  assert.doesNotMatch(slide, /PanResponder/);
  assert.doesNotMatch(slide, /twoFingerStartDist|activeTouches/);
});

test('a gesture root wraps the whole app, or Android fires no gesture at all', async () => {
  const layout = code(await readFile(join(here, '../../app/_layout.tsx'), 'utf8'));
  assert.match(layout, /import \{ GestureHandlerRootView \} from 'react-native-gesture-handler'/);
  // Outermost: it must contain the providers, not sit inside them.
  const root = layout.indexOf('<GestureHandlerRootView');
  const safeArea = layout.indexOf('<SafeAreaProvider>');
  assert.ok(root !== -1 && safeArea !== -1);
  assert.ok(root < safeArea, 'GestureHandlerRootView must be the outermost view');
});

test('Reanimated 4 has its worklets babel plugin, or animated styles silently die', async () => {
  const babel = code(await readFile(join(here, '../../babel.config.js'), 'utf8'));
  assert.match(babel, /react-native-worklets\/plugin/);
});

test('the slide owns no bounds of its own: the math comes from zoomTransform', async () => {
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /from '\.\.\/media\/zoomTransform'/);
  assert.match(slide, /fittedSize|maxTranslation/);
  // No second opinion about the limits.
  assert.doesNotMatch(slide, /const MAX_SCALE =|const MIN_SCALE =/);
});

test('pager ownership is wired from the ACTIVE slide to scrollEnabled', async () => {
  // §4, end to end: the slide reports, the pager obeys, and only the focused
  // slide may speak — a zoomed neighbour must not lock paging.
  const viewer = code(await readFile(join(here, '../../app/media/[id].tsx'), 'utf8'));
  assert.match(viewer, /scrollEnabled=\{pagerOwnsHorizontal\}/);
  assert.match(viewer, /onZoomOwnershipChange=\{\s*i === safeIndex \? setPagerOwnsHorizontal : undefined\s*\}/);
  assert.match(viewer, /active=\{i === safeIndex\}/);
});

test('changing item releases the pager, so zoom cannot strand the swipe', async () => {
  const viewer = code(await readFile(join(here, '../../app/media/[id].tsx'), 'utf8'));
  assert.match(viewer, /setPagerOwnsHorizontal\(true\);\s*\n\s*\}, \[index\]\);/);
});

test('the pan gesture is DISABLED at rest, not merely inert', async () => {
  // The regression this pins, found on a physical device: photos could not be
  // swiped at all. A gesture-handler Pan that bails out inside onUpdate has
  // still CLAIMED the touch, so the pager never sees the drag — being inert is
  // not the same as being disabled, and only the second gives the gesture back.
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /Gesture\.Pan\(\)\s*\n\s*\.enabled\(zoomed\)/);
  // And the bail-out that used to stand in for it must not come back.
  const panBlock = slide.slice(slide.indexOf('Gesture.Pan()'), slide.indexOf('Gesture.Tap()'));
  assert.doesNotMatch(panBlock, /if \(scale\.value <= 1\.05\) return;/);
});

test('one place decides both pager ownership and whether the pan is armed', async () => {
  // Two separate sources of truth for "am I zoomed" is how they drift apart.
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /setZoomed\(!pagerOwns\);\s*\n\s*onZoomOwnershipChange\?\.\(pagerOwns\);/);
});

test('crossing the zoom threshold arms the pan mid-pinch', async () => {
  // Without this the user has to lift their fingers and touch again before a
  // freshly zoomed photo can be dragged.
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /const crossed = \(next > 1\.05\) !== \(scale\.value > 1\.05\);/);
  assert.match(slide, /if \(crossed\) runOnJS\(reportOwnership\)\(next <= 1\.05\);/);
});

test('no timeout arbitrates pager vs zoom', async () => {
  // §4 forbids solving this with delays. Double-tap detection is gesture-
  // handler's own arbitration (numberOfTaps + requireExternalGestureToFail),
  // not a hand-rolled timestamp comparison like the one this replaced.
  const slide = await sourceOf('ImageSlide.tsx');
  assert.doesNotMatch(slide, /setTimeout|Date\.now\(\)|lastTap/);
  assert.match(slide, /requireExternalGestureToFail\(doubleTap\)/);
});

test('zoom resets on defocus and on a real geometry change', async () => {
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /if \(!active\) reset\(false\);/);
  assert.match(slide, /if \(geometryChanged\(previousViewport\.current, viewport\)\) reset\(false\);/);
});

test('rotation re-anchoring still owns the SELECTED item, untouched by zoom', async () => {
  // The regression §5 forbids reintroducing: rotating must not change which
  // media item is open. That is the viewer's re-anchor path, and the gesture
  // work must not have disturbed it.
  const viewer = code(await readFile(join(here, '../../app/media/[id].tsx'), 'utf8'));
  assert.match(viewer, /shouldReanchorViewer\(previousWidth, pagerWidth\)/);
  assert.match(viewer, /viewerOffsetForIndex\(safeIndex, pagerWidth, slides\.length\)/);

  // The selection has exactly TWO writers, and the gesture work added neither:
  // the route's own startIndex sync, and the pager's settled-scroll handler.
  // A zoom or ownership path that could write it is what would bring the
  // rotation bug back, so the count is the assertion.
  const writes = viewer.match(/setIndex\(/g) ?? [];
  assert.equal(writes.length, 2, 'unexpected writer of the selected index');
  assert.match(viewer, /setIndex\(startIndex\);/);
  assert.match(viewer, /setIndex\(next\);\s*\n\s*setViewerIndex\(next\);/);

  // And the ownership effect touches ownership only.
  // Anchored on the LAST useEffect before the `[index]` dependency list, so
  // the capture cannot swallow the effects declared above it.
  const ownershipEffect = viewer.match(
    /useEffect\(\(\) => \{((?:(?!useEffect)[\s\S])*?)\}, \[index\]\);/,
  );
  assert.ok(ownershipEffect, 'the per-item ownership reset effect is missing');
  assert.equal(ownershipEffect[1].trim(), 'setPagerOwnsHorizontal(true);');
});

test('the photo still fits with contain: zoom introduced no cropping', async () => {
  const slide = await sourceOf('ImageSlide.tsx');
  assert.match(slide, /resizeMode="contain"/);
});

test('pan bounds need the decoded source size, so it is actually requested', async () => {
  const slide = await sourceOf('ImageSlide.tsx');
  const image = await sourceOf('AuthedImage.tsx');
  assert.match(slide, /onNaturalSize=\{setSource\}/);
  assert.match(image, /onNaturalSize\?: \(size: \{ width: number; height: number \}\) => void/);
  assert.match(image, /event\.nativeEvent\.source/);
});

test('the video lifecycle was not touched by the gesture work', async () => {
  // §3/§54: video playback is working and stays out of scope.
  const video = await sourceOf('VideoSlide.tsx');
  assert.doesNotMatch(video, /react-native-gesture-handler|react-native-reanimated/);
  assert.match(video, /createManagedProbe\(/);
});
