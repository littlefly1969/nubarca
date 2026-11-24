import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { AlbumPhotoTextEditor } from './AlbumPhotoTextEditor';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it('edits the original preview and saves geometry with the version it read', async () => {
  const endpoint = '/api/party/p/photos/f/text';
  const mock = installFetchMock({
    [`GET ${endpoint}`]: () => jsonResponse({ text: 'Prima', layout: null, version: 3 }),
    [`PUT ${endpoint}`]: () => jsonResponse({ version: 4 }),
  });
  const saved = vi.fn();
  render(<I18nProvider><AlbumPhotoTextEditor endpoint={endpoint} previewUrl="/photo?v=3" onClose={vi.fn()} onSaved={saved} /></I18nProvider>);
  const field = await screen.findByRole('textbox');
  expect(screen.getByRole('presentation')).toHaveAttribute('src', '/photo?v=3&original=true');
  expect(screen.queryByRole('option', { name: 'Sotto la foto' })).toBeNull();
  fireEvent.change(field, { target: { value: 'Auguri' } });
  fireEvent.change(screen.getByTestId('photo-text-x'), { target: { value: '75' } });
  await userEvent.setup().click(screen.getByRole('button', { name: 'Salva nell’album' }));
  await waitFor(() => expect(saved).toHaveBeenCalledOnce());
  const body = JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!);
  expect(body).toMatchObject({ text: 'Auguri', expectedVersion: 3, layout: { mode: 'bubble', x: .75 } });
});

it('preserves the draft after a conflict and does not silently overwrite the newer edit', async () => {
  const endpoint = '/api/party/p/photos/f/text';
  const mock = installFetchMock({
    [`GET ${endpoint}`]: () => jsonResponse({ text: 'Prima', layout: null, version: 1 }),
    [`PUT ${endpoint}`]: () => jsonResponse({ error: 'photo_changed' }, 409),
  });
  const saved = vi.fn();
  render(<I18nProvider><AlbumPhotoTextEditor endpoint={endpoint} previewUrl="/photo" onClose={vi.fn()} onSaved={saved} /></I18nProvider>);
  fireEvent.change(await screen.findByRole('textbox'), { target: { value: 'Il mio testo' } });
  await userEvent.setup().click(screen.getByRole('button', { name: 'Salva nell’album' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('La foto è stata modificata');
  expect(screen.getByRole('textbox')).toHaveValue('Il mio testo');
  expect(saved).not.toHaveBeenCalled();
  expect(mock.calls.filter((c) => c.method === 'PUT')).toHaveLength(1);
});
