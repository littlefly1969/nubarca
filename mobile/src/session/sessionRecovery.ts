// Pure decision: does this cold-start validation failure mean the persisted
// cookie is DEAD (drop it) or merely that the server was UNREACHABLE (keep
// it and let the user retry)? Dropping the cookie on a network error would
// sign an airplane-mode user out permanently — an auth verdict exists only
// when the server actually answered 401/403.
export function shouldDropPersistedSession(err: unknown): boolean {
  const status = (err as { status?: number } | null)?.status;
  return status === 401 || status === 403;
}