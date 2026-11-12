import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { emptyResponse, errorResponse, installFetchMock, jsonResponse, type MockRequest } from '../test-utils';
import { archive, currentLocation, detail, FOLDERS, renderMail, summary } from './mail.test.fixtures';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function messagesQuery(req: MockRequest): URLSearchParams {
  return new URLSearchParams(req.url.split('?')[1] ?? '');
}

function baseHandlers(extra: Record<string, (req: MockRequest) => Response> = {}) {
  return {
    'GET /api/mail/archives': () => jsonResponse({ items: [archive(), archive({ id: 'arc-2', displayName: 'Casa' })] }),
    'GET /api/mail/archives/arc-1/folders': () => jsonResponse({ items: FOLDERS }),
    'GET /api/mail/archives/arc-2/folders': () => jsonResponse({ items: [] }),
    ...extra,
  };
}

describe('mail view', () => {
  it('opens the first archive, builds the nested folder tree and scopes the list to a folder', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1')], nextCursor: null }),
    }));
    renderMail('/mail');

    const tree = await screen.findByRole('navigation', { name: 'Cartelle' });
    // Root open by default: its children are visible; the nested one is not yet.
    expect(within(tree).getByRole('button', { name: /^Posta in arrivo/ })).toBeInTheDocument();
    expect(within(tree).getByRole('button', { name: /^Posta inviata/ })).toBeInTheDocument();
    expect(within(tree).queryByRole('button', { name: /^Clienti/ })).not.toBeInTheDocument();

    const expand = within(tree).getByRole('button', { name: 'Espandi Posta in arrivo' });
    expect(expand).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(expand);
    expect(within(tree).getByRole('button', { name: 'Comprimi Posta in arrivo' })).toHaveAttribute('aria-expanded', 'true');
    await userEvent.click(within(tree).getByRole('button', { name: /^Clienti/ }));

    await waitFor(() => expect(currentLocation()).toBe('/mail?archive=arc-1&folder=f-clients'));
    await waitFor(() => {
      const last = mock.calls.filter((c) => c.url.startsWith('/api/mail/messages?')).at(-1)!;
      const qs = messagesQuery(last);
      expect(qs.get('archiveId')).toBe('arc-1');
      expect(qs.get('folderId')).toBe('f-clients');
      expect(qs.get('limit')).toBe('50');
    });
    expect(within(tree).getByRole('button', { name: /^Clienti/ })).toHaveAttribute('aria-current', 'true');
  });

  it('says when the text search cannot reach every message yet, only while searching', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': (req) => jsonResponse({
        items: [summary('m1')],
        nextCursor: null,
        bodySearchIncomplete: messagesQuery(req).has('q'),
      }),
    }));
    renderMail('/mail?archive=arc-1&q=fattura');
    expect(await screen.findByTestId('mail-body-search-incomplete')).toHaveTextContent('ancora in preparazione');
    cleanup();

    renderMail('/mail?archive=arc-1');
    expect(await screen.findAllByTestId('mail-message-row')).toHaveLength(1);
    expect(screen.queryByTestId('mail-body-search-incomplete')).not.toBeInTheDocument();
  });

  it('pages with nextCursor and never asks for more than one page at a time', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': (req) => {
        const cursor = messagesQuery(req).get('cursor');
        return cursor === 'c2'
          ? jsonResponse({ items: [summary('m3')], nextCursor: null })
          : jsonResponse({ items: [summary('m1'), summary('m2')], nextCursor: 'c2' });
      },
    }));
    renderMail('/mail?archive=arc-1');

    expect(await screen.findAllByTestId('mail-message-row')).toHaveLength(2);
    await userEvent.click(screen.getByTestId('mail-load-more'));
    await waitFor(() => expect(screen.getAllByTestId('mail-message-row')).toHaveLength(3));
    expect(screen.getAllByTestId('mail-message-row').map((r) => r.getAttribute('data-message-id'))).toEqual(['m1', 'm2', 'm3']);
    expect(screen.queryByTestId('mail-load-more')).not.toBeInTheDocument();

    const pages = mock.calls.filter((c) => c.url.startsWith('/api/mail/messages?'));
    expect(pages).toHaveLength(2);
    expect(messagesQuery(pages[0]).has('cursor')).toBe(false);
    expect(messagesQuery(pages[1]).get('cursor')).toBe('c2');
  });

  it('reads a message: headers, attachments in all three states, html in a script-less sandbox with CSP', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1')], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
    }));
    renderMail('/mail?archive=arc-1');

    await userEvent.click(await screen.findByRole('link', { name: /Oggetto m1/ }));
    expect(currentLocation()).toBe('/mail/messages/m1?archive=arc-1');

    const reader = await screen.findByTestId('mail-reader');
    const heading = within(reader).getByRole('heading', { level: 2, name: 'Relazione trimestrale' });
    await waitFor(() => expect(heading).toHaveFocus());
    expect(reader).toHaveTextContent('Ada Example <ada@example.test>');
    expect(reader).toHaveTextContent('Bob <bob@example.test>');
    expect(reader).toHaveTextContent('carla@example.test');
    expect(reader).toHaveTextContent('Posta in arrivo');

    const rows = within(reader).getAllByTestId('mail-attachment');
    const [available, deleted, skipped] = rows;
    expect(available).toHaveAttribute('data-state', 'available');
    expect(within(available).getByRole('link', { name: 'Apri' })).toHaveAttribute('href', '/api/files/file-9/content');
    expect(within(available).getByRole('link', { name: 'Mostra nei file' })).toHaveAttribute('href', '/');
    expect(deleted).toHaveTextContent('Il file è stato eliminato da NubArca');
    expect(within(deleted).queryByRole('link')).not.toBeInTheDocument();
    expect(skipped).toHaveTextContent('Non estratto: allegato troppo grande');
    expect(within(skipped).queryByRole('link')).not.toBeInTheDocument();

    const frame = within(reader).getByTestId('mail-body-frame');
    expect(frame.tagName).toBe('IFRAME');
    expect(frame.getAttribute('sandbox')).toBe('allow-same-origin');
    expect(frame.getAttribute('sandbox')).not.toContain('allow-scripts');
    expect(frame.getAttribute('referrerpolicy')).toBe('no-referrer');
    const srcdoc = frame.getAttribute('srcdoc') ?? '';
    expect(srcdoc).toContain('<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; img-src \'self\' data:; style-src \'unsafe-inline\'; font-src \'self\' data:">');
    expect(srcdoc).toContain('<p>Ciao <b>Bob</b></p>');

    const embedded = within(reader).getByTestId('mail-embedded');
    expect(embedded.tagName).toBe('DETAILS');
    expect(embedded).toHaveTextContent('Fwd: ciao');
    expect(embedded).toHaveTextContent('Testo inoltrato');

    // Closing returns to the list, and focus to the row that opened it.
    await userEvent.click(within(reader).getByRole('button', { name: 'Torna all’elenco' }));
    expect(currentLocation()).toBe('/mail?archive=arc-1');
    await waitFor(() => expect(screen.getByRole('link', { name: /Oggetto m1/ })).toHaveFocus());
  });

  it('renders a text body as wrapped text and shows the truncation notice', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1', {
        body: { kind: 'text', html: null, text: 'Riga uno\nRiga due', truncated: true },
        attachments: [], embeddedMessages: [],
      })),
    }));
    renderMail('/mail/messages/m1?archive=arc-1');

    const reader = await screen.findByTestId('mail-reader');
    expect(within(reader).queryByTestId('mail-body-frame')).not.toBeInTheDocument();
    const pre = reader.querySelector('pre.mail-body-text');
    expect(pre?.textContent).toBe('Riga uno\nRiga due');
    expect(reader).toHaveTextContent('troncato durante l’importazione');
  });

  it('deletes a message after confirmation and removes it from the list', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1'), summary('m2')], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
      'DELETE /api/mail/messages/m1': () => emptyResponse(204),
    }));
    renderMail('/mail/messages/m1?archive=arc-1');

    const reader = await screen.findByTestId('mail-reader');
    await userEvent.click(within(reader).getByRole('button', { name: 'Elimina messaggio' }));
    const dialog = await screen.findByTestId('mail-delete-dialog');
    expect(dialog).toHaveTextContent('allegati restano nei tuoi file');
    expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(false);

    await userEvent.click(within(dialog).getByTestId('mail-delete-confirm'));
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'DELETE' && c.url === '/api/mail/messages/m1')).toBe(true));
    await waitFor(() => expect(screen.queryByTestId('mail-reader')).not.toBeInTheDocument());
    expect(currentLocation()).toBe('/mail?archive=arc-1');
    expect(screen.getAllByTestId('mail-message-row').map((r) => r.getAttribute('data-message-id'))).toEqual(['m2']);
    expect(screen.getByText(/Messaggio eliminato/)).toBeInTheDocument();
  });

  it('cancelling the confirmation deletes nothing', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1')], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
    }));
    renderMail('/mail/messages/m1?archive=arc-1');
    await userEvent.click(await screen.findByRole('button', { name: 'Elimina messaggio' }));
    await userEvent.click(within(await screen.findByTestId('mail-delete-dialog')).getByRole('button', { name: 'Annulla' }));
    expect(screen.queryByTestId('mail-delete-dialog')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('deep link: opens the reader directly and adopts the message’s archive and folder', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1', { archiveId: 'arc-2' })], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1', { archiveId: 'arc-2', folderId: 'f-x' })),
    }));
    renderMail('/mail/messages/m1');
    expect(await screen.findByTestId('mail-reader')).toBeInTheDocument();
    await waitFor(() => expect(currentLocation()).toBe('/mail/messages/m1?archive=arc-2&folder=f-x'));
  });

  it('deep link to a deleted message says it is no longer available', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/mail/messages/gone': () => errorResponse(404, { error: 'not_found' }),
    }));
    renderMail('/mail/messages/gone');
    expect(await screen.findByTestId('mail-reader-unavailable')).toHaveTextContent('Messaggio non più disponibile');
  });

  it('hands a 401 to the auth layer', async () => {
    const invalidateAuth = vi.fn();
    installFetchMock({ 'GET /api/mail/archives': () => errorResponse(401) });
    renderMail('/mail', { invalidateAuth });
    await waitFor(() => expect(invalidateAuth).toHaveBeenCalled());
  });
});

describe('structured search', () => {
  it('builds the query string, reflects it in the URL and searches the server', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [], nextCursor: null }),
    }));
    renderMail('/mail?archive=arc-1&folder=f-inbox');
    await screen.findByTestId('mail-list-empty');

    const form = screen.getByRole('search', { name: 'Ricerca nella posta' });
    await userEvent.type(within(form).getByLabelText('Cerca nella posta'), 'fattura');
    await userEvent.click(within(form).getByRole('button', { name: 'Filtri' }));
    await userEvent.type(within(form).getByLabelText('Da (mittente)'), 'ada@example.test');
    await userEvent.type(within(form).getByLabelText('A (destinatari)'), 'bob');
    await userEvent.type(within(form).getByLabelText('Oggetto'), 'relazione');
    await userEvent.type(within(form).getByLabelText('Dal'), '2024-03-01');
    await userEvent.type(within(form).getByLabelText('Al'), '2024-03-31');
    await userEvent.click(within(form).getByLabelText('Solo con allegati'));
    await userEvent.click(within(form).getByRole('button', { name: 'Cerca' }));

    await waitFor(() => expect(currentLocation()).toBe(
      '/mail?archive=arc-1&folder=f-inbox&q=fattura&from=ada%40example.test&to=bob&subject=relazione&since=2024-03-01&until=2024-03-31&attachments=1',
    ));
    await waitFor(() => {
      const qs = messagesQuery(mock.calls.filter((c) => c.url.startsWith('/api/mail/messages?')).at(-1)!);
      expect(Object.fromEntries(qs)).toEqual({
        archiveId: 'arc-1',
        folderId: 'f-inbox',
        q: 'fattura',
        from: 'ada@example.test',
        to: 'bob',
        subject: 'relazione',
        after: new Date(2024, 2, 1).toISOString(),
        before: new Date(2024, 3, 1).toISOString(),
        hasAttachments: 'true',
        limit: '50',
      });
    });
    expect(await screen.findByText('Nessun messaggio corrisponde alla ricerca.')).toBeInTheDocument();

    // Clearing keeps the scope and drops the criteria.
    await userEvent.click(within(form).getByRole('button', { name: 'Azzera la ricerca' }));
    await waitFor(() => expect(currentLocation()).toBe('/mail?archive=arc-1&folder=f-inbox'));
  });

  it('starts from the URL: a linked search is restored in the form', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [], nextCursor: null }),
    }));
    renderMail('/mail?archive=arc-1&q=fattura&from=ada');
    const form = await screen.findByRole('search');
    expect(within(form).getByLabelText('Cerca nella posta')).toHaveValue('fattura');
    expect(within(form).getByLabelText('Da (mittente)')).toHaveValue('ada');
  });
});

describe('documents view', () => {
  it('lists messages as documents with archive, folder and attachment chips', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({
        items: [summary('m1', { folderId: 'f-clients', hasAttachments: true, attachmentCount: 3 }), summary('m2')],
        nextCursor: null,
      }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
    }));
    renderMail('/mail?view=documents');

    const cards = await screen.findAllByTestId('mail-doc-card');
    expect(cards).toHaveLength(2);
    const [first] = cards;
    expect(within(first).getByRole('heading', { level: 3, name: 'Oggetto m1' })).toBeInTheDocument();
    expect(within(first).getByRole('link', { name: 'Oggetto m1' })).toHaveAttribute('href', '/mail/messages/m1?view=documents');
    expect(first).toHaveTextContent('Ada Example');
    await waitFor(() => expect(first).toHaveTextContent('Ufficio 2019 · Top of Outlook data file / Posta in arrivo / Clienti'));

    // Documents across every archive: no archive scope is sent.
    const firstQuery = messagesQuery(mock.calls.find((c) => c.url.startsWith('/api/mail/messages?'))!);
    expect(firstQuery.has('archiveId')).toBe(false);

    await userEvent.click(within(first).getByTestId('mail-doc-attachments'));
    const chips = await within(first).findAllByTestId('mail-doc-chip');
    expect(chips).toHaveLength(3);
    expect(chips[0]).toHaveAttribute('href', '/api/files/file-9/content');
    expect(chips[1].tagName).toBe('SPAN');
    expect(within(cards[1]).queryByTestId('mail-doc-attachments')).not.toBeInTheDocument();
  });

  it('opens a document in the reader and returns to the documents', async () => {
    installFetchMock(baseHandlers({
      'GET /api/mail/messages': () => jsonResponse({ items: [summary('m1')], nextCursor: null }),
      'GET /api/mail/messages/m1': () => jsonResponse(detail('m1')),
    }));
    renderMail('/mail?view=documents');
    await userEvent.click(await screen.findByRole('link', { name: 'Oggetto m1' }));
    expect(await screen.findByTestId('mail-reader')).toBeInTheDocument();
    expect(screen.getByTestId('mail-documents')).not.toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Torna all’elenco' }));
    expect(currentLocation()).toBe('/mail?view=documents');
    expect(screen.getByTestId('mail-documents')).toBeVisible();
  });
});
