import assert from 'node:assert/strict';
import test from 'node:test';
import {
  flowEffects,
  initialFlowState,
  tvFlowReducer,
  type TvFlowEvent,
  type TvFlowState,
} from './flow.ts';

const home = { displayName: 'Owner', galleryAvailable: true };
const mode: TvFlowState = { name: 'mode', notice: null };
const pairing: TvFlowState = { name: 'pairing', incomplete: false };

function run(events: TvFlowEvent[], from: TvFlowState = initialFlowState): TvFlowState {
  return events.reduce(tvFlowReducer, from);
}

test('paired startup always lands on mode selection, never a remembered mode', () => {
  assert.deepEqual(run([{ type: 'SESSION_READY' }]), mode);
  // Completing a pairing also lands on mode selection.
  assert.deepEqual(
    run([{ type: 'SESSION_INVALID' }, { type: 'SESSION_READY' }]),
    mode,
  );
});

test('Party opens without a PIN and BACK from its root returns to mode selection', () => {
  const party = run([{ type: 'SESSION_READY' }, { type: 'CHOOSE_PARTY' }]);
  assert.deepEqual(party, { name: 'party' });
  assert.deepEqual(tvFlowReducer(party, { type: 'PARTY_EXIT' }), mode);
});

test('Personal area opens PIN entry; BACK with no digits returns to mode selection', () => {
  const pin = run([{ type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }]);
  assert.deepEqual(pin, { name: 'pin', target: 'personal' });
  assert.deepEqual(tvFlowReducer(pin, { type: 'PIN_CANCELLED' }), mode);
});

test('a server-verified unlock is the ONLY way into a personal screen', () => {
  // UNLOCKED only works from pin entry.
  const nonPin: TvFlowState[] = [{ name: 'loading' }, pairing, mode, { name: 'party' }];
  for (const state of nonPin) {
    assert.deepEqual(tvFlowReducer(state, { type: 'UNLOCKED', home }), state);
  }
  const unlocked = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  assert.deepEqual(unlocked, { name: 'personalHome', home });

  // OPEN_GALLERY does nothing while locked.
  assert.deepEqual(tvFlowReducer(mode, { type: 'OPEN_GALLERY' }), mode);
});

test('gallery shell: BACK returns to personal home; BACK from home locks to mode selection', () => {
  const homeState = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  const gallery = tvFlowReducer(homeState, { type: 'OPEN_GALLERY' });
  assert.equal(gallery.name, 'personalGallery');
  assert.deepEqual(tvFlowReducer(gallery, { type: 'GALLERY_BACK' }), homeState);

  const locked = tvFlowReducer(homeState, { type: 'LOCK' });
  assert.deepEqual(locked, mode);
  // Re-entering requires the PIN again: only CHOOSE_PERSONAL → pin is offered.
  assert.deepEqual(tvFlowReducer(locked, { type: 'OPEN_GALLERY' }), locked);
  assert.deepEqual(tvFlowReducer(locked, { type: 'CHOOSE_PERSONAL' }), { name: 'pin', target: 'personal' });
});

test('video library is grant-gated and BACK returns to personal home', () => {
  const homeState = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  const videos = tvFlowReducer(homeState, { type: 'OPEN_VIDEOS' });
  assert.deepEqual(videos, { name: 'personalVideos', home });
  assert.deepEqual(tvFlowReducer(videos, { type: 'VIDEOS_BACK' }), homeState);
  assert.deepEqual(tvFlowReducer(mode, { type: 'OPEN_VIDEOS' }), mode);
  assert.equal(flowEffects(videos, { type: 'LOCK' }).revokeGrant, true);
});

test('a PIN change locks to mode selection with the notice — never back to pairing', () => {
  for (const from of [
    { name: 'personalHome', home } as TvFlowState,
    { name: 'personalGallery', home } as TvFlowState,
    { name: 'personalVideos', home } as TvFlowState,
  ]) {
    const evicted = tvFlowReducer(from, { type: 'LOCK', reason: 'pinChanged' });
    assert.deepEqual(evicted, { name: 'mode', notice: 'pinChanged' });
    // Party stays reachable and clears the notice; Personal re-asks the PIN.
    assert.deepEqual(tvFlowReducer(evicted, { type: 'CHOOSE_PARTY' }), { name: 'party' });
    assert.deepEqual(tvFlowReducer(evicted, { type: 'CHOOSE_PERSONAL' }), { name: 'pin', target: 'personal' });
  }
  // The pin-change lock revokes the grant server-side too.
  assert.equal(
    flowEffects({ name: 'personalHome', home }, { type: 'LOCK', reason: 'pinChanged' })
      .revokeGrant,
    true,
  );
});

test('a gallery without the capability flag cannot be opened', () => {
  const noGallery = { displayName: 'Owner', galleryAvailable: false };
  const state = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' },
    { type: 'UNLOCKED', home: noGallery },
  ]);
  assert.deepEqual(tvFlowReducer(state, { type: 'OPEN_GALLERY' }), state);
});

test('pairing revocation tears down EVERY state, personal or not', () => {
  const states: TvFlowState[] = [
    { name: 'loading' },
    mode,
    { name: 'party' },
    { name: 'pin', target: 'personal' },
    { name: 'personalHome', home },
    { name: 'personalGallery', home },
    { name: 'personalVideos', home },
  ];
  for (const state of states) {
    assert.deepEqual(tvFlowReducer(state, { type: 'SESSION_INVALID' }), pairing);
    const effects = flowEffects(state, { type: 'SESSION_INVALID' });
    assert.equal(effects.dropGrant, true);
    assert.equal(effects.clearSession, true);
  }
});

test('an incomplete association (owner without PIN) exits every paired state to pairing', () => {
  const pairedStates: TvFlowState[] = [
    mode,
    { name: 'party' },
    { name: 'pin', target: 'personal' },
    { name: 'personalHome', home },
    { name: 'personalGallery', home },
    { name: 'personalVideos', home },
  ];
  for (const state of pairedStates) {
    const next = tvFlowReducer(state, { type: 'ASSOCIATION_INCOMPLETE' });
    assert.deepEqual(next, { name: 'pairing', incomplete: true });
    const effects = flowEffects(state, { type: 'ASSOCIATION_INCOMPLETE' });
    assert.equal(effects.dropGrant, true);
    assert.equal(effects.clearSession, true);
  }
  // Not applicable before a session exists.
  assert.deepEqual(
    tvFlowReducer({ name: 'loading' }, { type: 'ASSOCIATION_INCOMPLETE' }),
    { name: 'loading' },
  );
  // Re-pairing from the incomplete state works normally.
  assert.deepEqual(
    tvFlowReducer({ name: 'pairing', incomplete: true }, { type: 'SESSION_READY' }),
    mode,
  );
});

test('LOCK revokes the grant server-side only when leaving a personal screen', () => {
  assert.equal(
    flowEffects({ name: 'personalHome', home }, { type: 'LOCK' }).revokeGrant, true);
  assert.equal(
    flowEffects({ name: 'personalGallery', home }, { type: 'LOCK' }).revokeGrant, true);
  assert.equal(
    flowEffects({ name: 'personalVideos', home }, { type: 'LOCK' }).revokeGrant, true);
  assert.equal(flowEffects(mode, { type: 'LOCK' }).revokeGrant, false);
  assert.equal(flowEffects({ name: 'party' }, { type: 'LOCK' }).revokeGrant, false);
});

test('Beauty Lab reuses the SAME PIN gate and grant, then navigates to the lab', () => {
  const pin = run([{ type: 'SESSION_READY' }, { type: 'CHOOSE_BEAUTY_LAB' }]);
  assert.deepEqual(pin, { name: 'pin', target: 'beautyLab' });
  // The same UNLOCKED event (grant in hand) routes to Beauty Lab, not the home.
  const lab = tvFlowReducer(pin, { type: 'UNLOCKED', home });
  assert.deepEqual(lab, { name: 'beautyLab', home });
  // BACK / no digits from the Beauty Lab PIN gate returns to mode selection.
  assert.deepEqual(tvFlowReducer(pin, { type: 'PIN_CANCELLED' }), mode);
});

test('BACK from the Beauty Lab root LOCKS to mode selection and revokes the grant', () => {
  const lab: TvFlowState = { name: 'beautyLab', home };
  assert.deepEqual(tvFlowReducer(lab, { type: 'LOCK' }), mode);
  assert.equal(flowEffects(lab, { type: 'LOCK' }).revokeGrant, true);
  // A PIN change evicts Beauty Lab with the notice, never to pairing.
  assert.deepEqual(
    tvFlowReducer(lab, { type: 'LOCK', reason: 'pinChanged' }),
    { name: 'mode', notice: 'pinChanged' },
  );
});

test('session/association invalidation tears down the Beauty Lab too', () => {
  const lab: TvFlowState = { name: 'beautyLab', home };
  assert.deepEqual(tvFlowReducer(lab, { type: 'SESSION_INVALID' }), pairing);
  assert.deepEqual(tvFlowReducer(lab, { type: 'ASSOCIATION_INCOMPLETE' }), { name: 'pairing', incomplete: true });
  const invalid = flowEffects(lab, { type: 'SESSION_INVALID' });
  assert.equal(invalid.dropGrant, true);
  assert.equal(invalid.clearSession, true);
});

test('Party and Personal Area can never be active simultaneously', () => {
  const party = run([{ type: 'SESSION_READY' }, { type: 'CHOOSE_PARTY' }]);
  // Personal events are inert inside Party …
  assert.deepEqual(tvFlowReducer(party, { type: 'UNLOCKED', home }), party);
  assert.deepEqual(tvFlowReducer(party, { type: 'OPEN_GALLERY' }), party);
  // … and Party events are inert inside the Personal Area.
  const personal = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  assert.deepEqual(tvFlowReducer(personal, { type: 'CHOOSE_PARTY' }), personal);
});
