import { NavLink, Outlet } from 'react-router';
import { useI18n } from '../i18n';
import type { MessageKey } from '../i18n';

// The Laboratory (Laboratorio): one primary-navigation destination holding the
// two owner-private experimental surfaces that used to sit beside each other in
// the nav as unrelated top-level entries.
//
// The tabs are ROUTES, not component state. A tab that lived in useState would
// be lost on refresh, unlinkable, and invisible to Back/Forward — so each
// section owns a URL (/lab/plates, /lab/aesthetics) and the shell renders
// whichever one the router matched through <Outlet />.
//
// They share a shell, a heading and a tab strip. They share nothing else:
// Plates talks to /api/plates/*, Aesthetics to /api/aesthetics/*, and this file
// deliberately knows about neither. Combining their APIs, storage, permissions
// or data models is explicitly NOT what this is.
//
// The strip is a <nav> of links rather than an ARIA tablist. Route-backed tabs
// ARE navigation: links give keyboard operation, focus order, middle-click,
// "open in new tab" and Back/Forward for free, and aria-current carries the
// selected state. An ARIA tablist would have to reimplement all of that and
// would lie about the widget being an in-page control.

interface LabSection {
  to: string;
  labelKey: MessageKey;
}

export const LAB_SECTIONS: readonly LabSection[] = [
  { to: '/lab/plates', labelKey: 'lab.plates' },
  { to: '/lab/aesthetics', labelKey: 'lab.aesthetics' },
] as const;

/** Where a bare /lab lands. */
export const LAB_DEFAULT_ROUTE = LAB_SECTIONS[0].to;

export function LaboratoryPage() {
  const { t } = useI18n();

  return (
    <section className="lab-workspace" aria-label={t('lab.heading')}>
      <header className="lab-header">
        <h2>{t('lab.heading')}</h2>
      </header>

      <nav className="lab-tabs" aria-label={t('lab.sectionsAria')}>
        {LAB_SECTIONS.map((section) => (
          <NavLink
            key={section.to}
            to={section.to}
            // NavLink sets aria-current="page" on the active link itself.
            className={({ isActive }) => (isActive ? 'lab-tab is-active' : 'lab-tab')}
          >
            {t(section.labelKey)}
          </NavLink>
        ))}
      </nav>

      <Outlet />
    </section>
  );
}
