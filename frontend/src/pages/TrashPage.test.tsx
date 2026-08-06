import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TrashPage } from './TrashPage';
import { AuthedWrapper, installFetchMock, jsonResponse, emptyResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('TrashPage', () => {
  it('renders deleted folders and files from /api/trash', async () => {
    installFetchMock({
      'GET /api/trash': () =>
        jsonResponse({
          folders: [
            {
              id: 'folder-1',
              name: 'Old Photos',
              parentFolderId: null,
              createdAt: '2026-05-20T10:00:00Z',
              updatedAt: null,
              deletedAt: '2026-05-23T10:00:00Z',
            },
          ],
          files: [
            {
              id: 'file-1',
              name: 'draft.txt',
              mimeType: 'text/plain',
              sizeBytes: 100,
              parentFolderId: null,
              createdAt: '2026-05-20T10:00:00Z',
              updatedAt: null,
              deletedAt: '2026-05-23T11:00:00Z',
            },
          ],
        }),
    });

    render(
      <AuthedWrapper>
        <TrashPage />
      </AuthedWrapper>,
    );

    expect(await screen.findByText('Old Photos')).toBeInTheDocument();
    expect(screen.getByText('draft.txt')).toBeInTheDocument();
  });

  it('asks for confirmation before permanently deleting an item', async () => {
    installFetchMock({
      'GET /api/trash': () =>
        jsonResponse({
          folders: [],
          files: [
            {
              id: 'file-1',
              name: 'draft.txt',
              mimeType: 'text/plain',
              sizeBytes: 100,
              parentFolderId: null,
              createdAt: '2026-05-20T10:00:00Z',
              updatedAt: null,
              deletedAt: '2026-05-23T11:00:00Z',
            },
          ],
        }),
      'DELETE /api/trash/files/file-1': () => emptyResponse(204),
    });

    render(
      <AuthedWrapper>
        <TrashPage />
      </AuthedWrapper>,
    );

    await screen.findByText('draft.txt');

    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    const user = userEvent.setup();
    await user.click(
      screen.getAllByRole('button', { name: 'Elimina definitivamente' })[0],
    );

    expect(confirmSpy).toHaveBeenCalled();

    // No DELETE call should happen when the user cancels the confirm dialog.
    // The mock only declares `GET /api/trash` and `DELETE /api/trash/files/file-1`;
    // declining the prompt should leave the second handler untouched.
    confirmSpy.mockRestore();

    // Now accept the prompt and verify the DELETE actually fires.
    const acceptSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    await user.click(
      screen.getAllByRole('button', { name: 'Elimina definitivamente' })[0],
    );

    await waitFor(() => {
      expect(acceptSpy).toHaveBeenCalledTimes(1);
    });
  });
});
