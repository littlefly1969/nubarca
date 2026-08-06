import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { UploadPanel } from './UploadPanel';
import { AuthedWrapper, installFetchMock, jsonResponse, errorResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function fileFromString(name: string, body = 'hello'): File {
  return new File([body], name, { type: 'text/plain' });
}

describe('UploadPanel', () => {
  it('POSTs a selected file as multipart to /api/files at root', async () => {
    const onComplete = vi.fn();
    const mock = installFetchMock({
      'POST /api/files': () =>
        jsonResponse(
          {
            id: 'file-1',
            name: 'note.txt',
            mimeType: 'text/plain',
            sizeBytes: 5,
            createdAt: '2026-05-24T10:00:00Z',
          },
          201,
        ),
    });

    render(
      <AuthedWrapper>
        <UploadPanel parentFolderId={null} onUploadsComplete={onComplete} />
      </AuthedWrapper>,
    );

    const input = screen.getByLabelText('Seleziona i file da caricare') as HTMLInputElement;
    const user = userEvent.setup();
    await user.upload(input, fileFromString('note.txt'));

    await waitFor(() => {
      expect(onComplete).toHaveBeenCalled();
    });

    const post = mock.calls.find((c) => c.url === '/api/files');
    expect(post).toBeDefined();
    expect(post!.method).toBe('POST');
    // FormData was used (multipart) — the api client never sets content-type.
    expect(post!.body).toBe('[FormData]');

    // The "uploaded" pill should be visible after the batch finishes.
    expect(await screen.findByText('caricato')).toBeInTheDocument();
  });

  it('surfaces the backend quota message on 413', async () => {
    installFetchMock({
      'POST /api/files': () =>
        errorResponse(413, { error: 'Upload would exceed your storage quota.' }),
    });

    render(
      <AuthedWrapper>
        <UploadPanel parentFolderId={null} onUploadsComplete={() => {}} />
      </AuthedWrapper>,
    );

    const input = screen.getByLabelText('Seleziona i file da caricare') as HTMLInputElement;
    const user = userEvent.setup();
    await user.upload(input, fileFromString('big.txt'));

    // The failed pill + the backend's friendly message both render.
    expect(await screen.findByText('non riuscito')).toBeInTheDocument();
    expect(
      await screen.findByText('Upload would exceed your storage quota.'),
    ).toBeInTheDocument();
  });

  it('renders the duplicate pill on 409', async () => {
    installFetchMock({
      'POST /api/files': () =>
        errorResponse(409, { error: 'A file with this name already exists.' }),
    });

    render(
      <AuthedWrapper>
        <UploadPanel parentFolderId={null} onUploadsComplete={() => {}} />
      </AuthedWrapper>,
    );

    const input = screen.getByLabelText('Seleziona i file da caricare') as HTMLInputElement;
    const user = userEvent.setup();
    await user.upload(input, fileFromString('note.txt'));

    expect(await screen.findByText('nome duplicato')).toBeInTheDocument();
  });

  // Slice 76: a folder upload where ONE file's logical path already exists
  // must 409 that file but keep uploading the rest of the batch.
  it('continues the batch when one file conflicts (409) and uploads the others', async () => {
    let call = 0;
    const mock = installFetchMock({
      // First file conflicts; the second succeeds. The component issues one
      // POST per file sequentially, so a per-request counter models the
      // "one occurrence already exists" case.
      'POST /api/files': () => {
        call += 1;
        return call === 1
          ? errorResponse(409, { error: 'A file with this name already exists.' })
          : jsonResponse(
              {
                id: 'file-2',
                name: 'altra.jpg',
                mimeType: 'image/jpeg',
                sizeBytes: 5,
                createdAt: '2026-05-31T10:00:00Z',
              },
              201,
            );
      },
    });

    const onComplete = vi.fn();
    render(
      <AuthedWrapper>
        <UploadPanel parentFolderId={null} onUploadsComplete={onComplete} />
      </AuthedWrapper>,
    );

    // Two files from the same uploaded directory ("Vacanze/").
    const conflicting = fileFromString('foto.jpg');
    const fresh = fileFromString('altra.jpg');
    const input = screen.getByLabelText('Seleziona i file da caricare') as HTMLInputElement;
    const user = userEvent.setup();
    await user.upload(input, [conflicting, fresh]);

    await waitFor(() => expect(onComplete).toHaveBeenCalled());

    // The conflicting file shows the duplicate pill, the other still uploaded —
    // proving the batch did not abort on the 409.
    expect(await screen.findByText('nome duplicato')).toBeInTheDocument();
    expect(await screen.findByText('caricato')).toBeInTheDocument();
    // Both files were actually POSTed (the second was not skipped).
    expect(mock.calls.filter((c) => c.url === '/api/files')).toHaveLength(2);
  });
});
