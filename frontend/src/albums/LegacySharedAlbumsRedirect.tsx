import { Navigate } from 'react-router';

// "Shared with me" is no longer a destination of its own: an album is an album,
// and both collections live at /albums. This keeps every bookmark, every link
// somebody was sent and the whole of the previous navigation working, by
// resolving to the same collection under its new address.
//
// `replace` keeps the dead legacy entry out of the history stack, so Back from
// Albums goes wherever the user actually came from rather than bouncing through
// the redirect.
//
// The per-album route is NOT redirected: /shared-albums/{id} is the recipient's
// album, backed by the recipient's authority, and it stays exactly where it is.
// Owner and recipient must never resolve to one route.
export function LegacySharedAlbumsRedirect() {
  return <Navigate to="/albums?scope=shared" replace />;
}
