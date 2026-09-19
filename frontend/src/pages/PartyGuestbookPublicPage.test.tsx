import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PARTY_GUESTBOOK_LIMITS } from '@nubarca/contracts';
import { PartyGuestbookPublicPage } from './PartyGuestbookPublicPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

/**
 * The guest's side of the book.
 *
 * What these defend: a party that keeps no book says so and offers no form; a
 * book that is readable but closed to new writing says WHICH of the two it is;
 * a dedication that needs approving is acknowledged as waiting rather than as
 * published; and the page says which of the two written contributions it is,
 * because a greeting and a dedication are different invitations and a guest
 * chooses between them.
 */
afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

function wrapper(token = 'tok-1') {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${token}/guestbook`]}>
        <Routes>
          <Route path="/party/:token/guestbook" element={<PartyGuestbookPublicPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const context = {
  title: 'Beach Party',
  phase: 'live',
  accessMode: 'full',
  eventStartsAt: null,
  albumName: 'Beach Party',
  itemCount: 0,
  coverUrl: null,
  content: [],
  capabilities: {
    contributionUrl: null, gameUrl: null, printUrl: null, faceSearch: false,
    slideshowMessageUrl: null, guestbookUrl: '/party/tok-1/guestbook',
  },
  library: { available: false, accessEndsAt: null },
};

function page(over: Record<string, unknown> = {}) {
  return {
    entries: [],
    canWrite: true,
    maxAuthorDisplayNameLength: 80,
    maxBodyLength: 1000,
    ...over,
  };
}

function mock(over: Record<string, unknown> = {}, submit?: () => Response) {
  installFetchMock({
    'GET /api/party/tok-1': () => jsonResponse(context),
    'GET /api/party/tok-1/guestbook': () => jsonResponse(page(over)),
    'POST /api/party/tok-1/guestbook': submit
      ?? (() => jsonResponse({ id: 'g1', status: 'visible', createdAt: '2026-09-19T21:00:00Z' })),
  });
}

describe('PartyGuestbookPublicPage (the guest book, as a guest reads it)', () => {
  it('names the party it is a book for, and offers the composer', async () => {
    mock();
    render(wrapper());

    expect(await screen.findAllByText(/Un ricordo per Beach Party/i)).not.toHaveLength(0);
    expect(screen.getByTestId('party-guestbook-form')).toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-empty')).toBeInTheDocument();
  });

  it('says a party keeps no book without hinting that one exists elsewhere', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(context),
      // 404 is the one answer for an unknown token, a revoked party and a
      // party with the book switched off.
      'GET /api/party/tok-1/guestbook': () => errorResponse(404),
    });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-unavailable')).toBeInTheDocument();
    expect(screen.queryByTestId('party-guestbook-form')).not.toBeInTheDocument();
    // No retry, no error tone: nothing went wrong, there is simply no book.
    expect(screen.queryByTestId('party-guestbook-error')).not.toBeInTheDocument();
  });

  it('keeps a finished party’s book readable while closing it to new dedications', async () => {
    mock({
      canWrite: false,
      entries: [
        { id: 'g1', authorDisplayName: 'Ada', body: 'Che serata', createdAt: '2026-09-19T21:00:00Z' },
      ],
    });
    render(wrapper());

    // A keepsake outlives the composer that filled it, so "you may read this"
    // and "you may add to this" are two answers and the page gives both.
    expect(await screen.findByTestId('party-guestbook-closed')).toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-list')).toHaveTextContent('Che serata');
    expect(screen.queryByTestId('party-guestbook-form')).not.toBeInTheDocument();
  });

  it('signs an unsigned dedication as a guest rather than leaving a blank', async () => {
    mock({
      entries: [
        { id: 'g1', authorDisplayName: null, body: 'Auguri', createdAt: '2026-09-19T21:00:00Z' },
      ],
    });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-list')).toHaveTextContent('Un ospite');
  });

  it('sends a dedication and says it is in the book', async () => {
    mock();
    render(wrapper());
    const user = userEvent.setup();

    await user.type(await screen.findByTestId('party-guestbook-body'), 'Grazie di tutto');
    await user.type(screen.getByTestId('party-guestbook-name'), 'Ada');
    await user.click(screen.getByTestId('party-guestbook-submit'));

    const sent = await screen.findByTestId('party-guestbook-sent');
    expect(sent).toHaveTextContent(/nel guestbook/i);
    expect(screen.getByTestId('party-guestbook-write-another')).toBeInTheDocument();
  });

  it('acknowledges a dedication that is waiting as waiting, not as published', async () => {
    mock({}, () => jsonResponse({ id: 'g1', status: 'pending', createdAt: '2026-09-19T21:00:00Z' }));
    render(wrapper());
    const user = userEvent.setup();

    await user.type(await screen.findByTestId('party-guestbook-body'), 'In attesa');
    await user.click(screen.getByTestId('party-guestbook-submit'));

    // Telling somebody their words are up when a host has not read them yet
    // is the one thing this screen must not do.
    const sent = await screen.findByTestId('party-guestbook-sent');
    expect(sent).toHaveTextContent(/dopo un controllo/i);
    expect(sent).not.toHaveTextContent(/è nel guestbook/i);
  });

  it('refuses to send a blank dedication without asking the server', async () => {
    let posts = 0;
    mock({}, () => { posts += 1; return jsonResponse({ id: 'g1', status: 'visible', createdAt: '' }); });
    render(wrapper());

    const submit = await screen.findByTestId('party-guestbook-submit');
    expect(submit).toBeDisabled();
    expect(posts).toBe(0);
  });

  it('counts down to the limit and refuses past it, before the server has to', async () => {
    mock();
    render(wrapper());
    const user = userEvent.setup();

    // The limit comes from the SHARED CONTRACT, not from the page the server
    // answered: both sides enforce the same number, so a guest is stopped by
    // the same rule that would refuse them a moment later.
    const limit = PARTY_GUESTBOOK_LIMITS.maxBodyLength;
    await user.type(await screen.findByTestId('party-guestbook-body'), '12345');
    expect(screen.getByTestId('party-guestbook-counter'))
      .toHaveTextContent(new RegExp(`Restano ${limit - 5} caratteri`, 'i'));

    fireEvent.change(screen.getByTestId('party-guestbook-body'), {
      target: { value: 'a'.repeat(limit + 1) },
    });
    expect(screen.getByTestId('party-guestbook-counter'))
      .toHaveTextContent(new RegExp(`al massimo di ${limit}`, 'i'));
    expect(screen.getByTestId('party-guestbook-submit')).toBeDisabled();
  });

  it('says plainly when the book was closed between opening the page and writing', async () => {
    mock({}, () => errorResponse(409, { error: 'guestbook_disabled' }));
    render(wrapper());
    const user = userEvent.setup();

    await user.type(await screen.findByTestId('party-guestbook-body'), 'Tardi');
    await user.click(screen.getByTestId('party-guestbook-submit'));

    // A stable code from the server, translated here — never the code itself.
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/è stato chiuso/i));
    expect(screen.queryByTestId('party-guestbook-sent')).not.toBeInTheDocument();
  });

  it('asks somebody who wrote too fast to wait, rather than blaming the dedication', async () => {
    mock({}, () => errorResponse(429, { error: 'too_many_requests' }));
    render(wrapper());
    const user = userEvent.setup();

    await user.type(await screen.findByTestId('party-guestbook-body'), 'Ancora');
    await user.click(screen.getByTestId('party-guestbook-submit'));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/Troppe dediche/i));
  });

  it('says which of the two written contributions this one is', async () => {
    mock();
    render(wrapper());
    await screen.findByTestId('party-guestbook-form');

    // The two written contributions are different invitations: a greeting is
    // read out during the evening, a dedication is kept. A guest picks between
    // them from the hub, so the book states the difference rather than leaving
    // somebody to write the same thing twice and wonder why only one appeared.
    const text = document.body.textContent ?? '';
    expect(text).toMatch(/Non finisce sullo schermo/i);
    // …and it never calls itself the thing it is not.
    expect(screen.queryByText(/Comparirà sullo schermo durante la festa/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Lascia un messaggio/i)).not.toBeInTheDocument();
  });
});
