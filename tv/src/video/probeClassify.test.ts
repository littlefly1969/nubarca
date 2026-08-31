import assert from 'node:assert/strict';
import test from 'node:test';
import { classifyVideoProbe, tvVideoModeFor } from './probeClassify.ts';
import { classifyVideoDelivery } from './videoDelivery.ts';

// The full canonical matrix — the one frontend and mobile also run — lives in
// videoDeliveryParity.test.ts. This file pins what the TV SCREEN does with it.

test('202 means the ladder is being prepared', () => {
  assert.equal(classifyVideoProbe(202, null), 'preparing');
});

test('200 mpegurl master means adaptive HLS', () => {
  assert.equal(classifyVideoProbe(200, 'application/vnd.apple.mpegurl'), 'hls');
  assert.equal(classifyVideoProbe(200, 'Application/VND.Apple.MPEGURL'), 'hls');
  assert.equal(classifyVideoProbe(200, 'application/vnd.apple.mpegurl; charset=utf-8'), 'hls');
});

test('anything else a 200/206 carries is the progressive stream, MIME or not', () => {
  assert.equal(classifyVideoProbe(206, 'video/mp4'), 'direct');
  assert.equal(classifyVideoProbe(200, 'video/quicktime'), 'direct');
  assert.equal(classifyVideoProbe(206, 'application/octet-stream'), 'direct');
  assert.equal(classifyVideoProbe(206, null), 'direct');
  assert.equal(classifyVideoProbe(200, null), 'direct');
});

test('the screen draws every failure the same way, but the VERDICTS stay distinct', () => {
  // One error pill is a presentation choice; collapsing the verdicts behind it
  // is what used to let the three clients disagree about what /video had said.
  for (const status of [401, 403, 404, 408, 425, 429, 500, 503, 418]) {
    assert.equal(classifyVideoProbe(status, null), 'error', String(status));
  }
  assert.equal(classifyVideoDelivery(404, null).kind, 'not-found');
  assert.equal(classifyVideoDelivery(401, null).kind, 'auth-error');
  assert.equal(classifyVideoDelivery(503, null).kind, 'transient-error');
  assert.equal(classifyVideoDelivery(418, null).kind, 'protocol-error');
  assert.equal(tvVideoModeFor({ kind: 'not-found' }), 'error');
});
