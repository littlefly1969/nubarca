import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { Layout } from './Layout';
import { NavDrawer } from './nav/NavDrawer';
import { ThemeProvider } from '../theme';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

function renderLayout({
  isAdmin = false,
  value,
  initialEntries = ['/'],
}: {
  isAdmin?: boolean;
  value?: Parameters<typeof AuthedWrapper>[0]['value'];
  initialEntries?: string[];
} = {}) {
  return render(
    <AuthedWrapper isAdmin={isAdmin} value={value}>
      <ThemeProvider>
        <MemoryRouter initialEntries={initialEntries}>
          <Routes>
            <Route element={<Layout />}>
              <Route path="/" element={<div>home page</div>} />
              <Route path="/media" element={<div>library page</div>} />
              <Route path="/albums" element={<div>albums page</div>} />
            </Route>
          </Routes>
        </MemoryRouter>
      </ThemeProvider>
    </AuthedWrapper>,
  );
}

// The desktop rail and the (conditionally mounted) mobile drawer render the SAME
// nav, so queries are scoped to the sidebar to stay unambiguous.
function sidebar() {
  return within(screen.getByTestId('app-sidebar'));
}

describe('Layout primary navigation', () => {
  it('shows the normal-user destinations in the sidebar', () => {
    renderLayout();
    const nav = sidebar();
    // Default UI language is Italian.
    expect(nav.getByRole('link', { name: 'File' })).toHaveAttribute('href', '/');
    expect(nav.getByRole('link', { name: 'Libreria' })).toHaveAttribute('href', '/media');
    expect(nav.getByRole('link', { name: 'Album' })).toHaveAttribute('href', '/albums');
    expect(nav.getByRole('link', { name: 'Volti' })).toHaveAttribute('href', '/people');
    expect(nav.getByRole('link', { name: 'Laboratorio' })).toHaveAttribute('href', '/lab');
    expect(nav.getByRole('link', { name: 'Condivisioni' })).toHaveAttribute('href', '/shares');
    expect(nav.getByRole('link', { name: 'Cestino' })).toHaveAttribute('href', '/trash');
  });

  it('does not offer Upload or TV Devices in the primary navigation', () => {
    renderLayout({ isAdmin: true });
    const nav = sidebar();
    expect(nav.queryByRole('link', { name: 'Carica' })).not.toBeInTheDocument();
    expect(nav.queryByRole('link', { name: 'Dispositivi TV' })).not.toBeInTheDocument();
    // …and no link points at the legacy routes either.
    const hrefs = nav.getAllByRole('link').map((a) => a.getAttribute('href'));
    expect(hrefs).not.toContain('/upload');
    expect(hrefs).not.toContain('/tv-devices');
  });

  it('keeps Cloud Functions and Private in the primary navigation', () => {
    renderLayout();
    const nav = sidebar();
    expect(nav.getByRole('link', { name: 'Funzioni cloud' })).toHaveAttribute('href', '/cloud-functions');
    expect(nav.getByRole('link', { name: 'Privato' })).toHaveAttribute('href', '/private');
  });

  it('groups navigation and separates administration for admins only', () => {
    renderLayout({ isAdmin: false });
    expect(sidebar().queryByRole('heading', { name: 'Amministrazione' })).not.toBeInTheDocument();
    cleanup();

    renderLayout({ isAdmin: true });
    const nav = sidebar();
    expect(nav.getByRole('heading', { name: 'Principale' })).toBeInTheDocument();
    expect(nav.getByRole('heading', { name: 'Altro' })).toBeInTheDocument();
    expect(nav.getByRole('heading', { name: 'Amministrazione' })).toBeInTheDocument();
  });

  it('keeps the admin entries role-gated', () => {
    renderLayout({ isAdmin: false });
    expect(sidebar().queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument();
    expect(sidebar().queryByRole('link', { name: 'Utenti' })).not.toBeInTheDocument();
    cleanup();

    renderLayout({ isAdmin: true });
    expect(sidebar().getByRole('link', { name: 'Admin' })).toHaveAttribute('href', '/admin');
    expect(sidebar().getByRole('link', { name: 'Utenti' })).toHaveAttribute('href', '/admin/users');
  });

  it('marks the current destination as the active nav link', () => {
    renderLayout({ initialEntries: ['/media'] });
    const library = sidebar().getByRole('link', { name: 'Libreria' });
    expect(library.className).toContain('app-nav-link-active');
    expect(library).toHaveAttribute('aria-current', 'page');
  });

  it('exposes the navigation as a labelled landmark', () => {
    renderLayout();
    expect(screen.getByRole('navigation', { name: 'Principale' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toBeInTheDocument();
  });

  it('collapses and expands the rail with an accessible toggle', async () => {
    renderLayout();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('nav-rail-toggle');
    expect(toggle).toHaveAttribute('aria-label', 'Comprimi la navigazione');

    await user.click(toggle);
    expect(screen.getByTestId('nav-rail-toggle')).toHaveAttribute('aria-label', 'Espandi la navigazione');
    // Labels stay reachable for assistive tech even in the icons-only rail.
    expect(sidebar().getByRole('link', { name: 'Libreria' })).toBeInTheDocument();

    await user.click(screen.getByTestId('nav-rail-toggle'));
    expect(screen.getByTestId('nav-rail-toggle')).toHaveAttribute('aria-label', 'Comprimi la navigazione');
  });
});

describe('Layout mobile drawer', () => {
  it('is closed until the menu button is pressed', () => {
    renderLayout();
    expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument();
    const button = screen.getByTestId('nav-menu-button');
    expect(button).toHaveAttribute('aria-label', 'Apri il menu di navigazione');
    expect(button).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens as a labelled modal dialog containing the same navigation', async () => {
    renderLayout();
    await userEvent.setup().click(screen.getByTestId('nav-menu-button'));

    const drawer = screen.getByTestId('nav-drawer');
    expect(drawer).toHaveAttribute('role', 'dialog');
    expect(drawer).toHaveAttribute('aria-modal', 'true');
    expect(drawer).toHaveAttribute('aria-label', 'Menu di navigazione');
    expect(screen.getByTestId('nav-menu-button')).toHaveAttribute('aria-expanded', 'true');
    expect(within(drawer).getByRole('link', { name: 'Libreria' })).toHaveAttribute('href', '/media');
    // Same information architecture — no Upload/TV Devices here either.
    expect(within(drawer).queryByRole('link', { name: 'Carica' })).not.toBeInTheDocument();
  });

  it('moves focus into the drawer and closes on Escape, restoring focus', async () => {
    renderLayout();
    const user = userEvent.setup();
    const button = screen.getByTestId('nav-menu-button');
    await user.click(button);

    expect(screen.getByTestId('nav-drawer')).toHaveFocus();

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument());
    expect(button).toHaveFocus();
  });

  it('closes on a route change', async () => {
    renderLayout();
    const user = userEvent.setup();
    await user.click(screen.getByTestId('nav-menu-button'));

    const drawer = screen.getByTestId('nav-drawer');
    await user.click(within(drawer).getByRole('link', { name: 'Album' }));

    await waitFor(() => expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument());
    expect(screen.getByText('albums page')).toBeInTheDocument();
  });

  it('closes on a backdrop click and via its own close button', async () => {
    renderLayout();
    const user = userEvent.setup();

    await user.click(screen.getByTestId('nav-menu-button'));
    await user.click(screen.getByTestId('nav-drawer-close'));
    await waitFor(() => expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument());

    await user.click(screen.getByTestId('nav-menu-button'));
    await user.click(screen.getByTestId('nav-drawer-backdrop'));
    await waitFor(() => expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument());
  });

  it('does not close itself when its close-on-navigation effect re-runs', async () => {
    // The regression this pins: the effect used to skip its first run via a
    // boolean ref, which is NOT idempotent. React re-runs effects for reasons
    // other than a real navigation — StrictMode double-invokes every mount
    // effect, and a changed callback identity re-runs this one — and each extra
    // run took the "not the first render" branch and closed the drawer. In a
    // real browser the drawer could therefore never be opened at all.
    //
    // Re-running with an UNCHANGED location must be a no-op.
    const onClose = vi.fn();
    const { rerender } = render(
      <AuthedWrapper>
        <MemoryRouter initialEntries={['/media']}>
          <NavDrawer isAdmin={false} onClose={onClose} />
        </MemoryRouter>
      </AuthedWrapper>,
    );
    expect(onClose).not.toHaveBeenCalled();

    // A new callback identity re-runs the effect without any navigation.
    for (let i = 0; i < 3; i += 1) {
      rerender(
        <AuthedWrapper>
          <MemoryRouter initialEntries={['/media']}>
            <NavDrawer isAdmin={false} onClose={() => onClose()} />
          </MemoryRouter>
        </AuthedWrapper>,
      );
    }

    expect(onClose).not.toHaveBeenCalled();
  });

  it('still closes on a route change under StrictMode', async () => {
    render(
      <StrictMode>
        <AuthedWrapper>
          <ThemeProvider>
            <MemoryRouter initialEntries={['/']}>
              <Routes>
                <Route element={<Layout />}>
                  <Route path="/" element={<div>home page</div>} />
                  <Route path="/albums" element={<div>albums page</div>} />
                </Route>
              </Routes>
            </MemoryRouter>
          </ThemeProvider>
        </AuthedWrapper>
      </StrictMode>,
    );
    const user = userEvent.setup();
    await user.click(screen.getByTestId('nav-menu-button'));

    const drawer = await screen.findByTestId('nav-drawer');
    await user.click(within(drawer).getByRole('link', { name: 'Album' }));

    await waitFor(() => expect(screen.queryByTestId('nav-drawer')).not.toBeInTheDocument());
    expect(screen.getByText('albums page')).toBeInTheDocument();
  });

  it('keeps Tab inside the open drawer', async () => {
    renderLayout();
    const user = userEvent.setup();
    await user.click(screen.getByTestId('nav-menu-button'));
    const drawer = screen.getByTestId('nav-drawer');

    // Shift+Tab from the panel itself wraps to the LAST focusable inside it,
    // never out to the page behind the modal.
    await user.keyboard('{Shift>}{Tab}{/Shift}');
    expect(drawer.contains(document.activeElement)).toBe(true);
  });
});

describe('Layout user menu', () => {
  it('gathers identity, account, language, theme and sign out in one popover', async () => {
    renderLayout();
    const user = userEvent.setup();

    // The bare header no longer scatters the email / language / logout.
    expect(screen.queryByText('(dev@nubarca.local)')).not.toBeInTheDocument();

    const trigger = screen.getByTestId('user-menu-trigger');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');
    await user.click(trigger);

    const popover = within(screen.getByTestId('user-menu-popover'));
    expect(popover.getByText('Dev User')).toBeInTheDocument();
    expect(popover.getByText('dev@nubarca.local')).toBeInTheDocument();
    expect(popover.getByRole('link', { name: 'Account' })).toHaveAttribute('href', '/account');
    expect(popover.getByRole('combobox', { name: 'Lingua' })).toBeInTheDocument();
    expect(popover.getByRole('radiogroup', { name: 'Tema' })).toBeInTheDocument();
    expect(popover.getByTestId('user-menu-signout')).toBeInTheDocument();
  });

  it('closes the popover on Escape', async () => {
    renderLayout();
    const user = userEvent.setup();
    await user.click(screen.getByTestId('user-menu-trigger'));
    expect(screen.getByTestId('user-menu-popover')).toBeInTheDocument();

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('user-menu-popover')).not.toBeInTheDocument());
    expect(screen.getByTestId('user-menu-trigger')).toHaveFocus();
  });

  it('signs out from the popover', async () => {
    const logout = vi.fn(async () => {});
    renderLayout({ value: { logout } });
    const user = userEvent.setup();
    await user.click(screen.getByTestId('user-menu-trigger'));
    await user.click(screen.getByTestId('user-menu-signout'));
    expect(logout).toHaveBeenCalled();
  });

  it('still persists a language change to the user profile via the API', async () => {
    const updateUser = vi.fn();
    const mock = installFetchMock({
      'PUT /api/auth/me/language': () => jsonResponse({
        id: 'user-1', email: 'dev@nubarca.local', displayName: 'Dev User',
        isAdmin: false, language: 'en',
      }),
    });

    renderLayout({ value: { updateUser } });
    const user = userEvent.setup();
    await user.click(screen.getByTestId('user-menu-trigger'));

    await user.selectOptions(screen.getByRole('combobox', { name: 'Lingua' }), 'en');

    await waitFor(() => {
      expect(mock.calls.some((c) => c.method === 'PUT' && c.url.includes('/api/auth/me/language'))).toBe(true);
    });
    const putCall = mock.calls.find((c) => c.url.includes('/api/auth/me/language'));
    expect(JSON.parse(putCall!.body ?? '{}')).toEqual({ language: 'en' });
    await waitFor(() => expect(updateUser).toHaveBeenCalled());
  });

  it('selects a theme from the popover and persists it', async () => {
    renderLayout();
    const user = userEvent.setup();
    await user.click(screen.getByTestId('user-menu-trigger'));

    await user.click(screen.getByTestId('theme-option-light'));
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(window.localStorage.getItem('nubarca.theme')).toBe('light');
  });
});
