import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyPage } from './PartyPage';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';

// The party's OWN QR after the guest list arrived — and it must be exactly the
// page it was. The personal invitation reuses this page's invitation surface;
// it does not change what the QR opens, never draws a reply form here, and the
// page never asks the personal-invitation API anything.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

function page() {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={['/party/tok-1']}>
        <Routes>
          <Route path="/party/:token" element={<PartyPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const context = (over: Record<string, unknown> = {}) => ({
  title: 'Matrimonio di Marta', phase: 'before', accessMode: 'full', eventStartsAt: null,
  albumName: null, itemCount: 0, coverUrl: null, content: [],
  capabilities: { contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false },
  library: { available: false, accessEndsAt: null },
  ...over,
});

describe('the party QR next to the guest list', () => {
  it('opens its own invitation with no reply form and no personal-invitation request', async () => {
    const mock = installFetchMock({ 'GET /api/party/tok-1': () => jsonResponse(context()) });
    render(page());

    expect(await screen.findByTestId('party-before')).toBeInTheDocument();
    expect(screen.getByText('Sei invitato')).toBeInTheDocument();
    // The QR's own promise, unchanged: this page becomes the party.
    expect(screen.getByText('Tieni questa pagina: diventerà la festa quando cominciamo.')).toBeInTheDocument();
    expect(screen.queryByTestId('party-rsvp')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/api/party-invitations/'))).toBe(false);
  });

  it('at the party, still opens the live hub and nothing of a guest list', async () => {
    const mock = installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(context({
        phase: 'live', albumName: 'Album della festa', itemCount: 0,
        capabilities: { contributionUrl: '/party/up-1/upload', gameUrl: null, printUrl: null, faceSearch: true },
      })),
      'GET /api/party/tok-1/items': () => jsonResponse({ albumName: 'Album della festa', items: [] }),
    });
    render(page());

    expect(await screen.findByTestId('party-capability-album')).toBeInTheDocument();
    expect(screen.getByTestId('party-hub-cta')).toHaveAttribute('href', '/party/up-1/upload');
    expect(screen.queryByTestId('party-rsvp')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('/api/party-invitations/'))).toBe(false);
  });
});
