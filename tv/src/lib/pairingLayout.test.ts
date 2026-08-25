import assert from 'node:assert/strict';
import test from 'node:test';
import {
  MIN_READABLE_PAIRING_QR,
  pairingLayout,
  TV_PAIRING_VIEWPORTS,
} from './pairingLayout.ts';
import { read } from '../testing/sourceText.ts';

const screen = read(import.meta.url, '../screens/PairingScreen.tsx');

test('pairing lockup and QR fit every supported TV viewport inside overscan', () => {
  for (const viewport of TV_PAIRING_VIEWPORTS) {
    const layout = pairingLayout(viewport);
    assert.ok(layout.contentHeight <= layout.usableHeight,
      `${viewport.width}x${viewport.height}: ${layout.contentHeight} > ${layout.usableHeight}`);
    assert.ok(layout.qrSize >= MIN_READABLE_PAIRING_QR,
      `${viewport.width}x${viewport.height}: QR ${layout.qrSize} is too small for TV pairing`);
    assert.ok(layout.detailsWidth >= 320,
      `${viewport.width}x${viewport.height}: only ${layout.detailsWidth}px for instructions`);
  }
});

test('the density-independent Fire TV viewport is a first-class geometry gate', () => {
  const compact = pairingLayout({ width: 960, height: 540 });
  assert.equal(compact.contentHeight <= compact.usableHeight, true);
  assert.equal(compact.qrSize, 320);
  assert.equal(compact.lockupHeight, 114);
});

test('720p at 2x uses the dense text contract and keeps a readable QR', () => {
  const dense = pairingLayout({ width: 640, height: 360 });
  assert.equal(dense.dense, true);
  assert.ok(dense.qrSize >= MIN_READABLE_PAIRING_QR);
  assert.ok(dense.contentHeight <= dense.usableHeight);
  assert.equal(pairingLayout({ width: 960, height: 540 }).dense, false);
});

test('lockup geometry preserves the approved 1280x297 master ratio', () => {
  for (const viewport of TV_PAIRING_VIEWPORTS) {
    const layout = pairingLayout(viewport);
    assert.ok(Math.abs(layout.lockupWidth / layout.lockupHeight - 1280 / 297) < 0.04);
  }
});

test('PairingScreen uses the measured layout instead of fixed stacked geometry', () => {
  assert.match(screen, /useWindowDimensions\(\)/);
  assert.match(screen, /pairingLayout\(viewport\)/);
  assert.match(screen, /size=\{layout\.qrSize\}/);
  assert.match(screen, /flexDirection: 'row'/);
  assert.doesNotMatch(screen, /size=\{?320\}?/,
    'a fixed QR in a vertical stack is what pushed the lockup off-screen');
});
