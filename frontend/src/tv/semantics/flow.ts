// The browser display's top-level state machine — pure, no I/O.
//
// A PORT of tv/src/personal/flow.ts restricted to the screens a browser has.
// Every state and event that exists in both is spelled the same and behaves
// the same, and `nativeParity.test.ts` runs the two reducers side by side to
// keep it that way. What the browser lacks is only what it has no surface
// for: the native Updates screen (a browser updates by reloading) and the
// separate personal album shelf (the web gallery is the one library).
//
//   loading ── SESSION_READY ──▶ mode ── CHOOSE_PARTY ──▶ party
//      │            │             │  ◀── PARTY_EXIT ───────┘
//      │            │             └── CHOOSE_PERSONAL / CHOOSE_BEAUTY_LAB ──▶ pin
//      │            │                    │  ◀── PIN_CANCELLED
//      │            │                    └── UNLOCKED ──▶ personalHome | beautyLab
//      │            │                          │ OPEN_LIBRARY ▶ personalLibrary
//      │            │                          │ ◀─ LIBRARY_BACK┘
//      │            │                          └── LOCK ──▶ mode
//      │            └─(assigned party)──▶ partySlideshow | partyGame | partyUnavailable
//      ├── SESSION_INVALID (from ANY state) ──▶ pairing
//      └── ASSOCIATION_INCOMPLETE (any paired state) ──▶ pairing(incomplete)
//
//   ASSIGNMENT (from ANY paired state):
//      party presentation ──▶ partySlideshow | partyGame | partyUnavailable
//      general            ──▶ mode (only when leaving a party presentation)
//
// THE ASSIGNMENT IS SERVER-AUTHORITATIVE. An owner who assigns this display to
// a party has decided what the screen in the room shows, and nothing local
// outranks it: not the mode selector, not manual Party browsing, not the PIN
// entry and not somebody standing in their own Personal Area — leaving a
// personal screen that way LOCKS it (flowEffects). The three party states are
// keyed by the server's assignment key, so Party A → Party B and slideshow →
// game are different states, which is what forces a teardown. And an assigned
// party is left ONLY by the server: no local event returns it to general.

import type { AssignedParty, AssignmentView } from './assignmentView';

export interface PersonalHomeInfo {
  displayName: string;
  galleryAvailable: boolean;
}

export type ModeNotice = 'pinChanged' | null;

export type UnlockTarget = 'personal' | 'beautyLab';

export type TvFlowState =
  | { name: 'loading' }
  | { name: 'pairing'; incomplete: boolean }
  | { name: 'mode'; notice: ModeNotice }
  // Manual Party browsing, chosen from the mode selector of a GENERAL display.
  | { name: 'party' }
  | { name: 'partySlideshow'; party: AssignedParty }
  | { name: 'partyGame'; party: AssignedParty }
  | { name: 'partyUnavailable'; party: AssignedParty }
  | { name: 'pin'; target: UnlockTarget }
  | { name: 'personalHome'; home: PersonalHomeInfo }
  | { name: 'personalLibrary'; home: PersonalHomeInfo }
  | { name: 'beautyLab'; home: PersonalHomeInfo };

export type TvFlowEvent =
  | { type: 'SESSION_READY'; assignment?: AssignmentView }
  | { type: 'SESSION_INVALID' }
  | { type: 'ASSOCIATION_INCOMPLETE' }
  | { type: 'CHOOSE_PARTY' }
  | { type: 'CHOOSE_PERSONAL' }
  | { type: 'CHOOSE_BEAUTY_LAB' }
  | { type: 'PIN_CANCELLED' }
  | { type: 'UNLOCKED'; home: PersonalHomeInfo }
  | { type: 'OPEN_LIBRARY' }
  | { type: 'LIBRARY_BACK' }
  | { type: 'LOCK'; reason?: 'pinChanged' }
  | { type: 'PARTY_EXIT' }
  | { type: 'ASSIGNMENT'; view: AssignmentView }
  | { type: 'PARTY_CONTENT_GONE' };

export const initialFlowState: TvFlowState = { name: 'loading' };

const MODE: TvFlowState = { name: 'mode', notice: null };

/**
 * Pure transition function. Illegal events for the current state return the
 * state unchanged, so scattered callers cannot create invalid combinations.
 */
export function tvFlowReducer(state: TvFlowState, event: TvFlowEvent): TvFlowState {
  switch (event.type) {
    case 'SESSION_INVALID':
      return { name: 'pairing', incomplete: false };
    case 'ASSOCIATION_INCOMPLETE':
      return state.name === 'loading' || state.name === 'pairing'
        ? state
        : { name: 'pairing', incomplete: true };
    case 'SESSION_READY':
      return state.name === 'loading' || state.name === 'pairing'
        ? (event.assignment ? assignedState(event.assignment) : null) ?? MODE
        : state;
    case 'CHOOSE_PARTY':
      return state.name === 'mode' ? { name: 'party' } : state;
    case 'CHOOSE_PERSONAL':
      return state.name === 'mode' ? { name: 'pin', target: 'personal' } : state;
    case 'CHOOSE_BEAUTY_LAB':
      return state.name === 'mode' ? { name: 'pin', target: 'beautyLab' } : state;
    case 'PIN_CANCELLED':
      return state.name === 'pin' ? MODE : state;
    case 'UNLOCKED':
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
    case 'LOCK':
      return isPersonalState(state)
        ? { name: 'mode', notice: event.reason ?? null }
        : state;
    case 'PARTY_EXIT':
      return state.name === 'party' ? MODE : state;

    case 'ASSIGNMENT': {
      if (state.name === 'loading' || state.name === 'pairing') return state;
      const target = assignedState(event.view);
      if (target === null) {
        return isAssignedPartyState(state) ? MODE : state;
      }
      // Already showing exactly this: a poll every few seconds must not restart
      // the show. Anything else, from ANY paired state, is the owner's decision.
      return sameAssignedState(state, target) ? state : target;
    }

    case 'PARTY_CONTENT_GONE':
      return state.name === 'partySlideshow'
        ? { name: 'partyUnavailable', party: state.party }
        : state;
  }
}

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

/** The three states a display assigned to a party can be in. */
export function isAssignedPartyState(
  state: TvFlowState,
): state is Extract<TvFlowState, { party: AssignedParty }> {
  return state.name === 'partySlideshow'
    || state.name === 'partyGame'
    || state.name === 'partyUnavailable';
}

/**
 * Every state that lives behind the Personal Area unlock grant. ONE predicate,
 * so a personal screen cannot be added without also being locked, re-validated
 * and torn down.
 */
export function isPersonalState(state: TvFlowState): boolean {
  return state.name === 'personalHome'
    || state.name === 'personalLibrary'
    || state.name === 'beautyLab';
}

/**
 * How a VALIDATED session is admitted to its first screen. A session whose
 * owner has no Personal Area code is an INCOMPLETE association and may run
 * neither Party nor Personal — an assigned party included — so it is admitted
 * without its assignment and handed straight to the recovery.
 */
export function admissionEvents(assignment: AssignmentView, pinConfigured: boolean): TvFlowEvent[] {
  return pinConfigured
    ? [{ type: 'SESSION_READY', assignment }]
    : [{ type: 'SESSION_READY' }, { type: 'ASSOCIATION_INCOMPLETE' }];
}

/**
 * Which side effects a transition requires:
 *   revokeGrant  — drop the unlock grant from memory AND revoke it server-side;
 *   dropGrant    — drop it from memory only (the session itself is gone);
 *   clearSession — the paired session is no longer usable.
 * A party the server assigns taking the screen from a Personal Area is a LOCK,
 * with the same effect as BACK.
 */
export function flowEffects(
  from: TvFlowState, event: TvFlowEvent,
): { revokeGrant: boolean; dropGrant: boolean; clearSession: boolean } {
  const teardown = event.type === 'SESSION_INVALID' || event.type === 'ASSOCIATION_INCOMPLETE';
  const locked = event.type === 'LOCK' && isPersonalState(from);
  const preempted = isPersonalState(from) && isAssignedPartyState(tvFlowReducer(from, event));
  const strayUnlock = event.type === 'UNLOCKED' && from.name !== 'pin';
  return {
    revokeGrant: locked || preempted || strayUnlock,
    dropGrant: teardown,
    clearSession: teardown,
  };
}
