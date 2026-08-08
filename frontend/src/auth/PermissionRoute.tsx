import type { ReactNode } from 'react';
import { Link } from 'react-router';
import type { PermissionKey } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { usePermissions } from './usePermissions';

interface PermissionRouteProps {
  // Every listed permission is required. The Laboratory sections use this: a
  // section needs the shell permission as well, matching the server's composite
  // policy exactly rather than approximating it.
  permissions: readonly PermissionKey[];
  children: ReactNode;
}

// A frontend guard for a destination the user may not hold.
//
// Its job is a CLEAN forbidden state rather than a broken page: without it, a
// direct navigation or an old bookmark renders a component that immediately
// fires an API call, gets 403, and shows whatever partial state it happened to
// be in. With it, the answer is a small explained page and a way back.
//
// It is not the security boundary and does not pretend to be. Every one of
// these routes is enforced server-side; this only decides what the person sees.
export function PermissionRoute({ permissions, children }: PermissionRouteProps) {
  const perms = usePermissions();
  if (!perms.hasAll(permissions)) {
    return <ForbiddenPage />;
  }
  return <>{children}</>;
}

export function ForbiddenPage() {
  const { t } = useI18n();
  return (
    <section className="admin-page" data-testid="forbidden-page">
      <header className="admin-header">
        <h2>{t('forbidden.heading')}</h2>
      </header>
      <div className="admin-card form-measure">
        <p role="status">{t('forbidden.body')}</p>
        <p>
          <Link to="/" className="row-action">{t('forbidden.backHome')}</Link>
        </p>
      </div>
    </section>
  );
}
