import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { BeautyLabUploadPage } from './BeautyLabUploadPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  sessionStorage.clear();
});

function wrapper(token = 'btok-1') {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/beauty-lab-upload/${token}`]}>
        <Routes>
          <Route path="/beauty-lab-upload/:token" element={<BeautyLabUploadPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

function jpeg(name = 'photo.jpg') {
  return new File([new Uint8Array([0xff, 0xd8, 0xff])], name, { type: 'image/jpeg' });
}

function activeState(over: Record<string, unknown> = {}) {
  return {
    status: 'active',
    expiresAt: new Date(Date.now() + 600_000).toISOString(),
    maxFiles: 40,
    maxTotalBytes: 500_000_000,
    accepted: 0,
    rejected: 0,
    ...over,
  };
}

describe('BeautyLabUploadPage (public QR mobile upload)', () => {
  it('renders camera + gallery controls and reports per-file results', async () => {
    let uploadBody: string | null = null;
    installFetchMock({
      'GET /api/beauty-lab-upload/btok-1': () => jsonResponse(activeState()),
      'POST /api/beauty-lab-upload/btok-1/files': (req) => {
        uploadBody = req.body;
        return jsonResponse({
          accepted: 1,
          rejected: 1,
          status: 'active',
          files: [
            { name: 'a.jpg', ok: true, reason: null },
            { name: 'b.jpg', ok: false, reason: 'not_an_image' },
          ],
        });
      },
    });

    render(wrapper());

    // Both native controls exist (camera capture + gallery multiple).
    const camera = await screen.findByLabelText(/Scatta una foto/i);
    const gallery = screen.getByLabelText(/Scegli dalla galleria/i);
    expect((camera as HTMLInputElement).accept).toBe('image/*');
    expect((camera as HTMLInputElement).getAttribute('capture')).toBe('environment');
    expect((gallery as HTMLInputElement).multiple).toBe(true);

    await userEvent.setup().upload(gallery as HTMLInputElement, [jpeg('a.jpg'), jpeg('b.jpg')]);

    const result = await screen.findByTestId('upload-result');
    expect(result).toHaveTextContent(/Caricata 1 foto/i);
    expect(result).toHaveTextContent(/1 rifiutata/i);
    expect(uploadBody).toBe('[FormData]');

    // No owner identity / lab-browsing / login surface.
    expect(screen.queryByText(/sign in|log in|password|owner/i)).not.toBeInTheDocument();
    // No persistent token storage.
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('shows an expired message and no upload controls when the session is expired', async () => {
    installFetchMock({
      'GET /api/beauty-lab-upload/btok-1': () => jsonResponse(activeState({ status: 'expired' })),
    });
    render(wrapper());
    expect(await screen.findByText(/scaduta/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/Scatta una foto/i)).not.toBeInTheDocument();
  });

  it('shows a revoked message when the session was closed on the TV', async () => {
    installFetchMock({
      'GET /api/beauty-lab-upload/btok-1': () => jsonResponse(activeState({ status: 'revoked' })),
    });
    render(wrapper());
    expect(await screen.findByText(/chiusa/i)).toBeInTheDocument();
  });

  it('shows unavailable when the token is unknown (404)', async () => {
    installFetchMock({
      'GET /api/beauty-lab-upload/btok-1': () => errorResponse(404),
    });
    render(wrapper());
    expect(await screen.findByText(/non è più valido/i)).toBeInTheDocument();
  });

  it('flips to unavailable when the token is revoked mid-upload (404 on POST)', async () => {
    installFetchMock({
      'GET /api/beauty-lab-upload/btok-1': () => jsonResponse(activeState()),
      'POST /api/beauty-lab-upload/btok-1/files': () => errorResponse(404),
    });
    render(wrapper());
    const gallery = await screen.findByLabelText(/Scegli dalla galleria/i);
    await userEvent.setup().upload(gallery as HTMLInputElement, [jpeg()]);
    expect(await screen.findByText(/non è più valido/i)).toBeInTheDocument();
  });
});
