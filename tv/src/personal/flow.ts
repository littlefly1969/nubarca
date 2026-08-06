// Explicit top-level state machine for the TV app modes (pure — no I/O), used
// by App.tsx and tested with node --test.
//
//   loading ── SESSION_READY ──▶ mode ── CHOOSE_PARTY ──▶ party
//      │                          │  ◀── PARTY_EXIT ───────┘
//      │                          └── CHOOSE_PERSONAL ──▶ pin
//      │                                 │  ◀── PIN_CANCELLED
//      │                                 └── UNLOCKED ──▶ personalHome
//      │                                       │ OPEN_GALLERY ▶ personalGallery
//      │                                       │ ◀─ GALLERY_BACK┘
//      │                                       │ OPEN_VIDEOS ▶ personalVideos
//      │                                       │ ◀─ VIDEOS_BACK┘
//      │                                       └── LOCK ──▶ mode
//      ├── SESSION_INVALID (from ANY state) ──▶ pairing ── SESSION_READY ▶ mode
//      └── ASSOCIATION_INCOMPLETE (any paired state) ──▶ pairing(incomplete)
//
// Invariants enforced here (side effects — revoking the grant, clearing caches
// — are driven by App.tsx off the transitions, see `flowEffects`):
//   * a personal screen is reachable ONLY through UNLOCKED (grant in hand);
//   * mode selection is ALWAYS the entry state after a session becomes ready —
//     the previously selected mode is never auto-reopened;
//   * BACK from the personal root LOCKS (never leaves the area unlocked); a
//     LOCK caused by a PIN change carries a notice for the mode selector but
//     stays on mode selection (the TV association itself is still valid);
//   * a revoked session tears down EVERY state, personal or not;
//   * a paired session whose owner has NO PIN (legacy/corrupted data — the
//     atomic pairing flow cannot produce it) is an INCOMPLETE association:
//     neither Party nor Personal may run under it; the TV returns to pairing
//     with the "pairing is incomplete" notice;
//   * Party and Personal Area can never be active simultaneously.

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

export type TvFlowState =
  | { name: 'loading' }
  | { name: 'pairing'; incomplete: boolean }
  | { name: 'mode'; notice: ModeNotice }
  | { name: 'party' }
  | { name: 'pin'; target: UnlockTarget }
  | { name: 'personalHome'; home: PersonalHomeInfo }
  | { name: 'personalGallery'; home: PersonalHomeInfo }
  | { name: 'personalVideos'; home: PersonalHomeInfo }
  | { name: 'beautyLab'; home: PersonalHomeInfo };

export type TvFlowEvent =
  | { type: 'SESSION_READY' }
  | { type: 'SESSION_INVALID' }
  | { type: 'ASSOCIATION_INCOMPLETE' }
  | { type: 'CHOOSE_PARTY' }
  | { type: 'CHOOSE_PERSONAL' }
  | { type: 'CHOOSE_BEAUTY_LAB' }
  | { type: 'PIN_CANCELLED' }
  | { type: 'UNLOCKED'; home: PersonalHomeInfo }
  | { type: 'OPEN_GALLERY' }
  | { type: 'GALLERY_BACK' }
  | { type: 'OPEN_VIDEOS' }
  | { type: 'VIDEOS_BACK' }
  | { type: 'LOCK'; reason?: 'pinChanged' }
  | { type: 'PARTY_EXIT' };

export const initialFlowState: TvFlowState = { name: 'loading' };

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
      // From startup OR a completed pairing: always land on mode selection.
      return state.name === 'loading' || state.name === 'pairing'
        ? { name: 'mode', notice: null }
        : state;
    case 'CHOOSE_PARTY':
      return state.name === 'mode' ? { name: 'party' } : state;
    case 'CHOOSE_PERSONAL':
      return state.name === 'mode' ? { name: 'pin', target: 'personal' } : state;
    case 'CHOOSE_BEAUTY_LAB':
      return state.name === 'mode' ? { name: 'pin', target: 'beautyLab' } : state;
    case 'PIN_CANCELLED':
      return state.name === 'pin' ? { name: 'mode', notice: null } : state;
    case 'UNLOCKED':
      // Navigate to the ORIGINAL requested target the PIN gate was opened for.
      if (state.name !== 'pin') return state;
      return state.target === 'beautyLab'
        ? { name: 'beautyLab', home: event.home }
        : { name: 'personalHome', home: event.home };
    case 'OPEN_GALLERY':
      return state.name === 'personalHome' && state.home.galleryAvailable
        ? { name: 'personalGallery', home: state.home }
        : state;
    case 'GALLERY_BACK':
      return state.name === 'personalGallery'
        ? { name: 'personalHome', home: state.home }
        : state;
    case 'OPEN_VIDEOS':
      return state.name === 'personalHome' && state.home.galleryAvailable
        ? { name: 'personalVideos', home: state.home }
        : state;
    case 'VIDEOS_BACK':
      return state.name === 'personalVideos'
        ? { name: 'personalHome', home: state.home }
        : state;
    case 'LOCK':
      // BACK from the Personal Area OR Beauty Lab root, an explicit lock, or a
      // server-side grant invalidation (reason 'pinChanged' when the owner
      // changed the PIN): always back to mode selection — never to pairing, the
      // TV association stays valid.
      return state.name === 'personalHome' || state.name === 'personalGallery'
        || state.name === 'personalVideos' || state.name === 'beautyLab'
        ? { name: 'mode', notice: event.reason ?? null }
        : state;
    case 'PARTY_EXIT':
      return state.name === 'party' ? { name: 'mode', notice: null } : state;
  }
}

// Which side effects a transition requires. Kept next to the reducer so the
// lock/teardown rules are testable without React:
//   * revokeGrant — lock explicitly: drop the grant from memory AND revoke it
//                   server-side (lockTvPersonal);
//   * dropGrant   — drop the in-memory grant only (the session is dead or the
//                   association is invalid — bounded expiry covers the rest);
//   * clearSession— drop the persisted TV session + purge cached media.
export function flowEffects(
  from: TvFlowState, event: TvFlowEvent,
): { revokeGrant: boolean; dropGrant: boolean; clearSession: boolean } {
  const canLock = from.name === 'personalHome' || from.name === 'personalGallery'
    || from.name === 'personalVideos' || from.name === 'beautyLab';
  const teardown = event.type === 'SESSION_INVALID' || event.type === 'ASSOCIATION_INCOMPLETE';
  return {
    revokeGrant: event.type === 'LOCK' && canLock,
    dropGrant: teardown,
    clearSession: teardown,
  };
}
