import assert from 'node:assert/strict';
import test from 'node:test';
import type { TvPartyMessage } from '../api/tv.ts';
import {
  beginHeroRotation, heroCandidates, heroEligible, nextHero, onMediaBoundary,
  remapRibbonIndex, ribbonRotating, ribbonVisible, sameMessages,
  HERO_DURATION_MS, HERO_EVERY_N_BOUNDARIES, MESSAGES_POLL_MS, RIBBON_ROTATE_MS,
} from './partyMessages.ts';

function message(over: Partial<TvPartyMessage> = {}): TvPartyMessage {
  return {
    id: 'm1',
    displayName: 'Giulia',
    text: 'Serata fantastica!',
    createdAt: '2026-01-01T20:00:00Z',
    isHero: false,
    heroPromotedAt: null,
    ...over,
  };
}

function hero(id: string, promotedAt: string): TvPartyMessage {
  return message({ id, isHero: true, heroPromotedAt: promotedAt });
}

// ── the numbers the wall is built on ────────────────────────────────────────

test('the timings are the ones the party experience was specified around', () => {
  // Pinned deliberately. Each of these is a product decision, not a tuning
  // constant, and changing one silently changes how the room feels.
  assert.equal(RIBBON_ROTATE_MS, 7_000);
  assert.equal(HERO_DURATION_MS, 6_000);
  assert.equal(HERO_EVERY_N_BOUNDARIES, 10);
  // Faster than the 15s media poll: the whole point is that a guest sees their
  // own message arrive while they are still holding the phone.
  assert.equal(MESSAGES_POLL_MS, 5_000);
});

// ── empty state ─────────────────────────────────────────────────────────────

test('an empty feed shows no band at all, rather than an empty one', () => {
  assert.equal(ribbonVisible({
    partyEnabled: true, messageCount: 0, overlayVisible: false, heroVisible: false,
  }), false);
  // And nothing rotates, so there is no timer running against nothing.
  assert.equal(ribbonRotating({ visible: false, messageCount: 0 }), false);
});

test('a non-party album never shows the band', () => {
  assert.equal(ribbonVisible({
    partyEnabled: false, messageCount: 3, overlayVisible: false, heroVisible: false,
  }), false);
});

// ── the ribbon ──────────────────────────────────────────────────────────────

test('one message is shown and left alone; several rotate', () => {
  assert.equal(ribbonVisible({
    partyEnabled: true, messageCount: 1, overlayVisible: false, heroVisible: false,
  }), true);
  // A single message does not crossfade into itself.
  assert.equal(ribbonRotating({ visible: true, messageCount: 1 }), false);
  assert.equal(ribbonRotating({ visible: true, messageCount: 2 }), true);
});

test('a refresh keeps the band on the message that is being read', () => {
  const before = [message({ id: 'a' }), message({ id: 'b' }), message({ id: 'c' })];
  // Somebody writes a new one while 'b' is on screen. 'b' stays on screen.
  const after = [...before, message({ id: 'd' })];
  assert.equal(remapRibbonIndex(after, 'b', 1), 1);

  // And when the feed is reordered rather than appended, the ID still wins.
  const reordered = [message({ id: 'c' }), message({ id: 'b' }), message({ id: 'a' })];
  assert.equal(remapRibbonIndex(reordered, 'b', 1), 1);
  assert.equal(remapRibbonIndex(reordered, 'a', 0), 2);
});

test('a message removed by moderation drops off the band instead of going stale', () => {
  const after = [message({ id: 'a' }), message({ id: 'c' })];
  // 'b' was hidden by the host mid-rotation. The band moves on rather than
  // holding a message the server has stopped sending.
  const index = remapRibbonIndex(after, 'b', 1);
  assert.equal(index, 1);
  assert.equal(after[index].id, 'c');

  // And an emptied feed cannot leave the index pointing at nothing.
  assert.equal(remapRibbonIndex([], 'b', 1), 0);
});

test('MENU hides the band, and closing MENU brings it back', () => {
  const base = { partyEnabled: true, messageCount: 3, heroVisible: false };
  // The overlay puts QR codes exactly where the band lives.
  assert.equal(ribbonVisible({ ...base, overlayVisible: true }), false);
  assert.equal(ribbonVisible({ ...base, overlayVisible: false }), true);
});

test('the band steps aside for a Hero rather than saying it twice', () => {
  assert.equal(ribbonVisible({
    partyEnabled: true, messageCount: 3, overlayVisible: false, heroVisible: true,
  }), false);
});

test('sameMessages notices a promotion, not only an arrival', () => {
  const before = [message({ id: 'a' })];
  assert.equal(sameMessages(before, [message({ id: 'a' })]), true);
  // A message that became a Hero is a CHANGED feed even though the ids match —
  // otherwise the poll would skip the update and the card would never appear.
  assert.equal(sameMessages(before, [hero('a', '2026-01-01T21:00:00Z')]), false);
  assert.equal(sameMessages(before, [message({ id: 'a' }), message({ id: 'b' })]), false);
  assert.equal(sameMessages(before, [message({ id: 'a', text: 'edited' })]), false);
});

// ── Hero eligibility ────────────────────────────────────────────────────────

test('a Hero appears only in an autoplaying party slideshow', () => {
  const base = {
    partyEnabled: true, slideshowMode: true, playing: true,
    faceFilterActive: false, candidateCount: 1,
  };
  assert.equal(heroEligible(base), true);

  // Someone opened ONE photo from the grid to look at it. No cards over it.
  assert.equal(heroEligible({ ...base, slideshowMode: false }), false);
  // The wall is paused.
  assert.equal(heroEligible({ ...base, playing: false }), false);
  // Not a party album at all.
  assert.equal(heroEligible({ ...base, partyEnabled: false }), false);
  // Nothing has been promoted.
  assert.equal(heroEligible({ ...base, candidateCount: 0 }), false);
});

test('an active face filter suspends Hero cards', () => {
  const base = {
    partyEnabled: true, slideshowMode: true, playing: true, candidateCount: 2,
  };
  // A guest asked to see the photographs they are in. A greeting card is not an
  // answer to that question, so the interruption stops...
  assert.equal(heroEligible({ ...base, faceFilterActive: true }), false);
  // ...and resumes when they leave the filter.
  assert.equal(heroEligible({ ...base, faceFilterActive: false }), true);
});

test('the ribbon keeps running during a face filter even though Heroes do not', () => {
  // Only the full-screen interruption is suspended; the quiet band is not an
  // interruption.
  assert.equal(ribbonVisible({
    partyEnabled: true, messageCount: 2, overlayVisible: false, heroVisible: false,
  }), true);
  assert.equal(heroEligible({
    partyEnabled: true, slideshowMode: true, playing: true,
    faceFilterActive: true, candidateCount: 2,
  }), false);
});

// ── Hero cadence ────────────────────────────────────────────────────────────

test('a Hero falls due after exactly ten media transitions', () => {
  let count = 0;
  for (let i = 1; i < HERO_EVERY_N_BOUNDARIES; i += 1) {
    const outcome = onMediaBoundary({ boundariesSinceHero: count, eligible: true });
    assert.equal(outcome.kind, 'advance', `boundary ${i}`);
    count = outcome.boundariesSinceHero;
    assert.equal(count, i);
  }

  const tenth = onMediaBoundary({ boundariesSinceHero: count, eligible: true });
  assert.equal(tenth.kind, 'hero');
  // The counter resets when the card is SHOWN.
  assert.equal(tenth.boundariesSinceHero, 0);
});

test('an ineligible stretch does not bank up a queue of Heroes', () => {
  // Twenty transitions with nothing promoted (or a face filter running).
  let count = 0;
  for (let i = 0; i < 20; i += 1) {
    const outcome = onMediaBoundary({ boundariesSinceHero: count, eligible: false });
    assert.equal(outcome.kind, 'advance');
    count = outcome.boundariesSinceHero;
  }
  // The moment it becomes eligible, ONE card is due — not twenty.
  const first = onMediaBoundary({ boundariesSinceHero: count, eligible: true });
  assert.equal(first.kind, 'hero');
  const second = onMediaBoundary({ boundariesSinceHero: first.boundariesSinceHero, eligible: true });
  assert.equal(second.kind, 'advance');
});

test('a manual viewer never reaches a Hero, however long it is left open', () => {
  // slideshowMode false means every boundary is ineligible, so the count can
  // climb forever without a card ever being inserted.
  let count = 0;
  for (let i = 0; i < 50; i += 1) {
    const outcome = onMediaBoundary({
      boundariesSinceHero: count,
      eligible: heroEligible({
        partyEnabled: true, slideshowMode: false, playing: true,
        faceFilterActive: false, candidateCount: 3,
      }),
    });
    assert.equal(outcome.kind, 'advance');
    count = outcome.boundariesSinceHero;
  }
});

// ── Hero rotation fairness ──────────────────────────────────────────────────

test('Heroes are ordered by when they were promoted, not by arrival', () => {
  const feed = [
    hero('c', '2026-01-01T22:00:00Z'),
    hero('a', '2026-01-01T20:00:00Z'),
    hero('b', '2026-01-01T21:00:00Z'),
    message({ id: 'plain' }),
  ];
  assert.deepEqual(heroCandidates(feed).map((m) => m.id), ['a', 'b', 'c']);
});

test('several Heroes take turns, and the newest does not monopolise the wall', () => {
  const feed = [
    hero('a', '2026-01-01T20:00:00Z'),
    hero('b', '2026-01-01T21:00:00Z'),
    hero('c', '2026-01-01T22:00:00Z'),
  ];
  let rotation = beginHeroRotation();
  const shown: string[] = [];
  for (let i = 0; i < 7; i += 1) {
    const pick = nextHero(rotation, feed);
    assert.notEqual(pick.message, null);
    shown.push(pick.message!.id);
    rotation = pick.rotation;
  }
  // The whole cycle completes before anything repeats: no starvation.
  assert.deepEqual(shown, ['a', 'b', 'c', 'a', 'b', 'c', 'a']);
});

test('a demoted Hero leaves the rotation without stopping it', () => {
  const full = [
    hero('a', '2026-01-01T20:00:00Z'),
    hero('b', '2026-01-01T21:00:00Z'),
    hero('c', '2026-01-01T22:00:00Z'),
  ];
  let rotation = beginHeroRotation();
  rotation = nextHero(rotation, full).rotation; // showed 'a'
  rotation = nextHero(rotation, full).rotation; // showed 'b'

  // The host demotes 'b' (or hides it) while it is the cursor. The cycle must
  // continue rather than stall on a message that is no longer a candidate.
  const reduced = [full[0], full[2]];
  const pick = nextHero(rotation, reduced);
  assert.notEqual(pick.message, null);
  assert.equal(pick.message!.id, 'a');
  assert.equal(nextHero(pick.rotation, reduced).message!.id, 'c');
});

test('demoting every Hero yields nothing to show and keeps the cursor', () => {
  const feed = [hero('a', '2026-01-01T20:00:00Z')];
  const rotation = nextHero(beginHeroRotation(), feed).rotation;
  const empty = nextHero(rotation, [message({ id: 'a' })]);
  assert.equal(empty.message, null);
  // The cursor survives, so re-promoting mid-party RESUMES the cycle rather
  // than restarting it.
  assert.equal(empty.rotation.lastShownId, 'a');
});

test('a promoted message that is later hidden is not a candidate at all', () => {
  // The server only ever sends visible messages, so a hidden Hero simply is not
  // in the feed — and heroCandidates therefore cannot select it.
  assert.deepEqual(heroCandidates([]).map((m) => m.id), []);
  assert.equal(nextHero(beginHeroRotation(), []).message, null);
});
