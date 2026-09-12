// Explicit top-level state machine for the TV app modes (pure — no I/O), used
// by App.tsx and tested with node --test.
//
//   loading ── SESSION_READY ──▶ mode ── CHOOSE_PARTY ──▶ party
//      │            │             │  ◀── PARTY_EXIT ───────┘
//      │            │             ├── CHOOSE_UPDATES ──▶ updates
//      │            │             │  ◀── UPDATES_BACK ─────┘
//      │            │             └── CHOOSE_PERSONAL ──▶ pin
//      │            │                    │  ◀── PIN_CANCELLED
//      │            │                    └── UNLOCKED ──▶ personalHome
//      │            │                          │ OPEN_LIBRARY ▶ personalLibrary
//      │            │                          │ ◀─ LIBRARY_BACK┘
//      │            │                          │ OPEN_ALBUMS ▶ personalAlbums
//      │            │                          │ ◀─ ALBUMS_BACK┘  │ OPEN_ALBUM
//      │            │                          │                  ▼
//      │            │                          │            personalAlbumItems
//      │            │                          │            ◀─ ALBUM_BACK
//      │            │                          └── LOCK ──▶ mode
//      │            └─(assigned party)──▶ partySlideshow | partyGame | partyUnavailable
//      ├── SESSION_INVALID (from ANY state) ──▶ pairing ── SESSION_READY ▶ mode | party*
//      └── ASSOCIATION_INCOMPLETE (any paired state) ──▶ pairing(incomplete)
//
//   ASSIGNMENT (from ANY paired state):
//      party presentation ──▶ partySlideshow | partyGame | partyUnavailable
//      general            ──▶ mode (only when leaving a party presentation)
//
// THE ASSIGNMENT IS SERVER-AUTHORITATIVE. An owner who assigns this television
// to a party has decided what the screen in the room shows, and nothing local
// outranks that: not the mode selector, not manual Party browsing, not the
// Updates screen, not the PIN entry, and not somebody standing in their own
// Personal Area. Leaving a personal screen this way LOCKS it (see flowEffects)
// exactly as BACK does. The three party states are keyed by the server's
// assignment key and named by its presentation, so Party A → Party B and
// slideshow → game are both different states — which is what forces a
// teardown rather than one party's picture surviving into the next.
//
// `personalGallery` and `personalVideos` are GONE. They were two independent
// browsing surfaces over the same owner media with two query models, two filter
// vocabularies and two navigation engines — and the video one had no filters at
// all. One `personalLibrary` state now serves All/Photos/Videos from a single
// query identity, exactly as the web Media Workspace does.
//
// Invariants enforced here (side effects — revoking the grant, clearing caches
// — are driven by App.tsx off the transitions, see `flowEffects`):
//   * a personal screen is reachable ONLY through UNLOCKED (grant in hand), and
//     an unlock that lands anywhere else is revoked on arrival;
//   * after a session becomes ready the entry state is the SERVER's: its
//     assigned party presentation, or mode selection — the previously selected
//     mode is never auto-reopened — and only once the association is known to
//     be complete (admissionEvents);
//   * BACK from the personal root LOCKS (never leaves the area unlocked); a
//     LOCK caused by a PIN change carries a notice for the mode selector but
//     stays on mode selection (the TV association itself is still valid);
//   * an assigned party is left ONLY by the server saying so — there is no
//     local event that returns an assigned television to the general
//     experience (BACK at its root closes the app instead, see App.tsx);
//   * a revoked session tears down EVERY state, personal, party or not;
//   * a paired session whose owner has NO PIN (legacy/corrupted data — the
//     atomic pairing flow cannot produce it) is an INCOMPLETE association:
//     neither Party nor Personal may run under it; the TV returns to pairing
//     with the "pairing is incomplete" notice;
//   * Party and Personal Area can never be active simultaneously;
//   * `updates` is a PLAIN top-level mode, deliberately NOT a personal state: it
//     needs no PIN, holds no grant, calls no owner-private API and touches no
//     cached media. It talks only to the OTA endpoint the app already uses and to
//     the public APK/release-descriptor path. Adding it to `isPersonalState`
//     would make BACK from it revoke a grant it never held.

import type { AssignedParty, AssignmentView } from '../lib/assignmentView';

export interface PersonalHomeInfo {
  displayName: string;
  galleryAvailable: boolean;
}

// Why the TV is on the mode selector / pairing screen — drives a small notice
// line in the UI. Never carries hash/generation/grant details.
export type ModeNotice = 'pinChanged' | null;

// The PIN screen is a SHARED gate: after unlock it navigates to the ORIGINAL
// requested target — Personal Area or Beauty Lab. Both reuse the SAME PIN + the
// SAME in-memory grant; Beauty Lab never mints a second PIN or grant type.
export type UnlockTarget = 'personal' | 'beautyLab';

// One album the user opened from the Personal Area album shelf. Only what the
// items screen needs to render its header — never a cover URL cache or counts
// that would go stale behind it.
export interface PersonalAlbumRef {
  id: string;
  name: string;
}

export type TvFlowState =
  | { name: 'loading' }
  | { name: 'pairing'; incomplete: boolean }
  | { name: 'mode'; notice: ModeNotice }
  // Manual Party browsing, chosen from the mode selector of a GENERAL TV.
  | { name: 'party' }
  // The party this television is ASSIGNED to, in the presentation the server
  // projects for it right now. The native slideshow while no game holds the
  // screen; the canonical web stage while one does; an honest native
  // "unavailable" when the party the assignment names cannot be shown.
  | { name: 'partySlideshow'; party: AssignedParty }
  | { name: 'partyGame'; party: AssignedParty }
  | { name: 'partyUnavailable'; party: AssignedParty }
  | { name: 'updates' }
  | { name: 'pin'; target: UnlockTarget }
  | { name: 'personalHome'; home: PersonalHomeInfo }
  | { name: 'personalLibrary'; home: PersonalHomeInfo }
  | { name: 'personalAlbums'; home: PersonalHomeInfo }
  | { name: 'personalAlbumItems'; home: PersonalHomeInfo; album: PersonalAlbumRef }
  | { name: 'beautyLab'; home: PersonalHomeInfo };

export type TvFlowEvent =
  // From startup OR a completed pairing, carrying the FIRST server read of the
  // assignment so an assigned television starts in its party directly rather
  // than passing through the mode selector and discovering it a poll later.
  | { type: 'SESSION_READY'; assignment?: AssignmentView }
  | { type: 'SESSION_INVALID' }
  | { type: 'ASSOCIATION_INCOMPLETE' }
  | { type: 'CHOOSE_PARTY' }
  | { type: 'CHOOSE_PERSONAL' }
  | { type: 'CHOOSE_BEAUTY_LAB' }
  | { type: 'CHOOSE_UPDATES' }
  | { type: 'UPDATES_BACK' }
  | { type: 'PIN_CANCELLED' }
  | { type: 'UNLOCKED'; home: PersonalHomeInfo }
  | { type: 'OPEN_LIBRARY' }
  | { type: 'LIBRARY_BACK' }
  | { type: 'OPEN_ALBUMS' }
  | { type: 'ALBUMS_BACK' }
  | { type: 'OPEN_ALBUM'; album: PersonalAlbumRef }
  | { type: 'ALBUM_BACK' }
  | { type: 'LOCK'; reason?: 'pinChanged' }
  | { type: 'PARTY_EXIT' }
  // The server's assignment and presentation, as the control plane most
  // recently read them. One event for every outcome, because the shell must
  // react to a party arriving, changing presentation, changing party and going
  // away with the same authority.
  | { type: 'ASSIGNMENT'; view: AssignmentView }
  // The assigned slideshow's album answered 404: the party the assignment
  // names is no longer readable. Fail closed until the server says otherwise.
  | { type: 'PARTY_CONTENT_GONE' };

export const initialFlowState: TvFlowState = { name: 'loading' };

const MODE: TvFlowState = { name: 'mode', notice: null };

// Pure transition function. Illegal events for the current state return the
// state unchanged (e.g. OPEN_GALLERY while locked can never mint a personal
// screen), so scattered callers cannot create invalid combinations.
export function tvFlowReducer(state: TvFlowState, event: TvFlowEvent): TvFlowState {
  switch (event.type) {
    case 'SESSION_INVALID':
      return { name: 'pairing', incomplete: false };
    case 'ASSOCIATION_INCOMPLETE':
      // Paired but the owner has no PIN: invalid association — do not keep the
      // mode selector (or any mode) usable under it. Not applicable before a
      // session exists.
      return state.name === 'loading' || state.name === 'pairing'
        ? state
        : { name: 'pairing', incomplete: true };
    case 'SESSION_READY':
      // From startup OR a completed pairing: the server's assigned party if it
      // has one, mode selection otherwise.
      return state.name === 'loading' || state.name === 'pairing'
        ? (event.assignment ? assignedState(event.assignment) : null) ?? MODE
        : state;
    case 'CHOOSE_PARTY':
      return state.name === 'mode' ? { name: 'party' } : state;
    case 'CHOOSE_UPDATES':
      // No PIN gate: the update surface exposes nothing owner-private. It shows
      // the running release identity and applies updates the device is already
      // entitled to receive.
      return state.name === 'mode' ? { name: 'updates' } : state;
    case 'UPDATES_BACK':
      return state.name === 'updates' ? MODE : state;
    case 'CHOOSE_PERSONAL':
      return state.name === 'mode' ? { name: 'pin', target: 'personal' } : state;
    case 'CHOOSE_BEAUTY_LAB':
      return state.name === 'mode' ? { name: 'pin', target: 'beautyLab' } : state;
    case 'PIN_CANCELLED':
      return state.name === 'pin' ? MODE : state;
    case 'UNLOCKED':
      // Navigate to the ORIGINAL requested target the PIN gate was opened for.
      if (state.name !== 'pin') return state;
      return state.target === 'beautyLab'
        ? { name: 'beautyLab', home: event.home }
        : { name: 'personalHome', home: event.home };
    case 'OPEN_LIBRARY':
      return state.name === 'personalHome' && state.home.galleryAvailable
        ? { name: 'personalLibrary', home: state.home }
        : state;
    case 'LIBRARY_BACK':
      return state.name === 'personalLibrary'
        ? { name: 'personalHome', home: state.home }
        : state;
    case 'OPEN_ALBUMS':
      return state.name === 'personalHome' && state.home.galleryAvailable
        ? { name: 'personalAlbums', home: state.home }
        : state;
    case 'ALBUMS_BACK':
      return state.name === 'personalAlbums'
        ? { name: 'personalHome', home: state.home }
        : state;
    case 'OPEN_ALBUM':
      // An album is reachable ONLY from the album shelf, which is itself only
      // reachable through UNLOCKED — so no caller can navigate straight into
      // owner-private album content.
      return state.name === 'personalAlbums'
        ? { name: 'personalAlbumItems', home: state.home, album: event.album }
        : state;
    case 'ALBUM_BACK':
      // BACK from inside an album returns to the album LIST, not to the
      // Personal Area home: the user came from the shelf and expects it back.
      return state.name === 'personalAlbumItems'
        ? { name: 'personalAlbums', home: state.home }
        : state;
    case 'LOCK':
      // BACK from the Personal Area OR Beauty Lab root, an explicit lock, or a
      // server-side grant invalidation (reason 'pinChanged' when the owner
      // changed the code): always back to mode selection — never to pairing,
      // the TV association stays valid.
      return isPersonalState(state)
        ? { name: 'mode', notice: event.reason ?? null }
        : state;
    case 'PARTY_EXIT':
      return state.name === 'party' ? MODE : state;

    case 'ASSIGNMENT': {
      // Nothing is trusted before there is a session to trust.
      if (state.name === 'loading' || state.name === 'pairing') return state;
      const target = assignedState(event.view);
      if (target === null) {
        // GENERAL: leave an assigned party at once rather than keeping a stale
        // one on screen. Any other state was never the server's to change.
        return isAssignedPartyState(state) ? MODE : state;
      }
      // Already showing exactly this: nothing to do — a poll every few
      // seconds must not restart the show. Anything else, from ANY paired
      // state, is the owner's decision and wins.
      return sameAssignedState(state, target) ? state : target;
    }

    case 'PARTY_CONTENT_GONE':
      return state.name === 'partySlideshow'
        ? { name: 'partyUnavailable', party: state.party }
        : state;
  }
}

/** The assigned-party state a view asks for, or null for GENERAL. */
function assignedState(view: AssignmentView): TvFlowState | null {
  switch (view.presentation) {
    case 'general': return null;
    case 'slideshow': return { name: 'partySlideshow', party: view.party };
    case 'game': return { name: 'partyGame', party: view.party };
    case 'unavailable': return { name: 'partyUnavailable', party: view.party };
  }
}

function sameAssignedState(state: TvFlowState, target: TvFlowState): boolean {
  if (!isAssignedPartyState(state) || !isAssignedPartyState(target)) return false;
  return state.name === target.name
    && state.party.key === target.party.key
    && state.party.albumId === target.party.albumId
    && state.party.albumName === target.party.albumName;
}

/** The three states a television assigned to a party can be in. */
export function isAssignedPartyState(
  state: TvFlowState,
): state is Extract<TvFlowState, { party: AssignedParty }> {
  return state.name === 'partySlideshow'
    || state.name === 'partyGame'
    || state.name === 'partyUnavailable';
}

// Which side effects a transition requires. Kept next to the reducer so the
// lock/teardown rules are testable without React:
//   * revokeGrant — lock explicitly: drop the grant from memory AND revoke it
//                   server-side (lockTvPersonal);
//   * dropGrant   — drop the in-memory grant only (the session is dead or the
//                   association is invalid — bounded expiry covers the rest);
//   * clearSession— drop the persisted TV session + purge cached media.
// Every state that lives behind the unlock grant. Kept as ONE predicate so a
// new personal screen cannot be added without also being locked, re-validated
// and torn down — the failure mode of listing the names at each call site.
export function isPersonalState(state: TvFlowState): boolean {
  return state.name === 'personalHome'
    || state.name === 'personalLibrary'
    || state.name === 'personalAlbums'
    || state.name === 'personalAlbumItems'
    || state.name === 'beautyLab';
}

/**
 * How a VALIDATED session is admitted to its first screen — after a relaunch
 * and after a completed pairing alike.
 *
 * The caller has read two things: the assignment (with its presentation) and
 * the Personal Area status. A session whose owner has no PIN is an INCOMPLETE
 * association and may run neither Party nor Personal — an assigned party
 * included — so it is admitted WITHOUT its assignment and immediately handed
 * to the existing ASSOCIATION_INCOMPLETE teardown: no party state is ever
 * entered on the way, and App dispatches both in one tick so nothing in
 * between is drawn. A complete one starts straight in the server's
 * presentation, with no stop on the mode selector.
 */
export function admissionEvents(assignment: AssignmentView, pinConfigured: boolean): TvFlowEvent[] {
  return pinConfigured
    ? [{ type: 'SESSION_READY', assignment }]
    : [{ type: 'SESSION_READY' }, { type: 'ASSOCIATION_INCOMPLETE' }];
}

export function flowEffects(
  from: TvFlowState, event: TvFlowEvent,
): { revokeGrant: boolean; dropGrant: boolean; clearSession: boolean } {
  const teardown = event.type === 'SESSION_INVALID' || event.type === 'ASSOCIATION_INCOMPLETE';
  // BACK/explicit lock from a personal screen.
  const locked = event.type === 'LOCK' && isPersonalState(from);
  // A party the server assigns took the screen from somebody's Personal Area.
  // That is a lock, with the same effect as BACK: private content is unmounted
  // by the navigation and the grant that could read more of it is gone.
  const preempted = isPersonalState(from) && isAssignedPartyState(tvFlowReducer(from, event));
  // The PIN screen was preempted while its code was being checked, and the
  // unlock landed afterwards. The grant is already in memory; nothing may keep it.
  const strayUnlock = event.type === 'UNLOCKED' && from.name !== 'pin';
  return {
    revokeGrant: locked || preempted || strayUnlock,
    dropGrant: teardown,
    clearSession: teardown,
  };
}
