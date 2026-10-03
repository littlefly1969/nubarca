import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import type { ImageMediaItem } from '@nubarca/api-client';
import { PartyQrCardPrintDialog } from './PartyQrCardPrintDialog';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../../test-utils';
import { QR_CARD_LINES } from '../../pages/partyPrintGeometry';

/**
 * The host's QR card: which photograph goes over the code, the card as it will
 * print, and one key per sheet — kept by a retry, so the sheets already
 * accepted are not printed twice.
 */
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function photo(id: string): ImageMediaItem {
  return {
    id, kind: 'image', name: `${id}.jpg`, title: null, displayName: `Foto ${id}`, mimeType: 'image/jpeg',
    sizeBytes: 1, width: 3000, height: 4000, createdAt: '2026-10-01T10:00:00Z', updatedAt: null,
    takenAt: null, favorite: false, rating: null, thumbnailUrl: `/api/files/${id}/thumbnail?size=small`,
    occurrenceCount: 1, hasDuplicates: false, hasGps: null,
  };
}

const accepted = { jobId: 'j1', shortCode: 'abc12345', state: 'ready', queueAhead: 0, mediaRemainingPrints: 120 };

function mount(post: Parameters<typeof installFetchMock>[0][string] = () => jsonResponse(accepted, 202),
  cover: string | null = 'p2') {
  const onClose = vi.fn();
  const mock = installFetchMock({
    'GET /api/albums/a1/media': () => jsonResponse({
      items: [photo('p1'), photo('p2'), photo('p3')], limit: 60, count: 3, nextCursor: null, hasMore: false, total: 3,
    }),
    'POST /api/albums/a1/party-print/qr-card': post,
  });
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <PartyQrCardPrintDialog
          albumId="a1" partyName="Festa al mare" partyUrl="/party/tok" coverFileItemId={cover} onClose={onClose}
        />
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return { mock, onClose };
}

const posts = (mock: ReturnType<typeof installFetchMock>) =>
  mock.calls.filter((c) => c.method === 'POST' && c.url === '/api/albums/a1/party-print/qr-card');
const keyOf = (call: { init?: RequestInit }) => new Headers(call.init?.headers).get('Idempotency-Key');

describe('PartyQrCardPrintDialog', () => {
  it('starts from the party’s cover, and shows the card with the server’s line', async () => {
    mount();
    expect(await screen.findByRole('radio', { name: 'Foto p2' })).toBeChecked();
    expect(screen.getByTestId('party-qr-card-line')).toHaveTextContent(QR_CARD_LINES.it);
    expect(screen.getByText('Festa al mare', { selector: 'strong' })).toBeInTheDocument();
  });

  it('without a cover in the album, starts from its first photograph', async () => {
    mount(undefined, 'elsewhere');
    expect(await screen.findByRole('radio', { name: 'Foto p1' })).toBeChecked();
  });

  it('sends one print per sheet, each with its own key, and names only the photograph and its framing', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: 'Foto p2' });
    await user.selectOptions(screen.getByTestId('party-qr-card-sheets'), '3');
    expect(screen.getByText('6 biglietti')).toBeInTheDocument();
    await user.click(screen.getByTestId('party-qr-card-send'));

    expect(await screen.findByTestId('party-qr-card-sent')).toHaveTextContent('3 fogli inviati alla stampante.');
    const sent = posts(mock);
    expect(sent).toHaveLength(3);
    expect(new Set(sent.map(keyOf)).size).toBe(3);
    const body = JSON.parse(sent[0].body!);
    expect(Object.keys(body).sort()).toEqual(['fileItemId', 'locale', 'placement']);
    expect(body).toMatchObject({ fileItemId: 'p2', locale: 'it' });
  });

  it('after a refusal, a retry keeps the keys of the sheets already sent', async () => {
    const user = userEvent.setup();
    let calls = 0;
    const { mock } = mount(() => {
      calls += 1;
      return calls === 2 ? errorResponse(409, { error: 'printer_offline' }) : jsonResponse(accepted, 202);
    });
    await screen.findByRole('radio', { name: 'Foto p2' });
    await user.selectOptions(screen.getByTestId('party-qr-card-sheets'), '2');
    await user.click(screen.getByTestId('party-qr-card-send'));

    const error = await screen.findByTestId('party-qr-card-error');
    expect(error).toHaveTextContent('Fogli inviati: 1 su 2.');
    await user.click(screen.getByTestId('party-qr-card-send'));
    await waitFor(() => expect(screen.getByTestId('party-qr-card-sent')).toBeInTheDocument());

    const keys = posts(mock).map(keyOf);
    // First try: sheet 1, sheet 2 (refused). Retry: sheet 1 again (its record answers), sheet 2.
    expect(keys).toHaveLength(4);
    expect(keys[2]).toBe(keys[0]);
    expect(keys[3]).toBe(keys[1]);
  });

  it('says why the party’s own conditions refuse a card', async () => {
    const user = userEvent.setup();
    mount(() => errorResponse(409, { error: 'format_unsupported' }));
    await screen.findByRole('radio', { name: 'Foto p2' });
    await user.click(screen.getByTestId('party-qr-card-send'));
    expect(await screen.findByTestId('party-qr-card-error'))
      .toHaveTextContent('La stampante della festa non taglia le strisce o non ha la carta 10×15.');
  });
});
