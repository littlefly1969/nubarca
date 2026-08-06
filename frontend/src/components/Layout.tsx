import { useCallback, useEffect, useRef, useState } from 'react';
import { Outlet } from 'react-router';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { BrandMark } from '../brand/BrandMark';
import { PRODUCT_NAME, SHELL_MARK_VISIBLE_PX, markBoxForVisibleWidth } from '../brand/brand';
import { readStoredItem } from '../storage/brandedStorageKey';
import { AppNav } from './nav/AppNav';
import { NavDrawer } from './nav/NavDrawer';
import { UserMenu } from './UserMenu';
import { Icon } from './icons/Icon';

// Authenticated app shell: a collapsible left navigation, a compact top utility
// bar and a full-width content region.
//
// This replaces the previous single header row that wrapped a dozen nav links
// next to the email, a language select and a logout button. The navigation is
// now grouped (Main / More / Administration) and driven by one data model
// (navModel), the user-scoped controls live in one popover (UserMenu), and
// narrow viewports get the same navigation through an accessible modal drawer
// rather than a second information architecture.

// Bounded local key for the rail state. Not a preference the backend knows or
// needs to know about.
const RAIL_KEY = 'nubarca.nav.collapsed';

function readCollapsed(): boolean {
  return readStoredItem(RAIL_KEY) === '1';
}

export function Layout() {
  const { state, logout, updateUser } = useAuth();
  const { t } = useI18n();
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const menuButtonRef = useRef<HTMLButtonElement>(null);

  const toggleRail = useCallback(() => {
    setCollapsed((prev) => {
      const next = !prev;
      try {
        window.localStorage.setItem(RAIL_KEY, next ? '1' : '0');
      } catch {
        // Non-fatal: the choice just does not survive a reload.
      }
      return next;
    });
  }, []);

  const closeDrawer = useCallback(() => setDrawerOpen(false), []);

  // The drawer is a modal; while it is open the page behind it must not scroll.
  useEffect(() => {
    if (!drawerOpen) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => { document.body.style.overflow = previous; };
  }, [drawerOpen]);

  if (state.status !== 'authed') {
    // ProtectedRoute already guards this; the branch keeps TS happy.
    return null;
  }

  const isAdmin = state.user.isAdmin;

  return (
    <div className={`app-shell${collapsed ? ' app-shell--rail' : ''}`}>
      <header className="app-topbar">
        <button
          ref={menuButtonRef}
          type="button"
          className="icon-button app-topbar__menu"
          aria-label={t('nav.openMenu')}
          aria-expanded={drawerOpen}
          aria-haspopup="dialog"
          data-testid="nav-menu-button"
          onClick={() => setDrawerOpen(true)}
        >
          <Icon name="menu" size={20} />
        </button>

        <button
          type="button"
          className="icon-button app-topbar__rail-toggle"
          aria-label={collapsed ? t('nav.expandNav') : t('nav.collapseNav')}
          aria-pressed={collapsed}
          data-testid="nav-rail-toggle"
          onClick={toggleRail}
        >
          <Icon name={collapsed ? 'chevron-right' : 'chevron-left'} size={20} />
        </button>

        <span className="app-topbar__brand app-brand-lockup">
          {/* Clear space comes from .app-brand-lockup's padding, not from
              padding inside the mark that would shrink the artwork. */}
          <BrandMark size={markBoxForVisibleWidth(SHELL_MARK_VISIBLE_PX.desktop)} clearSpace={false} />
          <span className="app-brand">{PRODUCT_NAME}</span>
        </span>

        <div className="app-topbar__utility">
          <UserMenu
            displayName={state.user.displayName}
            email={state.user.email}
            onUserUpdated={(user) => updateUser(user as Parameters<typeof updateUser>[0])}
            onSignOut={() => void logout()}
          />
        </div>
      </header>

      <div className="app-shell__body">
        {/* Desktop rail. CSS hides it below the drawer breakpoint; the drawer
            renders the same AppNav there. */}
        <nav className="app-sidebar" aria-label={t('nav.primary')} data-testid="app-sidebar">
          <AppNav isAdmin={isAdmin} collapsed={collapsed} />
        </nav>

        <main className="app-main">
          <Outlet />
        </main>
      </div>

      {drawerOpen && (
        <NavDrawer isAdmin={isAdmin} onClose={closeDrawer} returnFocusRef={menuButtonRef} />
      )}
    </div>
  );
}
