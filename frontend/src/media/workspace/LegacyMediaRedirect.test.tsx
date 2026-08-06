import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';
import { LegacyMediaRedirect } from './LegacyMediaRedirect';

afterEach(cleanup);

function LocationProbe() {
  const loc = useLocation();
  return <div data-testid="loc">{loc.pathname + loc.search}</div>;
}

function renderAt(entry: string, kind: 'image' | 'video', scope: 'active' | 'excluded') {
  render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/gallery" element={<LegacyMediaRedirect kind={kind} scope={scope} />} />
        <Route path="/gallery/excluded" element={<LegacyMediaRedirect kind={kind} scope={scope} />} />
        <Route path="/videos" element={<LegacyMediaRedirect kind={kind} scope={scope} />} />
        <Route path="/videos/excluded" element={<LegacyMediaRedirect kind={kind} scope={scope} />} />
        <Route path="/media" element={<LocationProbe />} />
        <Route path="/media/excluded" element={<LocationProbe />} />
      </Routes>
    </MemoryRouter>,
  );
  return screen.getByTestId('loc').textContent ?? '';
}

describe('LegacyMediaRedirect', () => {
  it('/gallery → /media?kind=image, preserving compatible params', () => {
    const loc = renderAt('/gallery?q=dog&sort=name&direction=asc', 'image', 'active');
    expect(loc).toContain('/media?');
    expect(loc).toContain('kind=image');
    expect(loc).toContain('q=dog');
    expect(loc).toContain('sort=name');
    expect(loc).toContain('direction=asc');
  });

  it('/videos → /media?kind=video', () => {
    const loc = renderAt('/videos', 'video', 'active');
    expect(loc).toBe('/media?kind=video');
  });

  it('/gallery/excluded → /media/excluded?kind=image', () => {
    const loc = renderAt('/gallery/excluded', 'image', 'excluded');
    expect(loc).toBe('/media/excluded?kind=image');
  });

  it('/videos/excluded → /media/excluded?kind=video and drops a stray scope param', () => {
    const loc = renderAt('/videos/excluded?scope=active&q=x', 'video', 'excluded');
    expect(loc).toContain('/media/excluded?');
    expect(loc).toContain('kind=video');
    expect(loc).toContain('q=x');
    expect(loc).not.toContain('scope=');
  });
});
