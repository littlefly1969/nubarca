// Deterministic seed for the browser-verification suite.
//
// Everything the specs assert is created here, through the product's own public
// API, so the seed exercises the same paths a user would. Users are created by
// the canonical administrative command (`users ensure`) before this runs; see
// scripts/seed.sh.
//
// Media fixtures are GENERATED with ffmpeg rather than committed. Fixed colours,
// sizes and durations make the bytes reproducible, and it keeps ~megabytes of
// binary out of a repository that has no other reason to carry them.

import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import {
  AI_TIMEOUT_MS,
  OTHER_OWNER,
  OWNER,
  SEED,
} from './env';
import {
  type Session,
  health,
  login,
  media,
  mediaItemId,
  post,
  semantic,
  upload,
  waitFor,
} from './api';

const WORK = join(import.meta.dirname, '..', '.state', 'fixtures');

/** A still image of a flat colour. Deterministic for a given colour and size. */
function makePhoto(name: string, colour: string): Uint8Array {
  const out = join(WORK, name);
  execFileSync(
    'ffmpeg',
    ['-y', '-loglevel', 'error', '-f', 'lavfi', '-i', `color=c=${colour}:s=640x480`,
     '-frames:v', '1', out],
    { stdio: ['ignore', 'ignore', 'pipe'] },
  );
  return readFileSync(out);
}

/**
 * A video of a given duration. The duration is the point: the marker strip
 * places markers proportionally along the timeline, so a video with no readable
 * duration would make the proportional-placement assertion meaningless.
 *
 * Two visually distinct halves give the deterministic backend different frames
 * to embed, which is what produces more than one semantic segment.
 */
function makeVideo(name: string, seconds: number): Uint8Array {
  const out = join(WORK, name);
  const half = seconds / 2;
  execFileSync(
    'ffmpeg',
    ['-y', '-loglevel', 'error',
     '-f', 'lavfi', '-i', `color=c=navy:s=320x240:d=${half}:r=10`,
     '-f', 'lavfi', '-i', `color=c=orange:s=320x240:d=${half}:r=10`,
     '-filter_complex', '[0:v][1:v]concat=n=2:v=1:a=0[v]',
     '-map', '[v]', '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
     '-t', String(seconds), out],
    { stdio: ['ignore', 'ignore', 'pipe'] },
  );
  return readFileSync(out);
}

export interface SeedResult {
  readonly owner: Session;
  readonly other: Session;
  readonly albumId: string;
  readonly unassignedPhoto: string;
  readonly assignedPhoto: string;
  readonly unassignedVideo: string;
  readonly assignedVideo: string;
  readonly excludedPhoto: string;
  readonly videoDurationSeconds: number;
}

const VIDEO_SECONDS = 12;

export async function seed(): Promise<SeedResult> {
  rmSync(WORK, { recursive: true, force: true });
  mkdirSync(WORK, { recursive: true });

  await waitFor('the API to become healthy', health, 120_000, 1000);

  const owner = await login(OWNER.email, OWNER.password);
  const other = await login(OTHER_OWNER.email, OTHER_OWNER.password);

  // Seeding expects an empty library. Say so plainly instead of letting the
  // first upload fail with a bare 409 from the duplicate-name check.
  const existing = await media(owner, { limit: 1 });
  if ((existing.items ?? []).length > 0) {
    throw new Error(
      'the E2E database already contains media: seed expects a fresh stack.\n' +
        'Run `npm run stop && npm run start` first, or just `npm run e2e`.',
    );
  }

  // --- media -------------------------------------------------------------
  const unassignedPhoto = await upload(
    owner, `${SEED.organizePhotoPrefix}-1.jpg`, makePhoto('unassigned.jpg', 'red'), 'image/jpeg');
  const assignedPhoto = await upload(
    owner, `${SEED.organizedPhotoPrefix}-1.jpg`, makePhoto('assigned.jpg', 'green'), 'image/jpeg');
  const excludedPhoto = await upload(
    owner, `${SEED.excludedPrefix}-1.jpg`, makePhoto('excluded.jpg', 'gray'), 'image/jpeg');

  const videoBytes = makeVideo('video.mp4', VIDEO_SECONDS);
  const unassignedVideo = await upload(
    owner, `${SEED.videoPrefix}-unassigned.mp4`, videoBytes, 'video/mp4');
  const assignedVideo = await upload(
    owner, `${SEED.videoPrefix}-assigned.mp4`, videoBytes, 'video/mp4');

  // A second owner's media. Nothing of this may ever appear for the first owner.
  await upload(other, `${SEED.otherOwnerPrefix}-1.jpg`, makePhoto('foreign.jpg', 'blue'), 'image/jpeg');

  // --- album and membership ---------------------------------------------
  const album = await post<{ id: string }>(owner, '/api/albums', { name: SEED.photoAlbum });
  const albumId = album.id;
  for (const fileItemId of [assignedPhoto.id, assignedVideo.id]) {
    await post(owner, `/api/albums/${albumId}/items`, { fileItemId });
  }

  // --- excluded scope ----------------------------------------------------
  await post(owner, '/api/media-library/exclude', { fileIds: [excludedPhoto.id] });

  // --- wait for the media pipeline and deterministic AI ------------------
  // Duration comes from ffprobe metadata; segments come from the worker running
  // the deterministic backend. Both are background work, so the seed blocks
  // until they land rather than leaving the specs to race them.
  await waitFor(
    'the seeded video to report a duration',
    async () => {
      const page = await media(owner, { kind: 'video', scope: 'active', limit: 50 });
      const item = (page.items ?? []).find((i) => mediaItemId(i) === unassignedVideo.id);
      return !!item && typeof item.durationSeconds === 'number' && item.durationSeconds > 0;
    },
    AI_TIMEOUT_MS,
  );

  // A video needs MORE THAN ONE marker for the ordering, placement and
  // activation assertions to mean anything. Markers are bestMatch plus
  // additionalMatches, so require at least one additional match with a real
  // timestamp — that is the shape the marker strip renders.
  await waitFor(
    'the deterministic backend to produce multiple semantic video markers',
    async () => {
      const page = await semantic(owner, { q: 'colour', kind: 'video', limit: 20 });
      return (page.items ?? []).some(
        (result) =>
          result.media?.kind === 'video' &&
          result.bestMatch?.representativeMilliseconds !== null &&
          (result.additionalMatches ?? []).some((m) => m.representativeMilliseconds !== null),
      );
    },
    AI_TIMEOUT_MS,
    5000,
  );

  // And a photo must have NO timestamped marker, which is the negative case the
  // "photo result has no marker strip" assertion depends on.
  await waitFor(
    'the seeded photo to appear in semantic results without timestamps',
    async () => {
      const page = await semantic(owner, { q: 'colour', kind: 'image', limit: 20 });
      return (page.items ?? []).some(
        (result) =>
          result.media?.kind === 'image' &&
          result.bestMatch?.representativeMilliseconds === null &&
          (result.additionalMatches ?? []).length === 0,
      );
    },
    AI_TIMEOUT_MS,
    5000,
  );

  return {
    owner,
    other,
    albumId,
    unassignedPhoto: unassignedPhoto.id,
    assignedPhoto: assignedPhoto.id,
    unassignedVideo: unassignedVideo.id,
    assignedVideo: assignedVideo.id,
    excludedPhoto: excludedPhoto.id,
    videoDurationSeconds: VIDEO_SECONDS,
  };
}

// Run directly (scripts/seed.sh) and record the result for the specs to read.
if (process.argv[1] && import.meta.url.endsWith(process.argv[1].split('/').pop() ?? '')) {
  const { writeFileSync } = await import('node:fs');
  const result = await seed();
  const statePath = join(import.meta.dirname, '..', '.state', 'seed.json');
  // Sessions carry cookies; the specs log in through the UI instead, so only
  // identifiers are persisted.
  const { owner: _o, other: _t, ...ids } = result;
  writeFileSync(statePath, JSON.stringify(ids, null, 2) + '\n');
  console.log(`seeded: ${JSON.stringify(ids, null, 2)}`);
}
