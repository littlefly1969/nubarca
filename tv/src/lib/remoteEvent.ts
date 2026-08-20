// ONE normalization of a TV remote event's PHASE. Pure, node-testable.
//
// THE PROBLEM
// -----------
// `react-native-tvos` delivers an `HWEvent` whose `eventKeyAction` describes
// the key phase: 0 = ACTION_DOWN, 1 = ACTION_UP. On some runtimes and for some
// synthetic events it is `undefined` instead. A handler that ignores the field
// fires TWICE for one physical press — the classic "one press skipped two
// photos" defect — and a handler that requires `=== 1` silently does nothing on
// the runtimes that omit it.
//
// Six screens each carried their own copy of `if (!evt || evt.eventKeyAction
// === 0) return;`. Six copies of a rule is six chances for the seventh screen
// to get it wrong, so the rule lives here now:
//
//     explicit ACTION_DOWN (0)  → ignore
//     explicit ACTION_UP   (1)  → act, exactly once
//     undefined                 → act, exactly once
//
// This module deliberately knows NOTHING about what any key means. Phase is a
// platform concern; meaning is a product concern and lives in
// video/remoteMap.ts (viewers) or personal/dpadCode.ts (the PIN reducer). That
// separation is what lets the secure code domain reuse the phase rule without
// inheriting viewer semantics.

/** The subset of HWEvent this module needs. Structural, so tests need no RN. */
export interface RemoteEventLike {
  eventType?: string;
  eventKeyAction?: number;
}

/** ACTION_DOWN as react-native-tvos reports it. */
export const REMOTE_ACTION_DOWN = 0;

/**
 * True when this delivery is the one that should produce an action.
 *
 * Anything that is not an explicit key-DOWN acts. That asymmetry is
 * deliberate: an unknown phase must still work the remote, and only the phase
 * we can positively identify as a duplicate is dropped.
 */
export function shouldActOnRemoteEvent(event: RemoteEventLike | null | undefined): boolean {
  if (!event) return false;
  return event.eventKeyAction !== REMOTE_ACTION_DOWN;
}

/**
 * The event type to act on, or null when this delivery must be ignored.
 * Callers switch on the result and never look at the phase themselves.
 */
export function actionableEventType(
  event: RemoteEventLike | null | undefined,
): string | null {
  if (!shouldActOnRemoteEvent(event)) return null;
  const type = event?.eventType;
  return typeof type === 'string' && type.length > 0 ? type : null;
}

// Dedicated transport keys. Listed here so a screen can ask "is this a media
// key?" without hard-coding the vocabulary, and so the negative rule below has
// one definition.
export const MEDIA_TRANSPORT_EVENTS: readonly string[] = [
  'playPause', 'play', 'pause', 'rewind', 'fastForward', 'stop',
];

/**
 * True for a dedicated transport key.
 *
 * Screens that are NOT a media context use this to LEAVE those keys alone.
 * Repurposing Play/Pause to activate a focused button, or Fast-Forward to move
 * a filter, would make NubArca steal transport control from whatever else the
 * television is playing — and would surprise a user whose muscle memory says
 * those keys belong to playback.
 */
export function isMediaTransportEvent(eventType: string): boolean {
  return MEDIA_TRANSPORT_EVENTS.includes(eventType);
}
