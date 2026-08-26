// Pure identity key for privacy-scoped subtrees (the media viewer).
//
// INVARIANT: when the authenticated identity changes — an account switch OR a
// sign-out — the first render under the NEW identity must observe FRESH,
// empty state; nothing belonging to the previous identity may ever be
// observed again. The app achieves this by keying ViewerProvider on this
// value: React unmounts and remounts a keyed element when the key changes,
// and the remounted provider constructs a new empty model BEFORE its first
// render. An effect-based wipe could only run AFTER such a render had already
// committed the previous identity's state — that was the race this replaces.
//
// Kept free of expo/RN/session imports so plain node --test can exercise it.

export interface ViewerIdentitySource {
  status: 'restoring' | 'unauthed' | 'authed';
  user: { id?: string } | null;
}

/** Distinct value per authenticated identity; 'anonymous' when signed out. */
export function viewerIdentityKey(session: ViewerIdentitySource): string {
  return session.status === 'authed'
    ? `user:${session.user?.id ?? 'unknown'}`
    : 'anonymous';
}