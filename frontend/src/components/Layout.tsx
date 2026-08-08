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
import { AppScrollProvider } from './appScroll';
import { CastProvider } from '../cast/CastProvider';
import { CastMiniController } from '../cast/CastMiniController';

// Authenticated app shell: a collapsible left navigation, a compact top utility
// bar and a full-width content region.
//
// This replaces the previous single header row that wrapped a dozen nav links
// next to the email, a language select and a logout button. The navigation is
// now grouped (Main / More / Administration) and driven by one data model
// (navModel), the user-scoped controls live in one popover (UserMenu), and
// narrow viewports get the same navigation through an accessible modal drawer
// rather than a second information architecture.
//
// The shell owns the viewport rather than growing the document: `.app-shell` is
// exactly one dynamic viewport tall, the top bar is its first row, and
// `.app-main` is the single box that scrolls. The top bar and the sidebar used to
// be sticky over a scrolling document, which worked but made the sidebar depend
// on the top bar's height in rem — change one and the other had to be edited to
// match. Now the sidebar simply fills the row it is given and `<main>` is handed
// to the pages below through AppScrollProvider, because with the document
// stationary an observer root or a virtualizer's scroll element has to be told
// where scrolling actually happens.

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
  const mainRef = useRef<HTMLElement>(null);

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
  // The scroll owner is `.app-main`, not the body, so that is what gets locked —
  // locking the body would no longer stop anything. `overflow: hidden` keeps the
  // element's scroll offset, and the reserved scrollbar gutter means the content
  // does not shift sideways while the drawer is open.
  useEffect(() => {
    const main = mainRef.current;
    if (!drawerOpen || !main) return;
    const previous = main.style.overflowY;
    main.style.overflowY = 'hidden';
    return () => { main.style.overflowY = previous; };
  }, [drawerOpen]);

  if (state.status !== 'authed') {
    // ProtectedRoute already guards this; the branch keeps TS happy.
    return null;
  }

  // One permission list feeds both the desktop rail and the mobile drawer.
  const permissions = state.user.effectivePermissions;

  return (
    // NUBARCA-GOOGLE-CAST-01: the Cast session is owned HERE, above every page
    // and every media viewer. That placement is the feature: closing the viewer
    // — or navigating to another page entirely — leaves the television playing,
    // and the mini controller below stays the way back to it.
    <CastProvider>
    <AppScrollProvider viewportRef={mainRef}>
    <div className={`app-shell${collapsed ? ' app-shell--rail' : ''}`}>
      <header className="app-topbar" data-testid="app-topbar">
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
          <AppNav permissions={permissions} collapsed={collapsed} />
        </nav>

        {/* The application's scroll viewport. Every authenticated page scrolls
            here; the document does not move. */}
        <main className="app-main" ref={mainRef} data-testid="app-main">
          <Outlet />
        </main>
      </div>

      {drawerOpen && (
        <NavDrawer permissions={permissions} onClose={closeDrawer} returnFocusRef={menuButtonRef} />
      )}

      {/* Renders itself only while something is playing on a receiver. */}
      <CastMiniController />
    </div>
    </AppScrollProvider>
    </CastProvider>
  );
}
