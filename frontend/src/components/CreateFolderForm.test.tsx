import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CreateFolderForm } from './CreateFolderForm';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('CreateFolderForm', () => {
  it('rejects an empty / whitespace name client-side without calling the API', async () => {
    const onCreated = vi.fn();
    const mock = installFetchMock({});

    render(
      <AuthedWrapper>
        <CreateFolderForm parentFolderId={null} onCreated={onCreated} />
      </AuthedWrapper>,
    );

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('New folder'), '   ');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(
      await screen.findByText('Please enter a folder name.'),
    ).toBeInTheDocument();
    expect(mock.calls).toHaveLength(0);
    expect(onCreated).not.toHaveBeenCalled();
  });

  it('POSTs a trimmed name to /api/folders and calls onCreated on success', async () => {
    const onCreated = vi.fn();
    const mock = installFetchMock({
      'POST /api/folders': () =>
        jsonResponse({ id: 'folder-1', name: 'Photos', createdAt: '2026-05-24T10:00:00Z' }, 201),
    });

    render(
      <AuthedWrapper>
        <CreateFolderForm parentFolderId={null} onCreated={onCreated} />
      </AuthedWrapper>,
    );

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('New folder'), '  Photos  ');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(onCreated).toHaveBeenCalled();
    });

    const post = mock.calls.find((c) => c.url === '/api/folders');
    expect(post).toBeDefined();
    expect(post!.method).toBe('POST');
    expect(JSON.parse(post!.body ?? '{}')).toEqual({ name: 'Photos' });
  });

  it('POSTs to /api/folders/{id}/folders when parentFolderId is set', async () => {
    const mock = installFetchMock({
      'POST /api/folders/parent-1/folders': () =>
        jsonResponse({ id: 'folder-2', name: 'Sub', createdAt: '2026-05-24T10:00:00Z' }, 201),
    });

    render(
      <AuthedWrapper>
        <CreateFolderForm parentFolderId="parent-1" onCreated={() => {}} />
      </AuthedWrapper>,
    );

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('New folder'), 'Sub');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.url === '/api/folders/parent-1/folders')).toBe(true);
    });
  });
});
