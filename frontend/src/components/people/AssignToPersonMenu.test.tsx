import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AssignToPersonMenu } from './AssignToPersonMenu';
import { I18nProvider } from '../../i18n';
import { installFetchMock, jsonResponse, emptyResponse } from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const people = [
  { personId: 'p-1', name: 'Alice', faceCount: 3, representative: null },
  { personId: 'p-2', name: 'Bob', faceCount: 1, representative: null },
];

it('opens the assign menu from a face and searches existing people', async () => {
  installFetchMock({});
  render(<I18nProvider><AssignToPersonMenu faceId="face-9" people={people} onChanged={() => {}} /></I18nProvider>);
  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  expect(screen.getByRole('dialog', { name: 'Assegna a persona' })).toBeTruthy();

  // Search narrows the list.
  await userEvent.type(screen.getByLabelText('Cerca persona'), 'Ali');
  expect(screen.getByRole('button', { name: 'Alice' })).toBeTruthy();
  expect(screen.queryByRole('button', { name: 'Bob' })).toBeNull();
});

it('assigns to an existing person', async () => {
  const onChanged = vi.fn();
  const mock = installFetchMock({
    'POST /api/people/faces/face-9/assign': () =>
      jsonResponse({ personId: 'p-1', name: 'Alice', faceCount: 4, representative: null }),
  });
  render(<I18nProvider><AssignToPersonMenu faceId="face-9" people={people} onChanged={onChanged} /></I18nProvider>);
  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  await userEvent.click(screen.getByRole('button', { name: 'Alice' }));

  await waitFor(() => {
    const call = mock.calls.find((c) => c.method === 'POST' && c.url.includes('/faces/face-9/assign'));
    expect(call).toBeTruthy();
    expect(JSON.parse(String(call!.body))).toEqual({ personId: 'p-1' });
  });
  expect(onChanged).toHaveBeenCalledWith('p-1');
});

it('creates a new person and assigns the face', async () => {
  const onChanged = vi.fn();
  const mock = installFetchMock({
    'POST /api/people/faces/face-9/assign': () =>
      jsonResponse({ personId: 'p-new', name: 'Carol', faceCount: 1, representative: null }),
  });
  render(<I18nProvider><AssignToPersonMenu faceId="face-9" people={people} onChanged={onChanged} /></I18nProvider>);
  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  await userEvent.type(screen.getByLabelText('Crea nuova persona'), 'Carol');
  await userEvent.click(screen.getByRole('button', { name: 'Crea e assegna' }));

  await waitFor(() => {
    const call = mock.calls.find((c) => c.method === 'POST' && c.url.includes('/faces/face-9/assign'));
    expect(JSON.parse(String(call!.body))).toEqual({ name: 'Carol' });
  });
  expect(onChanged).toHaveBeenCalledWith(null);
});

it('moves and removes an already-assigned face', async () => {
  const onChanged = vi.fn();
  const mock = installFetchMock({
    'POST /api/people/faces/face-9/assign': () =>
      jsonResponse({ personId: 'p-2', name: 'Bob', faceCount: 2, representative: null }),
    'DELETE /api/people/faces/face-9/assignment': () => emptyResponse(),
  });
  render(
    <I18nProvider><AssignToPersonMenu
      faceId="face-9"
      people={people}
      currentPersonId="p-1"
      currentPersonName="Alice"
      onChanged={onChanged}
    /></I18nProvider>,
  );
  // Trigger reflects the assigned state.
  await userEvent.click(screen.getByRole('button', { name: 'Sposta o rimuovi' }));
  expect(screen.getByText('Già assegnato a:')).toBeTruthy();
  expect(screen.getByText('Alice')).toBeTruthy();

  // The current person (Alice) is excluded from the move list; Bob is offered.
  expect(screen.queryByRole('button', { name: 'Alice' })).toBeNull();
  await userEvent.click(screen.getByRole('button', { name: 'Bob' }));
  await waitFor(() => {
    const call = mock.calls.find((c) => c.method === 'POST' && c.url.includes('/faces/face-9/assign'));
    expect(JSON.parse(String(call!.body))).toEqual({ personId: 'p-2' });
  });
  expect(onChanged).toHaveBeenCalledWith('p-2');

  // Reopen → remove from person.
  await userEvent.click(screen.getByRole('button', { name: 'Sposta o rimuovi' }));
  await userEvent.click(screen.getByRole('button', { name: 'Rimuovi dalla persona' }));
  await waitFor(() =>
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/faces/face-9/assignment'))).toBe(true),
  );
});

it('opens as a portal dialog and closes on Escape', async () => {
  installFetchMock({});
  render(<I18nProvider><AssignToPersonMenu faceId="face-9" people={people} onChanged={() => {}} /></I18nProvider>);
  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  const dialog = screen.getByRole('dialog', { name: 'Assegna a persona' });
  // Rendered through a portal onto document.body (not confined to a parent container).
  expect(dialog.closest('.assign-modal-backdrop')).toBeTruthy();
  expect(document.body.contains(dialog)).toBe(true);

  await userEvent.keyboard('{Escape}');
  expect(screen.queryByRole('dialog', { name: 'Assegna a persona' })).toBeNull();
});

it('renders no storage internals', async () => {
  installFetchMock({});
  const { container } = render(<I18nProvider><AssignToPersonMenu faceId="face-9" people={people} onChanged={() => {}} /></I18nProvider>);
  await userEvent.click(screen.getByRole('button', { name: 'Assegna a persona' }));
  const html = container.innerHTML;
  for (const needle of ['blobObjectId', 'storageKey', 'sha256', '/storage/objects/', 'embeddingBytes']) {
    expect(html).not.toContain(needle);
  }
});
