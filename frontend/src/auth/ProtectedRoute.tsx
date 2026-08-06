import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from './useAuth';

interface ProtectedRouteProps {
  children: ReactNode;
}

// Wraps any route that requires an authenticated user. The `loading` branch
// is intentionally a full-page spinner — flashing a redirect to /login while
// the auth probe is in flight would log the user out visually on every
// reload.
export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const { state } = useAuth();
  const location = useLocation();
  if (state.status === 'loading') {
    return <div className="loading-screen">Loading…</div>;
  }
  if (state.status === 'anon') {
    const returnTo = `${location.pathname}${location.search}${location.hash}`;
    return <Navigate to="/login" state={{ returnTo }} replace />;
  }
  return <>{children}</>;
}
