import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PARTY_GUESTBOOK_LIMITS } from '@nubarca/contracts';
import { PartyGuestbookPanel } from './PartyGuestbookPanel';
import {
  errorResponse, installFetchMock, jsonResponse, type FetchSpyEntry, type MockRequest,
} from '../test-utils';
import { I18nProvider } from '../i18n';

/**
 * Making a memory, on a phone, from the book's own call to action.
 *
 * What these defend: the guest only ever chooses a photograph and words — the
 * server resolves everything else; the preview IS the memory and follows the
 * design at once; nothing typed is lost to a refusal or a dropped network; a
 * photograph removed while the composer was open sends the guest back to
 * choose another WITH their words; and a closed book is said to be closed.
 */
afterEach(() => {
  cleanup();
  window.localStorage.clear();
});

const PHOTOS = [
  {
    id: 'p1', thumbnailUrl: '/api/party/tok-1/guestbook/photos/p1/thumbnail',
    previewUrl: '/api/party/tok-1/guestbook/photos/p1/preview',
    width: 1600, height: 1200, orientation: 'landscape',
  },
  {
    id: 'p2', thumbnailUrl: '/api/party/tok-1/guestbook/photos/p2/thumbnail',
    previewUrl: '/api/party/tok-1/guestbook/photos/p2/preview',
    width: 1200, height: 1600, orientation: 'portrait',
  },
];

function published(body: Record<string, unknown>, status = 'visible') {
  return {
    id: 'g-new',
    status,
    createdAt: '2026-10-01T21:00:00Z',
    remaining: null,
    entry: {
      id: 'g-new',
      authorDisplayName: body.authorDisplayName,
      body: body.body,
      createdAt: '2026-10-01T21:00:00Z',
      template: { key: body.templateKey, version: 1 },
      media: {
        url: '/api/party/tok-1/guestbook/g-new/photo',
        width: 1600, height: 1200, orientation: 'landscape',
        crop: body.crop,
      },
    },
  };
}

type Submit = (call: MockRequest) => Response | Promise<Response>;

function mock({
  submit, entries = [], remaining, photos,
}: {
  submit?: Submit;
  entries?: unknown[];
  remaining?: number;
  photos?: () => Promise<Response>;
} = {}) {
  let book = entries;
  return installFetchMock({
    'GET /api/party/tok-1/guestbook': () => jsonResponse({
      entries: book, canWrite: true, maxAuthorDisplayNameLength: 80, maxBodyLength: 1000,
      remaining: remaining ?? null,
    }),
    'GET /api/party/tok-1/guestbook/photos': photos ?? (() => jsonResponse({ photos: PHOTOS })),
    'POST /api/party/tok-1/guestbook': async (call) => {
      const response = submit
        ? await submit(call)
        : jsonResponse(published(JSON.parse(call.body!)));
      if (response.ok) {
        const created = await response.clone().json();
        book = [created.entry, ...book];
      }
      return response;
    },
  });
}

function renderBook() {
  return render(
    <I18nProvider>
      <PartyGuestbookPanel token="tok-1" />
    </I18nProvider>,
  );
}

async function openComposerAndChoose(user: ReturnType<typeof userEvent.setup>, photoId = 'p1') {
  await user.click(await screen.findByTestId('party-guestbook-start'));
  await user.click(await screen.findByTestId(`guestbook-photo-${photoId}`));
  return screen.findByTestId('guestbook-compose');
}

async function write(user: ReturnType<typeof userEvent.setup>, body: string, name: string) {
  await user.type(screen.getByTestId('guestbook-body'), body);
  await user.type(screen.getByTestId('guestbook-name'), name);
}

function lastPost(calls: FetchSpyEntry[]) {
  const post = [...calls].reverse().find((c) => c.method === 'POST');
  return post ? JSON.parse(post.body!) : null;
}

describe('PartyGuestbookComposer (making a memory)', () => {
  it('opens on the party’s photographs, loading first, from the photo-only chooser', async () => {
    let release: () => void = () => {};
    const arrived = new Promise<void>((resolve) => { release = resolve; });
    const { calls } = mock({ photos: async () => { await arrived; return jsonResponse({ photos: PHOTOS }); } });
    renderBook();
    const user = userEvent.setup();

    await user.click(await screen.findByTestId('party-guestbook-start'));
    // A loading state before the photographs arrive…
    expect(await screen.findByTestId('guestbook-picker-loading')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Scegli una foto' })).toBeInTheDocument();
    release();

    // …then only what the chooser offered: the server's photographs, never an
    // album to choose and never the album's mixed media list.
    const grid = await screen.findByTestId('guestbook-picker-grid');
    expect(within(grid).getAllByRole('button')).toHaveLength(2);
    expect(within(grid).getAllByRole('button')[0]).toHaveAccessibleName('Foto 1 di 2');
    expect(calls.some((c) => c.url === '/api/party/tok-1/guestbook/photos')).toBe(true);
    expect(calls.some((c) => /\/items|\/media\//.test(c.url))).toBe(false);
  });

  it('turns a tap on a photograph into a memory at once, with the four designs to choose from', async () => {
    mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    expect(screen.getByRole('heading', { name: 'Crea il ricordo' })).toBeInTheDocument();

    const preview = screen.getByTestId('guestbook-preview-memory');
    expect(within(preview).getByRole('img')).toHaveAttribute('src', PHOTOS[0].previewUrl);
    expect(preview).toHaveAttribute('data-template', 'nubarca');

    const templates = within(screen.getByTestId('guestbook-templates')).getAllByRole('radio');
    expect(templates.map((radio) => radio.getAttribute('value')))
      .toEqual(['nubarca', 'polaroid', 'editorial', 'celebration']);
    expect(screen.getByRole('radio', { name: /NubArca/ })).toBeChecked();

    // Changing the design changes the preview immediately — no third screen.
    await user.click(screen.getByTestId('guestbook-template-polaroid'));
    expect(screen.getByTestId('guestbook-preview-memory')).toHaveAttribute('data-template', 'polaroid');
    expect(screen.getByRole('radio', { name: /Polaroid/ })).toBeChecked();
    await user.click(screen.getByTestId('guestbook-template-celebration'));
    expect(screen.getByTestId('guestbook-preview-memory')).toHaveAttribute('data-template', 'celebration');
  });

  it('shows the dedication with its paragraphs and the signature in the preview', async () => {
    mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Riga uno{Enter}{Enter}Riga due', 'Ada');

    expect(screen.getByTestId('guestbook-preview-memory-body').textContent).toBe('Riga uno\n\nRiga due');
    expect(screen.getByTestId('guestbook-preview-memory-author')).toHaveTextContent('Ada');
  });

  it('keeps the publish button off until there is a photograph, a dedication and a signature within limits', async () => {
    const { calls } = mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    const publish = screen.getByTestId('guestbook-publish');
    expect(publish).toHaveTextContent('Pubblica nel guestbook');
    expect(publish).toBeDisabled();
    expect(screen.getByTestId('guestbook-incomplete')).toBeInTheDocument();

    await user.type(screen.getByTestId('guestbook-body'), 'Auguri');
    expect(publish).toBeDisabled(); // unsigned
    await user.type(screen.getByTestId('guestbook-name'), 'Ada');
    expect(publish).toBeEnabled();

    // Over the limit — counted as the server counts — is off again, and said.
    fireEvent.change(screen.getByTestId('guestbook-body'), {
      target: { value: 'a'.repeat(PARTY_GUESTBOOK_LIMITS.maxBodyLength + 1) },
    });
    expect(publish).toBeDisabled();
    expect(screen.getByTestId('guestbook-body')).toHaveAttribute('aria-invalid', 'true');
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('publishes the photograph’s id, the design’s key and the framing — and nothing the server decides', async () => {
    const { calls } = mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await user.click(screen.getByTestId('guestbook-template-editorial'));
    await write(user, 'Per sempre', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    await screen.findByTestId('guestbook-sent');
    expect(lastPost(calls)).toEqual({
      sourceMediaItemId: 'p1',
      authorDisplayName: 'Ada',
      body: 'Per sempre',
      templateKey: 'editorial',
      crop: { centerX: 0.5, centerY: 0.5, zoom: 1 },
    });
  });

  it('frames a portrait photograph a little high by default, where the faces usually are', async () => {
    const { calls } = mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user, 'p2');
    await write(user, 'Ciao', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    await screen.findByTestId('guestbook-sent');
    expect(lastPost(calls).crop).toEqual({ centerX: 0.5, centerY: 0.42, zoom: 1 });
  });

  it('repositions with the keyboard and zooms with the slider, through the shared crop frame', async () => {
    const { calls } = mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await user.click(screen.getByTestId('guestbook-reposition-open'));
    const frame = screen.getByTestId('guestbook-crop');
    frame.focus();
    await user.keyboard('{ArrowRight}{ArrowDown}');
    fireEvent.change(screen.getByTestId('guestbook-zoom'), { target: { value: '2' } });
    await user.click(screen.getByTestId('guestbook-reposition-done'));
    // Back to the memory itself, framed anew.
    expect(screen.getByTestId('guestbook-preview-memory')).toBeInTheDocument();

    await write(user, 'Inquadrata', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));
    await screen.findByTestId('guestbook-sent');
    const crop = lastPost(calls).crop;
    expect(crop.centerX).toBeCloseTo(0.52);
    expect(crop.centerY).toBeCloseTo(0.52);
    expect(crop.zoom).toBe(2);
  });

  it('shows the new memory once the server has it, and the book has it on the way back', async () => {
    mock();
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Grazie di tutto', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    const sent = await screen.findByTestId('guestbook-sent');
    expect(within(sent).getByRole('heading')).toHaveTextContent('Il tuo ricordo è nel guestbook');
    expect(within(sent).getByTestId('guestbook-sent-memory')).toHaveTextContent('Grazie di tutto');
    expect(screen.getByTestId('guestbook-sent-another')).toBeInTheDocument();

    await user.click(screen.getByTestId('guestbook-sent-done'));
    const list = await screen.findByTestId('party-guestbook-list');
    expect(list).toHaveTextContent('Grazie di tutto');
    // Focus comes back to where the guest left from.
    await waitFor(() => expect(screen.getByTestId('party-guestbook-start')).toHaveFocus());
  });

  it('says a memory that waits for approval is waiting, not published', async () => {
    mock({ submit: (call) => jsonResponse(published(JSON.parse(call.body!), 'pending')) });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'In attesa', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    const sent = await screen.findByTestId('guestbook-sent');
    expect(sent).toHaveTextContent(/dopo un controllo/i);
    expect(sent).not.toHaveTextContent(/è nel guestbook/i);
  });

  it('sends the guest back to the photographs, words intact, when the photograph was removed meanwhile', async () => {
    let attempts = 0;
    const { calls } = mock({
      submit: (call) => {
        attempts += 1;
        return attempts === 1
          ? errorResponse(409, { error: 'guestbook_photo_unavailable' })
          : jsonResponse(published(JSON.parse(call.body!)));
      },
    });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user, 'p1');
    await user.click(screen.getByTestId('guestbook-template-polaroid'));
    await write(user, 'Che festa', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    // Back at the photographs, told why, with the vanished one no longer
    // selected.
    expect(await screen.findByTestId('guestbook-picker-notice'))
      .toHaveTextContent('Questa foto non è più disponibile. Scegline un’altra.');
    expect(screen.queryByTestId('guestbook-sent')).not.toBeInTheDocument();
    expect(await screen.findByTestId('guestbook-photo-p1')).toHaveAttribute('aria-pressed', 'false');

    // A new photograph, and everything else exactly as the guest left it.
    await user.click(screen.getByTestId('guestbook-photo-p2'));
    expect(screen.getByTestId('guestbook-body')).toHaveValue('Che festa');
    expect(screen.getByTestId('guestbook-name')).toHaveValue('Ada');
    expect(screen.getByRole('radio', { name: /Polaroid/ })).toBeChecked();

    await user.click(screen.getByTestId('guestbook-publish'));
    await screen.findByTestId('guestbook-sent');
    expect(lastPost(calls)).toMatchObject({
      sourceMediaItemId: 'p2', body: 'Che festa', authorDisplayName: 'Ada', templateKey: 'polaroid',
    });
  });

  it('keeps the whole draft through a network failure and offers to try again', async () => {
    let attempts = 0;
    mock({
      submit: (call) => {
        attempts += 1;
        if (attempts === 1) throw new TypeError('Failed to fetch');
        return jsonResponse(published(JSON.parse(call.body!)));
      },
    });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await user.click(screen.getByTestId('guestbook-template-editorial'));
    await write(user, 'Non perderla', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    expect(await screen.findByTestId('guestbook-failure-retry')).toHaveTextContent(/ancora qui/i);
    expect(screen.getByTestId('guestbook-publish')).toHaveTextContent('Riprova');
    expect(screen.getByTestId('guestbook-body')).toHaveValue('Non perderla');
    expect(screen.getByTestId('guestbook-name')).toHaveValue('Ada');
    expect(screen.getByTestId('guestbook-preview-memory')).toHaveAttribute('data-template', 'editorial');

    await user.click(screen.getByTestId('guestbook-publish'));
    expect(await screen.findByTestId('guestbook-sent')).toHaveTextContent('Non perderla');
  });

  it('treats a server failure as worth retrying, not as the guest’s mistake', async () => {
    mock({ submit: () => errorResponse(500) });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Riprova', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    expect(await screen.findByTestId('guestbook-failure-retry')).toBeInTheDocument();
    expect(screen.getByTestId('guestbook-publish')).toBeEnabled();
  });

  it('stays in the composer and points at the field the server refused', async () => {
    mock({ submit: () => errorResponse(400, { error: 'guestbook_invalid_author' }) });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Auguri', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    const name = screen.getByTestId('guestbook-name');
    await waitFor(() => expect(name).toHaveAttribute('aria-invalid', 'true'));
    expect(name).toHaveAccessibleDescription(/firma non è valida/i);
    expect(screen.getByTestId('guestbook-compose')).toBeInTheDocument();
    expect(screen.getByTestId('guestbook-body')).toHaveValue('Auguri');
  });

  it('says plainly when the book will take no more, and stops offering to publish', async () => {
    mock({ submit: () => errorResponse(409, { error: 'guestbook_limit_reached', maxEntries: 1 }) });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Una di troppo', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    expect(await screen.findByTestId('guestbook-failure-closed'))
      .toHaveTextContent('Hai già lasciato tutti i ricordi che questa festa prevede.');
    expect(screen.getByTestId('guestbook-publish')).toBeDisabled();
  });

  it('asks somebody who wrote too fast to wait, and lets them try again', async () => {
    mock({ submit: () => errorResponse(429, { error: 'too_many_requests' }) });
    renderBook();
    const user = userEvent.setup();

    await openComposerAndChoose(user);
    await write(user, 'Ancora', 'Ada');
    await user.click(screen.getByTestId('guestbook-publish'));

    expect(await screen.findByTestId('guestbook-failure-retry')).toHaveTextContent(/Troppi ricordi/i);
  });

  it('can be left without publishing, and nothing is sent', async () => {
    const { calls } = mock();
    renderBook();
    const user = userEvent.setup();

    await user.click(await screen.findByTestId('party-guestbook-start'));
    await user.click(await screen.findByTestId('guestbook-picker-cancel'));
    expect(await screen.findByTestId('party-guestbook-start')).toBeInTheDocument();
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });
});
