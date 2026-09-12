import assert from 'node:assert/strict';
import test from 'node:test';
import {
  flowEffects,
  initialFlowState,
  isPersonalState,
  tvFlowReducer,
  type TvFlowEvent,
  type TvFlowState,
} from './flow.ts';
import type { AssignmentView } from '../lib/assignmentView.ts';

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

  // The library and the album shelf do nothing while locked.
  assert.deepEqual(tvFlowReducer(mode, { type: 'OPEN_LIBRARY' }), mode);
  assert.deepEqual(tvFlowReducer(mode, { type: 'OPEN_ALBUMS' }), mode);
});

test('library: BACK returns to personal home; BACK from home locks to mode selection', () => {
  const homeState = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  const library = tvFlowReducer(homeState, { type: 'OPEN_LIBRARY' });
  assert.deepEqual(library, { name: 'personalLibrary', home });
  assert.deepEqual(tvFlowReducer(library, { type: 'LIBRARY_BACK' }), homeState);

  const locked = tvFlowReducer(homeState, { type: 'LOCK' });
  assert.deepEqual(locked, mode);
  // Re-entering requires the code again: only CHOOSE_PERSONAL → pin is offered.
  assert.deepEqual(tvFlowReducer(locked, { type: 'OPEN_LIBRARY' }), locked);
  assert.deepEqual(tvFlowReducer(locked, { type: 'CHOOSE_PERSONAL' }), { name: 'pin', target: 'personal' });
  assert.equal(flowEffects(library, { type: 'LOCK' }).revokeGrant, true);
});

test('albums: reachable only from the shelf, and BACK returns to the shelf', () => {
  const homeState = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  const shelf = tvFlowReducer(homeState, { type: 'OPEN_ALBUMS' });
  assert.deepEqual(shelf, { name: 'personalAlbums', home });

  const album = { id: 'a-1', name: 'Vacanze' };
  const inside = tvFlowReducer(shelf, { type: 'OPEN_ALBUM', album });
  assert.deepEqual(inside, { name: 'personalAlbumItems', home, album });

  // BACK from inside an album returns to the SHELF, not to the personal home:
  // the user came from the shelf and expects it back.
  assert.deepEqual(tvFlowReducer(inside, { type: 'ALBUM_BACK' }), shelf);
  assert.deepEqual(tvFlowReducer(shelf, { type: 'ALBUMS_BACK' }), homeState);

  // An album can never be entered from anywhere else — in particular not from a
  // locked state, and not by skipping the shelf.
  assert.deepEqual(tvFlowReducer(mode, { type: 'OPEN_ALBUM', album }), mode);
  assert.deepEqual(tvFlowReducer(homeState, { type: 'OPEN_ALBUM', album }), homeState);
  assert.equal(flowEffects(inside, { type: 'LOCK' }).revokeGrant, true);
});

test('a PIN change locks to mode selection with the notice — never back to pairing', () => {
  for (const from of [
    { name: 'personalHome', home } as TvFlowState,
    { name: 'personalLibrary', home } as TvFlowState,
    { name: 'personalAlbums', home } as TvFlowState,
    { name: 'personalAlbumItems', home, album: { id: 'a-1', name: 'Vacanze' } } as TvFlowState,
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

test('a library without the capability flag cannot be opened', () => {
  const noGallery = { displayName: 'Owner', galleryAvailable: false };
  const state = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' },
    { type: 'UNLOCKED', home: noGallery },
  ]);
  assert.deepEqual(tvFlowReducer(state, { type: 'OPEN_LIBRARY' }), state);
  assert.deepEqual(tvFlowReducer(state, { type: 'OPEN_ALBUMS' }), state);
});

test('pairing revocation tears down EVERY state, personal or not', () => {
  const states: TvFlowState[] = [
    { name: 'loading' },
    mode,
    { name: 'party' },
    { name: 'pin', target: 'personal' },
    { name: 'personalHome', home },
    { name: 'personalLibrary', home },
    { name: 'personalAlbums', home },
    { name: 'personalAlbumItems', home, album: { id: 'a-1', name: 'Vacanze' } },
    { name: 'beautyLab', home },
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
    { name: 'personalLibrary', home },
    { name: 'personalAlbums', home },
    { name: 'personalAlbumItems', home, album: { id: 'a-1', name: 'Vacanze' } },
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
    flowEffects({ name: 'personalLibrary', home }, { type: 'LOCK' }).revokeGrant, true);
  assert.equal(
    flowEffects({ name: 'personalAlbums', home }, { type: 'LOCK' }).revokeGrant, true);
  assert.equal(
    flowEffects(
      { name: 'personalAlbumItems', home, album: { id: 'a-1', name: 'Vacanze' } },
      { type: 'LOCK' },
    ).revokeGrant, true);
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

// --- the assigned party: server-authoritative takeover ----------------------

type PartyView = Extract<AssignmentView, { party: unknown }>;
const view = (
  presentation: 'slideshow' | 'game' | 'unavailable',
  key = 'k-a', albumId: string | null = 'album-a', albumName: string | null = 'Festa',
): PartyView => ({ presentation, party: { key, albumId, albumName } });
const general: AssignmentView = { presentation: 'general' };
const assignment = (v: AssignmentView): TvFlowEvent => ({ type: 'ASSIGNMENT', view: v });

const everyLocalState: TvFlowState[] = [
  mode,
  { name: 'mode', notice: 'pinChanged' },
  { name: 'party' },
  { name: 'updates' },
  { name: 'pin', target: 'personal' },
  { name: 'pin', target: 'beautyLab' },
  { name: 'personalHome', home },
  { name: 'personalLibrary', home },
  { name: 'personalAlbums', home },
  { name: 'personalAlbumItems', home, album: { id: 'a-1', name: 'Vacanze' } },
  { name: 'beautyLab', home },
];

test('an assigned television starts in its party, straight from the first read', () => {
  // The first GET /api/tv/session already carries the presentation. No stop on
  // the mode selector, no second request to discover the takeover.
  for (const from of [initialFlowState, pairing, { name: 'pairing', incomplete: true } as TvFlowState]) {
    assert.deepEqual(tvFlowReducer(from, { type: 'SESSION_READY', assignment: view('slideshow') }),
      { name: 'partySlideshow', party: view('slideshow').party });
    assert.deepEqual(tvFlowReducer(from, { type: 'SESSION_READY', assignment: view('game') }),
      { name: 'partyGame', party: view('game').party });
    assert.deepEqual(tvFlowReducer(from, { type: 'SESSION_READY', assignment: view('unavailable') }),
      { name: 'partyUnavailable', party: view('unavailable').party });
    assert.deepEqual(tvFlowReducer(from, { type: 'SESSION_READY', assignment: general }), mode);
  }
});

test('the presentation moves the screen: general, slideshow, game, finished, restarted', () => {
  let state = tvFlowReducer(mode, assignment(view('slideshow')));
  assert.equal(state.name, 'partySlideshow');
  // The game takes the display.
  state = tvFlowReducer(state, assignment(view('game')));
  assert.equal(state.name, 'partyGame');
  // FINISHED (after the closing card): the slideshow of the SAME party.
  state = tvFlowReducer(state, assignment(view('slideshow')));
  assert.deepEqual(state, { name: 'partySlideshow', party: view('slideshow').party });
  // restart_game → lobby: the game takes it again. No pairing, no new party.
  state = tvFlowReducer(state, assignment(view('game')));
  assert.deepEqual(state, { name: 'partyGame', party: view('game').party });
  // A GENERAL television waiting on the mode selector is taken over directly.
  assert.equal(tvFlowReducer(mode, assignment(view('game'))).name, 'partyGame');
});

test('the same assignment read again is the same state: a poll restarts nothing', () => {
  const state = tvFlowReducer(mode, assignment(view('game')));
  assert.equal(tvFlowReducer(state, assignment(view('game'))), state);
  const slideshow = tvFlowReducer(mode, assignment(view('slideshow')));
  assert.equal(tvFlowReducer(slideshow, assignment(view('slideshow'))), slideshow);
});

test('Party A to Party B is a different state, even on the same album', () => {
  const a = tvFlowReducer(mode, assignment(view('game', 'k-a')));
  const b = tvFlowReducer(a, assignment(view('game', 'k-b', 'album-b', 'Compleanno')));
  assert.notEqual(b, a);
  assert.deepEqual(b, { name: 'partyGame', party: { key: 'k-b', albumId: 'album-b', albumName: 'Compleanno' } });
  // A new party link for the SAME album is a new party too: a different key
  // is what forces the teardown and the fresh capability.
  const relinked = tvFlowReducer(a, assignment(view('game', 'k-a2')));
  assert.notEqual(relinked, a);
  assert.equal(relinked.name === 'partyGame' && relinked.party.key, 'k-a2');
});

test('PARTY to GENERAL returns to mode selection, and GENERAL touches nothing else', () => {
  for (const presentation of ['slideshow', 'game', 'unavailable'] as const) {
    assert.deepEqual(tvFlowReducer(tvFlowReducer(mode, assignment(view(presentation))), assignment(general)),
      mode);
  }
  // A general television's own screens are not the server's to move.
  for (const state of everyLocalState) {
    assert.equal(tvFlowReducer(state, assignment(general)), state, state.name);
  }
});

test('an unavailable party fails closed, and only the server lets it out', () => {
  const game = tvFlowReducer(mode, assignment(view('game')));
  const gone = tvFlowReducer(game, assignment(view('unavailable')));
  assert.deepEqual(gone, { name: 'partyUnavailable', party: view('unavailable').party });

  // The slideshow's album answering 404 fails closed on the SAME party — never
  // another album, never general.
  const slideshow = tvFlowReducer(mode, assignment(view('slideshow')));
  assert.deepEqual(tvFlowReducer(slideshow, { type: 'PARTY_CONTENT_GONE' }),
    { name: 'partyUnavailable', party: slideshow.name === 'partySlideshow' ? slideshow.party : null });
  // It means nothing anywhere else.
  assert.equal(tvFlowReducer(game, { type: 'PARTY_CONTENT_GONE' }), game);
  assert.equal(tvFlowReducer(mode, { type: 'PARTY_CONTENT_GONE' }), mode);

  // Out again only by a server read: slideshow, game, or general.
  assert.equal(tvFlowReducer(gone, assignment(view('slideshow'))).name, 'partySlideshow');
  assert.equal(tvFlowReducer(gone, assignment(view('game'))).name, 'partyGame');
  assert.deepEqual(tvFlowReducer(gone, assignment(general)), mode);
});

test('an assigned party preempts every local surface, Personal Area included', () => {
  for (const from of everyLocalState) {
    for (const presentation of ['slideshow', 'game', 'unavailable'] as const) {
      const next = tvFlowReducer(from, assignment(view(presentation)));
      assert.equal(next.name,
        { slideshow: 'partySlideshow', game: 'partyGame', unavailable: 'partyUnavailable' }[presentation],
        `${from.name} → ${presentation}`);
    }
  }
});

test('preempting a personal screen LOCKS it; preempting anything else holds nothing', () => {
  const personal = everyLocalState.filter((state) => isPersonalState(state));
  assert.equal(personal.length, 5);
  for (const from of personal) {
    const effects = flowEffects(from, assignment(view('game')));
    // Exactly what BACK does: the grant is dropped and revoked server-side.
    assert.equal(effects.revokeGrant, true, from.name);
    assert.equal(effects.clearSession, false, 'the pairing is not the Personal Area');
  }
  for (const from of everyLocalState.filter((state) => !isPersonalState(state))) {
    assert.equal(flowEffects(from, assignment(view('game'))).revokeGrant, false, from.name);
  }
  // GENERAL arriving while somebody is in their library changes nothing.
  assert.equal(flowEffects({ name: 'personalLibrary', home }, assignment(general)).revokeGrant, false);
});

test('an unlock that lands after its PIN screen was preempted is revoked on arrival', () => {
  const game = tvFlowReducer({ name: 'pin', target: 'personal' }, assignment(view('game')));
  assert.equal(game.name, 'partyGame');
  // The code was being checked when the party arrived; the grant came back
  // afterwards. It opens nothing and it is not kept.
  assert.equal(tvFlowReducer(game, { type: 'UNLOCKED', home }), game);
  assert.equal(flowEffects(game, { type: 'UNLOCKED', home }).revokeGrant, true);
  // An ordinary unlock on the PIN screen is, of course, kept.
  assert.equal(flowEffects({ name: 'pin', target: 'personal' }, { type: 'UNLOCKED', home }).revokeGrant, false);
});

test('no local event can take an assigned television back to general', () => {
  const localEvents: TvFlowEvent[] = [
    { type: 'CHOOSE_PARTY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'CHOOSE_BEAUTY_LAB' },
    { type: 'CHOOSE_UPDATES' }, { type: 'UPDATES_BACK' }, { type: 'PIN_CANCELLED' },
    { type: 'UNLOCKED', home }, { type: 'OPEN_LIBRARY' }, { type: 'LIBRARY_BACK' },
    { type: 'OPEN_ALBUMS' }, { type: 'ALBUMS_BACK' }, { type: 'ALBUM_BACK' },
    { type: 'OPEN_ALBUM', album: { id: 'a-1', name: 'Vacanze' } },
    { type: 'LOCK' }, { type: 'LOCK', reason: 'pinChanged' }, { type: 'PARTY_EXIT' },
    { type: 'SESSION_READY' }, { type: 'SESSION_READY', assignment: general },
  ];
  for (const presentation of ['slideshow', 'game', 'unavailable'] as const) {
    const assigned = tvFlowReducer(mode, assignment(view(presentation)));
    for (const event of localEvents) {
      assert.equal(tvFlowReducer(assigned, event), assigned, `${presentation} + ${event.type}`);
    }
  }
});

test('a revoked session tears an assigned party down like any other state', () => {
  for (const presentation of ['slideshow', 'game', 'unavailable'] as const) {
    const assigned = tvFlowReducer(mode, assignment(view(presentation)));
    assert.deepEqual(tvFlowReducer(assigned, { type: 'SESSION_INVALID' }), pairing);
    const effects = flowEffects(assigned, { type: 'SESSION_INVALID' });
    assert.equal(effects.dropGrant, true);
    assert.equal(effects.clearSession, true);
    assert.deepEqual(tvFlowReducer(assigned, { type: 'ASSOCIATION_INCOMPLETE' }),
      { name: 'pairing', incomplete: true });
  }
});

test('nothing is taken over before there is a session to trust', () => {
  for (const from of [initialFlowState, pairing]) {
    assert.equal(tvFlowReducer(from, assignment(view('game'))), from);
  }
});

test('Party and Personal Area can never be active simultaneously', () => {
  const party = run([{ type: 'SESSION_READY' }, { type: 'CHOOSE_PARTY' }]);
  // Personal events are inert inside Party …
  assert.deepEqual(tvFlowReducer(party, { type: 'UNLOCKED', home }), party);
  assert.deepEqual(tvFlowReducer(party, { type: 'OPEN_LIBRARY' }), party);
  assert.deepEqual(tvFlowReducer(party, { type: 'OPEN_ALBUMS' }), party);
  // … and Party events are inert inside the Personal Area.
  const personal = run([
    { type: 'SESSION_READY' }, { type: 'CHOOSE_PERSONAL' }, { type: 'UNLOCKED', home },
  ]);
  assert.deepEqual(tvFlowReducer(personal, { type: 'CHOOSE_PARTY' }), personal);
});
