import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  fetchPermissionCatalog,
  listRoles,
  type PermissionCatalogEntry,
  type Role,
} from '@nubarca/api-client';

// The roles and the permission catalogue, loaded together.
//
// Both administration screens need both: the Users page to EXPLAIN a role before
// it is assigned, the Roles page to edit one. Loading them from one hook means a
// role preview always reads current role data and never a copy that travelled
// through a user object — the shape that made a role change render the previous
// role's permissions.

export type RoleCatalogStatus = 'loading' | 'ready' | 'forbidden' | 'error';

export interface RoleCatalog {
  status: RoleCatalogStatus;
  roles: Role[];
  permissions: PermissionCatalogEntry[];
  find(roleKey: string | null | undefined): Role | null;
  reload(): void;
}

export function useRoleCatalog(): RoleCatalog {
  const [status, setStatus] = useState<RoleCatalogStatus>('loading');
  const [roles, setRoles] = useState<Role[]>([]);
  const [permissions, setPermissions] = useState<PermissionCatalogEntry[]>([]);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      setStatus('loading');
      try {
        const [roleList, catalog] = await Promise.all([
          listRoles(controller.signal),
          fetchPermissionCatalog(controller.signal),
        ]);
        setRoles(roleList.roles);
        setPermissions(catalog.permissions);
        setStatus('ready');
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setStatus(err instanceof ApiError && err.status === 403 ? 'forbidden' : 'error');
      }
    })();
    return () => controller.abort();
  }, [nonce]);

  const find = useCallback(
    (roleKey: string | null | undefined) => roles.find((r) => r.key === roleKey) ?? null,
    [roles],
  );

  const reload = useCallback(() => setNonce((n) => n + 1), []);

  return { status, roles, permissions, find, reload };
}
