import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyMessagesPage } from './PartyMessagesPage';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const LIST = '/api/albums/a1/party-messages';

function message(over: Record<string, unknown> = {}) {
  return {
    id: 'm1',
    displayName: 'Giulia',
    text: 'Serata fantastica!',
    status: 'visible',
    createdAt: '2026-01-01T20:00:00Z',
    moderatedAt: null,
    isHero: false,
    heroPromotedAt: null,
    ...over,
  };
}

function list(over: Record<string, unknown> = {}) {
  return {
    albumId: 'a1',
    partyActive: true,
    requireMessageApproval: false,
    isOwner: true,
    items: [message()],
    ...over,
  };
}

function renderPage() {
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/albums/a1/party-messages']}>
        <Routes>
          <Route path="/albums/:albumId/party-messages" element={<PartyMessagesPage />} />
          <Route path="/albums" element={<p>albums index</p>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('party messages moderation page', () => {
  it('shows a guest message with its author, text and state', async () => {
    installFetchMock({ [`GET ${LIST}`]: () => jsonResponse(list()) });
    renderPage();

    const row = await screen.findByTestId('party-message-row');
    expect(row).toHaveTextContent('Giulia');
    expect(row).toHaveTextContent('Serata fantastica!');
    expect(row).toHaveTextContent(/live/i);
  });

  it('calls an unsigned message a guest rather than showing an empty author', async () => {
    installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ items: [message({ displayName: null })] })),
    });
    renderPage();

    expect(await screen.findByTestId('party-message-row')).toHaveTextContent(/ospite/i);
  });

  it('approves and rejects a waiting message', async () => {
    let items = [message({ status: 'pending' })];
    const mock = installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ requireMessageApproval: true, items })),
      [`POST ${LIST}/m1/approve`]: () => { items = [message({ status: 'visible' })]; return emptyResponse(); },
      [`POST ${LIST}/m1/reject`]: () => emptyResponse(),
    });
    renderPage();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: /^approva$/i }));
    await waitFor(() =>
      expect(screen.getByTestId('party-message-row')).toHaveTextContent(/live/i));

    expect(mock.calls.some((c) => c.url === `${LIST}/m1/approve` && c.method === 'POST')).toBe(true);
    // Approve/reject are offered only while the message is waiting.
    expect(screen.queryByRole('button', { name: /rifiuta/i })).toBeNull();
  });

  it('hides and restores a live message', async () => {
    let items = [message()];
    installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ items })),
      [`POST ${LIST}/m1/hide`]: () => { items = [message({ status: 'hidden' })]; return emptyResponse(); },
      [`POST ${LIST}/m1/restore`]: () => { items = [message({ status: 'visible' })]; return emptyResponse(); },
    });
    renderPage();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: /nascondi/i }));
    await screen.findByRole('button', { name: /ripristina/i });
    await user.click(screen.getByRole('button', { name: /ripristina/i }));
    await waitFor(() =>
      expect(screen.getByTestId('party-message-row')).toHaveTextContent(/live/i));
  });

  it('offers Hero only on a live message, and demotion once promoted', async () => {
    let items = [message({ status: 'pending' })];
    installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ items })),
      [`POST ${LIST}/m1/approve`]: () => { items = [message()]; return emptyResponse(); },
      [`POST ${LIST}/m1/promote-hero`]: () => {
        items = [message({ isHero: true, heroPromotedAt: '2026-01-01T21:00:00Z' })];
        return emptyResponse();
      },
      [`POST ${LIST}/m1/demote-hero`]: () => { items = [message()]; return emptyResponse(); },
    });
    renderPage();
    const user = userEvent.setup();

    // A waiting message cannot be promoted — the server refuses it, and the UI
    // does not offer it either.
    await screen.findByTestId('party-message-row');
    expect(screen.queryByRole('button', { name: /promuovi hero/i })).toBeNull();

    await user.click(screen.getByRole('button', { name: /^approva$/i }));
    await user.click(await screen.findByRole('button', { name: /promuovi hero/i }));

    await waitFor(() =>
      expect(screen.getByTestId('party-message-row')).toHaveTextContent(/hero/i));
    await user.click(screen.getByRole('button', { name: /rimuovi hero/i }));
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: /rimuovi hero/i })).toBeNull());
  });

  it('filters by state without a round-trip', async () => {
    const mock = installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({
        items: [
          message({ id: 'm1', text: 'Live one' }),
          message({ id: 'm2', text: 'Waiting one', status: 'pending' }),
          message({ id: 'm3', text: 'Hidden one', status: 'hidden' }),
          message({ id: 'm4', text: 'Hero one', isHero: true, heroPromotedAt: '2026-01-01T21:00:00Z' }),
        ],
      })),
    });
    renderPage();
    const user = userEvent.setup();

    await waitFor(() => expect(screen.getAllByTestId('party-message-row')).toHaveLength(4));

    await user.click(screen.getByRole('tab', { name: /in attesa/i }));
    expect(screen.getAllByTestId('party-message-row')).toHaveLength(1);
    expect(screen.getByTestId('party-message-row')).toHaveTextContent('Waiting one');

    await user.click(screen.getByRole('tab', { name: /^hero$/i }));
    expect(screen.getByTestId('party-message-row')).toHaveTextContent('Hero one');

    await user.click(screen.getByRole('tab', { name: /nascosti/i }));
    expect(screen.getByTestId('party-message-row')).toHaveTextContent('Hidden one');

    // Filtering is local: only the initial load hit the network.
    expect(mock.calls.filter((c) => c.url === LIST)).toHaveLength(1);
  });

  it('lets the OWNER change the approval mode without touching the party tokens', async () => {
    let requireMessageApproval = false;
    const mock = installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ requireMessageApproval })),
      'PATCH /api/albums/a1/party-settings': () => {
        requireMessageApproval = true;
        return jsonResponse({ albumId: 'a1', requireMessageApproval: true });
      },
    });
    renderPage();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText(/i messaggi degli ospiti richiedono approvazione/i));
    await waitFor(() =>
      expect(screen.getByLabelText(/richiedono approvazione/i)).toBeChecked());

    // `enabled: true` keeps the party on and the tokens stable; nothing else in
    // the party's configuration is sent.
    const patch = mock.calls.find((c) => c.method === 'PATCH');
    expect(JSON.parse(patch!.body!)).toEqual({ enabled: true, requireMessageApproval: true });
  });

  it('shows a DELEGATE the queue but never the owner-only approval switch', async () => {
    installFetchMock({ [`GET ${LIST}`]: () => jsonResponse(list({ isOwner: false })) });
    renderPage();

    await screen.findByTestId('party-message-row');
    expect(screen.getByTestId('party-messages-delegate-notice')).toBeInTheDocument();
    // The approval mode is a party SETTING, and settings stay with the owner.
    expect(screen.queryByTestId('message-approval-toggle')).toBeNull();
    // Moderation itself is fully available to them.
    expect(screen.getByRole('button', { name: /nascondi/i })).toBeEnabled();
  });

  it('separates "no party running" from "nobody has written anything"', async () => {
    installFetchMock({
      [`GET ${LIST}`]: () => jsonResponse(list({ partyActive: false, items: [] })),
    });
    const { unmount } = renderPage();
    expect(await screen.findByTestId('party-messages-no-party')).toBeInTheDocument();
    expect(screen.queryByTestId('party-messages-empty')).toBeNull();
    unmount();

    installFetchMock({ [`GET ${LIST}`]: () => jsonResponse(list({ items: [] })) });
    renderPage();
    expect(await screen.findByTestId('party-messages-empty')).toBeInTheDocument();
  });

  it('sends an unauthorised caller back to the albums index rather than explaining', async () => {
    // The server answers one generic 404 for "no such album" and "not yours to
    // manage" alike; the page must not invent a distinction it was not told.
    installFetchMock({ [`GET ${LIST}`]: () => errorResponse(404) });
    renderPage();

    expect(await screen.findByText('albums index')).toBeInTheDocument();
  });
});
