import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { DetailsPanel } from '../components/files/DetailsPanel';
import type { Entry } from '../components/files/types';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const FILE: Entry = {
  kind: 'file',
  id: 'file-9',
  file: { id: 'file-9', name: 'report.pdf', mimeType: 'application/pdf', sizeBytes: 20480, createdAt: '2026-10-01T10:00:00Z' },
};

const FOLDER: Entry = { kind: 'folder', id: 'folder-1', folder: { id: 'folder-1', name: 'Posta', createdAt: '2026-10-01T10:00:00Z' } };

function renderPanel(entry: Entry, withRouter = true) {
  const panel = (
    <DetailsPanel entry={entry} currentParentId={null} onClose={() => {}} onOutcome={() => {}} onOpen={() => {}} />
  );
  return render(<AuthedWrapper>{withRouter ? <MemoryRouter>{panel}</MemoryRouter> : panel}</AuthedWrapper>);
}

describe('DetailsPanel: Origine email', () => {
  it('shows the e-mail a file came from, with a link to the message', async () => {
    const mock = installFetchMock({
      'GET /api/mail/file-origins/file-9': () => jsonResponse({
        items: [{
          messageId: 'msg-1', archiveId: 'arc-1', archiveName: 'Ufficio 2019', subject: 'Relazione trimestrale',
          from: { name: 'Ada Example', address: 'ada@example.test' }, date: '2024-03-01T10:00:00Z',
          attachmentName: 'report.pdf',
        }],
      }),
    });
    renderPanel(FILE);

    const section = await screen.findByTestId('mail-file-origin');
    expect(within(section).getByRole('heading', { name: 'Origine email' })).toBeInTheDocument();
    expect(within(section).getByRole('link', { name: 'Relazione trimestrale' })).toHaveAttribute('href', '/mail/messages/msg-1');
    expect(section).toHaveTextContent('Ufficio 2019');
    expect(section).toHaveTextContent('Ada Example <ada@example.test>');
    expect(section).toHaveTextContent('report.pdf');
    expect(mock.calls.map((c) => c.url)).toEqual(['/api/mail/file-origins/file-9']);
    // The rest of the panel is untouched.
    expect(screen.getByRole('button', { name: 'Rename' })).toBeInTheDocument();
  });

  it('works without a router (the file browser harness has none)', async () => {
    installFetchMock({
      'GET /api/mail/file-origins/file-9': () => jsonResponse({
        items: [{ messageId: 'msg-1', archiveId: 'arc-1', archiveName: 'A', subject: null, from: null, date: null, attachmentName: 'report.pdf' }],
      }),
    });
    renderPanel(FILE, false);
    const link = await screen.findByRole('link', { name: '(senza oggetto)' });
    expect(link).toHaveAttribute('href', '/mail/messages/msg-1');
  });

  it('renders nothing when the file has no live origin', async () => {
    const mock = installFetchMock({ 'GET /api/mail/file-origins/file-9': () => jsonResponse({ items: [] }) });
    renderPanel(FILE);
    await waitFor(() => expect(mock.calls).toHaveLength(1));
    expect(screen.queryByTestId('mail-file-origin')).not.toBeInTheDocument();
    expect(screen.queryByText('Origine email')).not.toBeInTheDocument();
  });

  it('renders nothing when the request fails', async () => {
    const mock = installFetchMock({ 'GET /api/mail/file-origins/file-9': () => errorResponse(503, { error: 'unavailable' }) });
    renderPanel(FILE);
    await waitFor(() => expect(mock.calls).toHaveLength(1));
    expect(screen.queryByTestId('mail-file-origin')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('asks nothing without mail.access', async () => {
    const mock = installFetchMock({});
    render(
      <AuthedWrapper permissions={[]}>
        <MemoryRouter>
          <DetailsPanel entry={FILE} currentParentId={null} onClose={() => {}} onOutcome={() => {}} onOpen={() => {}} />
        </MemoryRouter>
      </AuthedWrapper>,
    );
    expect(await screen.findByRole('button', { name: 'Rename' })).toBeInTheDocument();
    expect(mock.calls).toHaveLength(0);
    expect(screen.queryByTestId('mail-file-origin')).not.toBeInTheDocument();
  });

  it('asks nothing for a folder', async () => {
    const mock = installFetchMock({});
    renderPanel(FOLDER);
    expect(await screen.findByRole('button', { name: 'Gallery' })).toBeInTheDocument();
    expect(mock.calls).toHaveLength(0);
  });
});
