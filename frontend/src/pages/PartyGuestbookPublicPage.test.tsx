import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyGuestbookPublicPage } from './PartyGuestbookPublicPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

const guestbookCss = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), 'PartyGuestbook.css'), 'utf8');

/**
 * The guest's side of the book, as a book of photograph memories.
 *
 * What these defend: a party that keeps no book says so and offers nothing; a
 * book that is readable but closed says WHICH of the two it is; the book leads
 * with one call to leave a memory rather than a form; and every memory is
 * drawn from the read model alone — photograph, framing, design, paragraphs —
 * one per row.
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

function memory(over: Record<string, unknown> = {}) {
  return {
    id: 'g1',
    authorDisplayName: 'Ada',
    body: 'Che serata',
    createdAt: '2026-09-19T21:00:00Z',
    template: { key: 'nubarca', version: 1 },
    media: {
      url: '/api/party/tok-1/guestbook/g1/photo',
      width: 1600,
      height: 1200,
      orientation: 'landscape',
      crop: { centerX: 0.5, centerY: 0.5, zoom: 1 },
    },
    ...over,
  };
}

function page(over: Record<string, unknown> = {}) {
  return {
    entries: [],
    canWrite: true,
    maxAuthorDisplayNameLength: 80,
    maxBodyLength: 1000,
    ...over,
  };
}

function mock(over: Record<string, unknown> = {}) {
  installFetchMock({
    'GET /api/party/tok-1': () => jsonResponse(context),
    'GET /api/party/tok-1/guestbook': () => jsonResponse(page(over)),
  });
}

describe('PartyGuestbookPublicPage (the guest book, as a guest reads it)', () => {
  it('names the party and leads with one call to leave a memory, not a form', async () => {
    mock();
    render(wrapper());

    expect(await screen.findAllByText(/Un ricordo per Beach Party/i)).not.toHaveLength(0);
    const start = await screen.findByTestId('party-guestbook-start');
    expect(start).toHaveTextContent('Lascia un ricordo');
    expect(start).toBeEnabled();
    // The composer is not on the page until somebody asks for it.
    expect(screen.queryByTestId('guestbook-compose')).not.toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
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
    expect(screen.queryByTestId('party-guestbook-start')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-guestbook-error')).not.toBeInTheDocument();
  });

  it('keeps a finished party’s book readable while closing it to new memories', async () => {
    mock({ canWrite: false, entries: [memory()] });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-closed')).toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-list')).toHaveTextContent('Che serata');
    expect(screen.queryByTestId('party-guestbook-start')).not.toBeInTheDocument();
  });

  it('tells a guest, while the book is closed to the room, that these are THEIR memories', async () => {
    mock({ scope: 'mine', entries: [memory({ body: 'Il mio ricordo' })] });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-scope-mine'))
      .toHaveTextContent('Durante la festa vedi i ricordi che hai lasciato tu.');
    expect(screen.getByRole('heading', { name: 'I tuoi ricordi' })).toBeInTheDocument();
    expect(screen.getByTestId('party-guestbook-list')).toHaveTextContent('Il mio ricordo');
    // Writing is untouched by the room's visibility.
    expect(screen.getByTestId('party-guestbook-start')).toBeEnabled();
  });

  it('says a guest has left nothing yet, rather than that the book is empty', async () => {
    mock({ scope: 'mine', entries: [] });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-empty'))
      .toHaveTextContent('Non hai ancora lasciato un ricordo.');
  });

  it('reads the whole book, as a book, when the regia has opened it', async () => {
    mock({ scope: 'all', entries: [memory(), memory({ id: 'g2', authorDisplayName: 'Bo', body: 'Auguri' })] });
    render(wrapper());

    const list = await screen.findByTestId('party-guestbook-list');
    expect(list.querySelectorAll('figure.guestbook-memory')).toHaveLength(2);
    expect(screen.queryByTestId('party-guestbook-scope-mine')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'I ricordi' })).toBeInTheDocument();
    // A book, not a feed: nothing to like, react to or rank.
    expect(document.body.textContent ?? '').not.toMatch(/mi piace|like|commenta|reazion|classifica/i);
  });

  it('does not invite a guest who has written every memory they were allowed', async () => {
    mock({ remaining: 0 });
    render(wrapper());

    expect(await screen.findByTestId('party-guestbook-start')).toBeDisabled();
    expect(screen.getByTestId('party-guestbook-remaining')).toHaveTextContent('0');
  });

  it('draws every memory from the read model alone: photograph, design, paragraphs, signature', async () => {
    mock({
      entries: [
        memory({
          id: 'g2',
          authorDisplayName: 'Giulia',
          body: 'Riga uno\n\nRiga due',
          template: { key: 'polaroid', version: 1 },
          media: {
            url: '/api/party/tok-1/guestbook/g2/photo',
            width: 1200, height: 1600, orientation: 'portrait',
            crop: { centerX: 0.5, centerY: 0.3, zoom: 2 },
          },
        }),
        memory(),
      ],
    });
    render(wrapper());

    const card = await screen.findByTestId('party-guestbook-memory-g2');
    expect(card).toHaveAttribute('data-template', 'polaroid');
    expect(card).toHaveAttribute('data-template-version', '1');
    const photo = within(card).getByRole('img');
    expect(photo).toHaveAttribute('src', '/api/party/tok-1/guestbook/g2/photo');
    expect(photo).toHaveAttribute('alt', 'La foto scelta da Giulia');
    // Framed as its author framed it: a 2x zoom makes the picture twice the
    // frame's width, and the frame is the polaroid's square.
    expect(photo.style.width).toBe('200%');
    expect(within(card).getByTestId('party-guestbook-memory-g2-body').textContent).toBe('Riga uno\n\nRiga due');
    expect(within(card).getByTestId('party-guestbook-memory-g2-author')).toHaveTextContent('Giulia');
  });

  it('keeps the book one memory per row, a book and not a feed', async () => {
    mock({ entries: [memory({ id: 'a' }), memory({ id: 'b' }), memory({ id: 'c' })] });
    render(wrapper());

    const list = await screen.findByTestId('party-guestbook-list');
    expect(within(list).getAllByRole('listitem')).toHaveLength(3);
    // The rule itself: one column at EVERY width — there is no media query
    // that widens the list into a grid, and paragraphs are kept as written.
    expect(guestbookCss).toMatch(/\.party-guestbook-list\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
    expect(guestbookCss).not.toMatch(/@media[^{]*\{\s*\.party-guestbook-list/);
    expect(guestbookCss).toMatch(/\.guestbook-memory-body\s*\{[^}]*white-space:\s*pre-wrap/);
    // …and nothing social: no reactions, no counts.
    expect(screen.queryByRole('button', { name: /mi piace|like/i })).not.toBeInTheDocument();
  });

  it('draws a memory whose design this client does not know yet, rather than nothing', async () => {
    mock({ entries: [memory({ template: { key: 'polaroid', version: 9 } })] });
    render(wrapper());

    const card = await screen.findByTestId('party-guestbook-memory-g1');
    expect(card).toHaveAttribute('data-template', 'polaroid');
    expect(within(card).getByRole('img')).toBeInTheDocument();
  });
});
