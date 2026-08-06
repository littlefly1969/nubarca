import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { GallerySettingsPanel } from './GallerySettingsPanel';
import { installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';
import type { MediaLibraryEffective } from '@nubarca/api-client';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const FOLDER_ID = '11111111-1111-1111-1111-111111111111';

function effective(overrides: Partial<MediaLibraryEffective> = {}): MediaLibraryEffective {
  return {
    folderId: FOLDER_ID,
    photos: { excluded: false, source: 'default', sourceFolderId: null, sourceFolderName: null },
    videos: { excluded: false, source: 'default', sourceFolderId: null, sourceFolderName: null },
    rule: null,
    ...overrides,
  };
}

function renderPanel(onSaved = vi.fn(), onCancel = vi.fn()) {
  render(
    <I18nProvider>
      <GallerySettingsPanel
        folderId={FOLDER_ID}
        folderName="Documents"
        onSaved={onSaved}
        onCancel={onCancel}
      />
    </I18nProvider>,
  );
  return { onSaved, onCancel };
}

describe('GallerySettingsPanel', () => {
  it('explains the default and shows the inherited-included state', async () => {
    installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective()),
    });
    renderPanel();

    expect(await screen.findByText(/inclusi nella galleria salvo esclusione/)).toBeInTheDocument();
    const state = await screen.findByTestId('gallery-effective');
    expect(state).toHaveTextContent('Foto — Ereditato: incluso nella galleria (predefinito)');
    expect(state).toHaveTextContent('Video — Ereditato: incluso nella galleria (predefinito)');
  });

  it('shows inherited exclusion naming the ancestor and explicit rules', async () => {
    installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective({
        photos: { excluded: true, source: 'inherited', sourceFolderId: 'p1', sourceFolderName: 'Documents' },
        videos: {
          excluded: true, source: 'rule', sourceFolderId: FOLDER_ID, sourceFolderName: 'Scans',
        },
      })),
    });
    renderPanel();

    const state = await screen.findByTestId('gallery-effective');
    expect(state).toHaveTextContent('Foto — Ereditato: escluso da “Documents”');
    expect(state).toHaveTextContent('Video — Esplicito: escluso qui');
  });

  it('saves an exclude rule with the chosen kinds', async () => {
    let putBody: string | null = null;
    const mock = installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective()),
      'PUT /api/media-library/rules': (req) => {
        putBody = req.body;
        return jsonResponse({
          id: 'r1', folderId: FOLDER_ID, folderName: 'Documents', ruleType: 'exclude',
          appliesToPhotos: true, appliesToVideos: false, appliesToChildren: true,
          createdAt: '2026-06-11T10:00:00Z', updatedAt: '2026-06-11T10:00:00Z',
        });
      },
    });
    const { onSaved } = renderPanel();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText(/Escludi dalla galleria/));
    await user.click(screen.getByLabelText('Video')); // photos-only rule
    await user.click(screen.getByRole('button', { name: 'Salva' }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(onSaved.mock.calls[0][0]).toMatch(/è ora escluso dalla galleria/);
    expect(mock.calls.some((c) => c.method === 'PUT')).toBe(true);
    const parsed = JSON.parse(putBody!);
    expect(parsed).toMatchObject({
      folderId: FOLDER_ID,
      ruleType: 'exclude',
      appliesToPhotos: true,
      appliesToVideos: false,
      appliesToChildren: true,
    });
  });

  it('re-includes a subfolder under an excluded parent', async () => {
    let putBody: string | null = null;
    installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective({
        photos: { excluded: true, source: 'inherited', sourceFolderId: 'p1', sourceFolderName: 'Documents' },
        videos: { excluded: true, source: 'inherited', sourceFolderId: 'p1', sourceFolderName: 'Documents' },
      })),
      'PUT /api/media-library/rules': (req) => {
        putBody = req.body;
        return jsonResponse({
          id: 'r2', folderId: FOLDER_ID, folderName: 'FamilyPhotos', ruleType: 'include',
          appliesToPhotos: true, appliesToVideos: true, appliesToChildren: true,
          createdAt: '2026-06-11T10:00:00Z', updatedAt: '2026-06-11T10:00:00Z',
        });
      },
    });
    const { onSaved } = renderPanel();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText(/Includi nella galleria/));
    await user.click(screen.getByRole('button', { name: 'Salva' }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(JSON.parse(putBody!)).toMatchObject({ ruleType: 'include' });
  });

  it('removes the explicit rule when switching back to follow-parent', async () => {
    const deleted: string[] = [];
    installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective({
        photos: { excluded: true, source: 'rule', sourceFolderId: FOLDER_ID, sourceFolderName: 'Documents' },
        videos: { excluded: true, source: 'rule', sourceFolderId: FOLDER_ID, sourceFolderName: 'Documents' },
        rule: {
          id: 'r9', folderId: FOLDER_ID, folderName: 'Documents', ruleType: 'exclude',
          appliesToPhotos: true, appliesToVideos: true, appliesToChildren: true,
          createdAt: '2026-06-11T09:00:00Z', updatedAt: '2026-06-11T09:00:00Z',
        },
      })),
      'DELETE /api/media-library/rules/r9': (req) => {
        deleted.push(req.url);
        return new Response(null, { status: 204 });
      },
    });
    const { onSaved } = renderPanel();
    const user = userEvent.setup();

    // The panel pre-selects the existing rule; switch to inherit and save.
    await user.click(await screen.findByLabelText(/Segui la cartella superiore/));
    await user.click(screen.getByRole('button', { name: 'Salva' }));

    await waitFor(() => expect(onSaved).toHaveBeenCalled());
    expect(deleted).toHaveLength(1);
  });

  it('refuses a rule that applies to nothing', async () => {
    installFetchMock({
      'GET /api/media-library/effective': () => jsonResponse(effective()),
    });
    renderPanel();
    const user = userEvent.setup();

    await user.click(await screen.findByLabelText(/Escludi dalla galleria/));
    await user.click(screen.getByLabelText('Foto'));
    await user.click(screen.getByLabelText('Video'));
    await user.click(screen.getByRole('button', { name: 'Salva' }));

    expect(await screen.findByText('Seleziona foto, video o entrambi.')).toBeInTheDocument();
  });
});
