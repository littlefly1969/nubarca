import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { LegacySharedAlbumsRedirect } from './LegacySharedAlbumsRedirect';

afterEach(cleanup);

// "Shared with me" stopped being a destination. Every link, bookmark and message
// that already points at the old address has to keep working — and the per-album
// route has to keep pointing at the RECIPIENT's authority rather than being
// folded into the owner's.

function Probe({ label }: { label: string }) {
  const location = useLocation();
  return (
    <div>
      <span data-testid="where">{label}</span>
      <span data-testid="search">{location.search}</span>
    </div>
  );
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={['/somewhere', path]}>
      <Routes>
        <Route path="/somewhere" element={<Probe label="somewhere" />} />
        <Route path="/albums" element={<Probe label="albums" />} />
        <Route path="/shared-albums" element={<LegacySharedAlbumsRedirect />} />
        <Route path="/shared-albums/:albumId" element={<Probe label="shared-detail" />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('legacy /shared-albums', () => {
  it('lands on the shared collection of the one Albums page', () => {
    renderAt('/shared-albums');

    expect(screen.getByTestId('where')).toHaveTextContent('albums');
    expect(screen.getByTestId('search')).toHaveTextContent('?scope=shared');
  });

  it('leaves the per-album deep link exactly where it was', () => {
    // The recipient's album is backed by the recipient's authority. Redirecting
    // it into /albums/{id} would resolve one URL to the OWNER's route.
    renderAt('/shared-albums/alb-1');

    expect(screen.getByTestId('where')).toHaveTextContent('shared-detail');
  });
});
