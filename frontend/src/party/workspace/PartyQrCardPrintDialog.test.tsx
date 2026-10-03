import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { PartyQrCardPrintDialog } from './PartyQrCardPrintDialog';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../../test-utils';
import { QR_CARD_LINES } from '../../pages/partyPrintGeometry';

/**
 * The host's QR card: which photograph goes over the code, the card as it will
 * print, and one key per sheet — kept by a retry, so the sheets already
 * accepted are not printed twice.
 */
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const dialogCss = readFileSync(
  resolve(dirname(fileURLToPath(import.meta.url)), 'PartyQrCardPrintDialog.css'), 'utf8');
const rule = (selector: string) =>
  dialogCss.match(new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{([^}]*)\\}`))?.[1] ?? '';

const photo = (fileItemId: string) => ({ fileItemId, width: 3000, height: 4000 });

const accepted = { jobId: 'j1', shortCode: 'abc12345', state: 'ready', queueAhead: 0, mediaRemainingPrints: 120 };

function mount(post: Parameters<typeof installFetchMock>[0][string] = () => jsonResponse(accepted, 202),
  cover: string | null = 'p2') {
  const onClose = vi.fn();
  const mock = installFetchMock({
    'GET /api/albums/a1/party-print/qr-card/photos': () => jsonResponse([photo('p1'), photo('p2'), photo('p3')]),
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
  it('starts from the party’s cover, first in the list, and shows the card with the server’s line', async () => {
    const { mock } = mount();
    const first = await screen.findByRole('radio', { name: 'Foto 1' });
    expect(first).toBeChecked();
    expect(first).toHaveAttribute('value', 'p2');
    // Every photograph the party shows, from the party's own list — not a page of the album.
    expect(mock.calls.some((c) => c.url === '/api/albums/a1/party-print/qr-card/photos')).toBe(true);
    expect(screen.getAllByRole('radio')).toHaveLength(3);
    expect(screen.getByTestId('party-qr-card-line')).toHaveTextContent(QR_CARD_LINES.it);
    expect(screen.getByText('Festa al mare', { selector: 'strong' })).toBeInTheDocument();
  });

  it('lays the photographs out one per square, each as tall as its own picture', async () => {
    mount();
    await screen.findByRole('radio', { name: 'Foto 1' });
    const grid = screen.getByTestId('party-qr-card-photos');
    expect(grid.querySelectorAll('label')).toHaveLength(3);
    // The squares were drawn over one another: the square sat on the label with
    // the picture at height 100%, and the scrolling fieldset squeezed the rows
    // into its max-height. Now the picture carries the square and the grid
    // scrolls in its own box.
    expect(rule('.party-qr-card-photo-grid img')).toMatch(/aspect-ratio:\s*1/);
    expect(rule('.party-qr-card-photo-grid img')).toMatch(/height:\s*auto/);
    expect(rule('.party-qr-card-photo-grid label')).not.toMatch(/aspect-ratio/);
    expect(rule('.party-qr-card-photo-grid')).toMatch(/overflow-y:\s*auto/);
    expect(rule('.party-qr-card-photos')).not.toMatch(/max-height|overflow/);
  });

  it('without a cover in the album, starts from its first photograph', async () => {
    mount(undefined, 'elsewhere');
    const first = await screen.findByRole('radio', { name: 'Foto 1' });
    expect(first).toBeChecked();
    expect(first).toHaveAttribute('value', 'p1');
  });

  it('sends one print per sheet, each with its own key, and names only the photograph and its framing', async () => {
    const user = userEvent.setup();
    const { mock } = mount();
    await screen.findByRole('radio', { name: 'Foto 1' });
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
    await screen.findByRole('radio', { name: 'Foto 1' });
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
    await screen.findByRole('radio', { name: 'Foto 1' });
    await user.click(screen.getByTestId('party-qr-card-send'));
    expect(await screen.findByTestId('party-qr-card-error'))
      .toHaveTextContent('La stampante della festa non taglia le strisce o non ha la carta 10×15.');
  });
});
