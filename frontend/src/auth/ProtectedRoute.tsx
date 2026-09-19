import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useI18n } from '../i18n';
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
  const { t } = useI18n();
  if (state.status === 'loading') {
    // The FIRST words of every authenticated session, so they are the first
    // that have to be in the reader's language.
    return <div className="loading-screen">{t('common.loading')}</div>;
  }
  if (state.status === 'anon') {
    const returnTo = `${location.pathname}${location.search}${location.hash}`;
    return <Navigate to="/login" state={{ returnTo }} replace />;
  }
  return <>{children}</>;
}
