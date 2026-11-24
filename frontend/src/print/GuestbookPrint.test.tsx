import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { type PartyPrintManifest } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { GuestbookPrint } from './GuestbookPrint';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('prints the published memory with no personal text editor and keeps its key on retry', async () => {
  let attempts = 0;
  const mock = installFetchMock({ 'POST /api/party/p/print': () => ++attempts === 1
    ? jsonResponse({ error: 'render_failed' }, 503)
    : jsonResponse({ jobId: 'job', publicSequence: 1 }, 202) });
  const accepted = vi.fn();
  const manifest = { formats: [{ type: 'photo', enabled: true, remaining: 4, remainingForYou: 2 }] } as PartyPrintManifest;
  render(<I18nProvider><GuestbookPrint token="p" entryId="memory" manifest={manifest} onAccepted={accepted} /></I18nProvider>);
  expect(screen.queryByRole('textbox')).toBeNull();
  const img = screen.getByRole('img');
  Object.defineProperties(img, { naturalWidth: { value: 1000 }, naturalHeight: { value: 1600 } });
  fireEvent.load(img);
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'Stampa ricordo' }));
  await screen.findByRole('alert');
  await user.click(screen.getByRole('button', { name: 'Stampa ricordo' }));
  await waitFor(() => expect(accepted).toHaveBeenCalledOnce());
  const posts = mock.calls.filter((c) => c.method === 'POST');
  expect(JSON.parse(posts[0].body!)).toMatchObject({ guestbookEntryId: 'memory', product: 'photo', theme: 'pure', orientation: 'portrait' });
  expect(posts[0].body).toEqual(posts[1].body);
});
