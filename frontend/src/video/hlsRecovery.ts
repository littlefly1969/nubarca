// What to do about a FATAL hls.js error, as a pure decision.
//
// Two failure modes have to stay distinguishable:
//
//   * A transient one — a segment that 502'd, a decoder that hiccupped on a
//     rendition switch — where replacing the player with a permanent error
//     message is wrong, because retrying works.
//
//   * A real one — the file is gone, or the session expired and the segment
//     request came back 401 — where retrying forever hides the truth from the
//     user and hammers the server.
//
// The rule is a small, finite budget per class. Recoveries are attempted while
// budget remains and the error state is honest once it does not. There is no
// path here that retries without bound.

export type FatalErrorKind = 'network' | 'media' | 'other';

export type RecoveryAction = 'restart-load' | 'recover-media' | 'give-up';

/** Recoveries already spent, per class. */
export interface RecoveryBudget {
  network: number;
  media: number;
}

export const EMPTY_BUDGET: RecoveryBudget = { network: 0, media: 0 };

/**
 * Attempts allowed per class.
 *
 * Network gets one more than media because a flaky segment is the commoner
 * and cheaper failure; a media error that survives two `recoverMediaError()`
 * calls is not going to survive a third.
 */
export const MAX_NETWORK_RECOVERIES = 3;
export const MAX_MEDIA_RECOVERIES = 2;

export interface RecoveryPlan {
  action: RecoveryAction;
  /** The budget after this decision. */
  budget: RecoveryBudget;
}

/** Map an hls.js `ErrorTypes` value onto the classes this policy knows. */
export function classifyFatalError(
  type: string,
  types: { NETWORK_ERROR: string; MEDIA_ERROR: string },
): FatalErrorKind {
  if (type === types.NETWORK_ERROR) return 'network';
  if (type === types.MEDIA_ERROR) return 'media';
  return 'other';
}

/** What to do about one fatal error, given what has already been spent. */
export function planRecovery(kind: FatalErrorKind, spent: RecoveryBudget): RecoveryPlan {
  if (kind === 'network' && spent.network < MAX_NETWORK_RECOVERIES) {
    return { action: 'restart-load', budget: { ...spent, network: spent.network + 1 } };
  }
  if (kind === 'media' && spent.media < MAX_MEDIA_RECOVERIES) {
    return { action: 'recover-media', budget: { ...spent, media: spent.media + 1 } };
  }
  // Unknown class, or the budget for this one is gone.
  return { action: 'give-up', budget: spent };
}
