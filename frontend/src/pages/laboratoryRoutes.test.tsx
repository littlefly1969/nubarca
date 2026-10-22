// UX-02 §4: Plates lives inside the Laboratory workspace, and its sections are
// routes rather than component state.
//
// This harness mirrors the /lab subtree App.tsx declares — parent, index
// redirect, the Plates child, and the preserved /plates deep link — with a stub
// leaf so the real workspace's API mocks are not the subject. That the
// paths exist in the REAL router is covered separately, in App.routes.test.tsx.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import {
  Navigate, Route, RouterProvider, Routes, createMemoryRouter, useLocation,
} from 'react-router';
import { LAB_DEFAULT_ROUTE, LAB_SECTIONS, LaboratoryPage } from './LaboratoryPage';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

beforeEach(() => {
  installFetchMock({
    'GET /api/plates/images': () => jsonResponse([]),
    '* ': () => jsonResponse([]),
  });
});

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}

function currentLocation(): string {
  return screen.getByTestId('location').textContent ?? '';
}

/** The /lab subtree exactly as App.tsx declares it, plus a probe. */
function renderLab(entries: string[]) {
  const router = createMemoryRouter(
    [
      {
        path: '*',
        element: (
          <>
            <LocationProbe />
            <Routes>
              <Route path="/lab" element={<LaboratoryPage />}>
                <Route index element={<Navigate to={LAB_DEFAULT_ROUTE} replace />} />
                <Route path="plates" element={<div>plates workspace</div>} />
              </Route>
              <Route path="/plates" element={<Navigate to="/lab/plates" replace />} />
            </Routes>
          </>
        ),
      },
    ],
    { initialEntries: entries },
  );
  const view = render(
    <AuthedWrapper>
      <RouterProvider router={router} />
    </AuthedWrapper>,
  );
  return { ...view, router };
}

describe('Laboratory workspace', () => {
  it('offers the Plates section', () => {
    expect(LAB_SECTIONS.map((s) => s.to)).toEqual(['/lab/plates']);
    expect(LAB_DEFAULT_ROUTE).toBe('/lab/plates');
  });

  it('shows one common heading and a tab per section', async () => {
    renderLab(['/lab/plates']);
    expect(await screen.findByRole('heading', { name: 'Laboratorio' })).toBeInTheDocument();
    const tabs = screen.getByRole('navigation', { name: 'Sezioni del laboratorio' });
    expect(tabs).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Targhe' })).toHaveAttribute('href', '/lab/plates');
    expect(screen.queryByRole('link', { name: 'Estetica' })).not.toBeInTheDocument();
  });

  it('redirects a bare /lab to the default section', async () => {
    renderLab(['/lab']);
    await waitFor(() => expect(currentLocation()).toBe('/lab/plates'));
    expect(screen.getByText('plates workspace')).toBeInTheDocument();
  });

  it('preserves the old /plates deep link', async () => {
    renderLab(['/plates']);
    await waitFor(() => expect(currentLocation()).toBe('/lab/plates'));
    expect(screen.getByText('plates workspace')).toBeInTheDocument();
  });

  it('marks the selected section for assistive technology', async () => {
    renderLab(['/lab/plates']);
    await waitFor(() => expect(currentLocation()).toBe('/lab/plates'));
    expect(screen.getByRole('link', { name: 'Targhe' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Targhe' })).toHaveClass('is-active');
  });
});
