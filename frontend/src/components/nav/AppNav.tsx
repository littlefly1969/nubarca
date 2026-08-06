import { NavLink } from 'react-router';
import { useI18n } from '../../i18n';
import { Icon } from '../icons/Icon';
import { buildNavGroups } from './navModel';

interface AppNavProps {
  isAdmin: boolean;
  // Icons-only rail. Labels stay in the accessible name via `title` + the
  // visually-hidden span, so a collapsed rail is still usable with a screen
  // reader and with a mouse hover.
  collapsed?: boolean;
  // Fired on any link activation so a modal drawer can close itself.
  onNavigate?: () => void;
}

// The nav item list, rendered identically by the desktop sidebar and the mobile
// drawer. One component, one information architecture — there is no separate
// mobile navigation model.
export function AppNav({ isAdmin, collapsed = false, onNavigate }: AppNavProps) {
  const { t } = useI18n();
  const groups = buildNavGroups({ isAdmin });

  return (
    <div className={`app-nav${collapsed ? ' is-collapsed' : ''}`}>
      {groups.map((group) => (
        <div key={group.id} className={`app-nav__group app-nav__group--${group.id}`}>
          <h2 className="app-nav__group-title" id={`app-nav-group-${group.id}`}>
            {t(group.titleKey)}
          </h2>
          <ul className="app-nav__list" aria-labelledby={`app-nav-group-${group.id}`}>
            {group.items.map((item) => (
              <li key={item.to}>
                <NavLink
                  to={item.to}
                  end={item.end}
                  className={({ isActive }) =>
                    isActive ? 'app-nav-link app-nav-link-active' : 'app-nav-link'}
                  title={collapsed ? t(item.labelKey) : undefined}
                  onClick={onNavigate}
                >
                  <Icon name={item.icon} />
                  <span className={collapsed ? 'visually-hidden' : 'app-nav-link__label'}>
                    {t(item.labelKey)}
                  </span>
                </NavLink>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  );
}
