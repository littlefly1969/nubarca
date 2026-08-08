import type { PermissionCatalogEntry, Role } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import {
  builtInRoleDescriptionKey,
  builtInRoleLabelKey,
  permissionGroupLabelKey,
  permissionLabelKey,
} from './permissionMeta';
import { groupAllPermissions } from './permissionSet';

// How a role is NAMED. A built-in role has a product name that belongs in the
// reader's language; a custom role is named by the operator, so its name is
// shown verbatim — translating somebody's own words would be wrong.
export function useRoleText() {
  const { t } = useI18n();
  return {
    name(role: Pick<Role, 'key' | 'name'>): string {
      const key = builtInRoleLabelKey(role.key);
      return key ? t(key) : role.name;
    },
    description(role: Pick<Role, 'key' | 'description'>): string | null {
      const key = builtInRoleDescriptionKey(role.key);
      return key ? t(key) : role.description;
    },
  };
}

export function RoleBadge({ role }: { role: Pick<Role, 'key' | 'name' | 'isSystem'> }) {
  const text = useRoleText();
  return (
    <span className={`role-badge${role.isSystem ? ' role-badge--system' : ''}`}>
      {text.name(role)}
    </span>
  );
}

// The read-only answer to "what does this role actually mean".
//
// Rendered from ROLE data and the catalogue — never from a user's cached detail,
// which is exactly what used to leave the previous role's permissions on screen
// after a role change. Give it a different role and it says something different,
// immediately, because there is no other state involved.
//
// State is never carried by colour alone: each row has a symbol AND a word.
export function RolePermissionPreview({
  role, catalog, headingLevel = 'h4',
}: {
  role: Pick<Role, 'key' | 'permissions'>;
  catalog: readonly PermissionCatalogEntry[];
  headingLevel?: 'h3' | 'h4' | 'h5';
}) {
  const { t } = useI18n();
  const Heading = headingLevel;
  const held = new Set(role.permissions);

  return (
    <div className="role-preview" data-testid="role-permission-preview">
      {groupAllPermissions(catalog).map(({ group, entries }) => (
        <section key={group} className="role-preview__group">
          <Heading>{t(permissionGroupLabelKey(group))}</Heading>
          <ul>
            {entries.map((entry) => {
              const on = held.has(entry.key);
              return (
                <li
                  key={entry.key}
                  className={
                    `role-preview__row${entry.parent ? ' role-preview__row--child' : ''}` +
                    `${on ? ' is-on' : ''}`
                  }
                  data-permission={entry.key}
                  data-included={on ? 'yes' : 'no'}
                >
                  <span className="role-preview__name">{t(permissionLabelKey(entry.key))}</span>
                  <span className="role-preview__state">
                    <span aria-hidden="true">{on ? '✓' : '–'}</span>
                    <span className="visually-hidden">
                      {on ? t('roles.included') : t('roles.notIncluded')}
                    </span>
                  </span>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}

// The one-line summary shown next to a role selector: what it is, and how much
// it grants, before anything is saved.
export function RoleSummary({ role }: { role: Role }) {
  const { tn } = useI18n();
  const text = useRoleText();
  const description = text.description(role);
  return (
    <div className="role-summary" data-testid="role-summary" data-role={role.key}>
      <strong>{text.name(role)}</strong>
      {description && <p className="muted">{description}</p>}
      <p className="role-summary__count" data-testid="role-permission-count">
        {tn(role.permissions.length, 'roles.permissionCount')}
      </p>
    </div>
  );
}
