import assert from 'node:assert/strict';
import test from 'node:test';
import { classifyVideoProbe } from './probeClassify.ts';

test('202 means the ladder is being prepared', () => {
  assert.equal(classifyVideoProbe(202, null), 'preparing');
});

test('200 mpegurl master means adaptive HLS', () => {
  assert.equal(classifyVideoProbe(200, 'application/vnd.apple.mpegurl'), 'hls');
  assert.equal(classifyVideoProbe(200, 'Application/VND.Apple.MPEGURL'), 'hls');
});

test('206/200 video bytes mean the legacy direct stream', () => {
  assert.equal(classifyVideoProbe(206, 'video/mp4'), 'direct');
  assert.equal(classifyVideoProbe(200, 'video/quicktime'), 'direct');
  assert.equal(classifyVideoProbe(206, null), 'direct');
});

test('auth failures and missing files are errors', () => {
  assert.equal(classifyVideoProbe(401, null), 'error');
  assert.equal(classifyVideoProbe(404, 'application/json'), 'error');
  assert.equal(classifyVideoProbe(500, null), 'error');
});
