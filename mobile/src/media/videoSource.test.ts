// Authenticated video-source tests. THE security rule: the native player
// source carries the exact session Cookie header, and without a session there
// is no source at all. Removing the Cookie from the source must fail this
// file (slice negative check #2).

import assert from 'node:assert/strict';
import test from 'node:test';
import { configureBaseUrl } from '../api/client.ts';
import {
  setSessionCookieSource,
  staticSessionCookieSource,
} from '../api/sessionAccess.ts';
import {
  buildAuthenticatedSource,
  authenticatedSource,
} from './imageSource.ts';
import { buildVideoSource, videoFileVideoPath } from './videoSource.ts';
import type { VideoMediaItem } from '../api/media.ts';

const COOKIE = `NubArca.Auth=${'v'.repeat(36)}`;

function fakeSession(cookie: string | null) {
  return staticSessionCookieSource(cookie);
}

function videoItem(overrides: Partial<VideoMediaItem> = {}): VideoMediaItem {
  return {
    id: 'file-1',
    kind: 'video',
    name: 'clip.mp4',
    title: null,
    displayName: 'clip.mp4',
    mimeType: 'video/mp4',
    sizeBytes: 1234,
    width: 1920,
    height: 1080,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    takenAt: null,
    favorite: false,
    rating: null,
    thumbnailUrl: '/api/files/file-1/poster',
    occurrenceCount: 1,
    hasDuplicates: false,
    posterUrl: '/api/files/file-1/poster',
    durationSeconds: 90,
    videoCodec: 'h264',
    hasAudio: true,
    posterSource: 'ffmpeg',
    previewStripUrl: null,
    ...overrides,
  };
}

test('builds a Range-video source with the exact session cookie header', () => {
  const src = buildAuthenticatedSource('https://nubarca.example', COOKIE, videoFileVideoPath('file-1'));
  assert.notEqual(src, null);
  assert.equal(src!.uri, 'https://nubarca.example/api/files/file-1/video');
  assert.equal(src!.headers.cookie, COOKIE);
});

test('no session cookie means NO authenticated source', () => {
  assert.equal(buildAuthenticatedSource('https://nubarca.example', null, '/api/files/x/video'), null);
  assert.equal(buildAuthenticatedSource('https://nubarca.example', '', '/api/files/x/video'), null);
});

test('authenticatedSource snapshots the wired runtime session', () => {
  configureBaseUrl('https://nubarca.example');
  setSessionCookieSource(fakeSession(COOKIE));
  const src = authenticatedSource('/api/files/file-1/preview');
  assert.equal(src?.headers.cookie, COOKIE);
  setSessionCookieSource(fakeSession(null));
  assert.equal(authenticatedSource('/api/files/file-1/preview'), null);
});

test('video playback source carries the cookie and metadata', () => {
  setSessionCookieSource(fakeSession(COOKIE));
  configureBaseUrl('https://nubarca.example');
  const playback = buildVideoSource(videoItem());
  assert.notEqual(playback, null);
  assert.equal(playback!.source.uri, 'https://nubarca.example/api/files/file-1/video');
  assert.equal(playback!.source.headers.cookie, COOKIE);
  assert.equal(playback!.metadata.title, 'clip.mp4');
  assert.equal(playback!.metadata.poster, '/api/files/file-1/poster');
});

test('signed out, no video playback source exists', () => {
  setSessionCookieSource(fakeSession(null));
  assert.equal(buildVideoSource(videoItem()), null);
});
