import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumSettingsPanel } from './AlbumSettingsPanel';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const album = {
  id: 'a1', name: 'Trip', description: null, showOnTv: false,
  createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z',
};
const party = {
  albumId: 'a1', showOnTv: false, partyMode: false, partyUrl: null,
  uploadEnabled: false, uploadUrl: null, requireUploadApproval: false,
};

function renderPanel(overrides: Partial<Parameters<typeof AlbumSettingsPanel>[0]> = {}) {
  const props = {
    albumId: 'a1', album, party,
    onAlbumUpdated: vi.fn(), onPartyUpdated: vi.fn(), onDeleted: vi.fn(), onClose: vi.fn(),
    ...overrides,
  };
  render(
    <AuthedWrapper>
      <MemoryRouter><AlbumSettingsPanel {...props} /></MemoryRouter>
    </AuthedWrapper>,
  );
  return props;
}

describe('AlbumSettingsPanel', () => {
  it('is a modal dialog with rename, TV and delete controls', () => {
    renderPanel();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByTestId('album-name')).toHaveValue('Trip');
    expect(screen.getByTestId('album-tv-toggle')).toBeInTheDocument();
    expect(screen.getByTestId('album-delete')).toBeInTheDocument();
  });

  it('Save is disabled until the name/description is dirty, then persists', async () => {
    const onAlbumUpdated = vi.fn();
    installFetchMock({ 'PATCH /api/albums/a1': () => jsonResponse({ ...album, name: 'Trip 2024' }) });
    renderPanel({ onAlbumUpdated });
    const save = screen.getByTestId('album-save');
    expect(save).toBeDisabled();
    await userEvent.type(screen.getByTestId('album-name'), ' 2024');
    expect(save).toBeEnabled();
    await userEvent.click(save);
    expect(onAlbumUpdated).toHaveBeenCalledWith(expect.objectContaining({ name: 'Trip 2024' }));
  });

  it('Escape closes the panel', async () => {
    const onClose = vi.fn();
    renderPanel({ onClose });
    screen.getByTestId('album-name').focus();
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });
});
