import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { OrganizeByDateWizard } from './OrganizeByDateWizard';
import { AuthedWrapper, installFetchMock, jsonResponse, errorResponse } from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const DRY_RUN = {
  summary: {
    candidateCount: 5,
    withDateCount: 4,
    missingDateCount: 1,
    toMoveCount: 4,
    alreadyOrganizedCount: 0,
    skippedMissingCount: 1,
    skippedConflictCount: 0,
    foldersToCreateCount: 2,
    estimatedOperations: 6,
    bySource: { userOverride: 0, metadataOriginal: 4, metadataFallback: 0, fileCreatedFallback: 0, missing: 1 },
  },
  samples: [
    { name: 'IMG_1.jpg', currentPath: '/IMG_1.jpg', targetPath: '/Photos/2024/2024-05-17/IMG_1.jpg', effectiveDateTaken: '2024-05-17T09:00:00Z', dateTakenSource: 'metadata_original', action: 'move' },
  ],
};

function renderWizard(onDone = vi.fn()) {
  render(
    <AuthedWrapper>
      <OrganizeByDateWizard
        currentFolderId={null}
        currentFolderName="Home"
        selectedFileIds={[]}
        onClose={vi.fn()}
        onDone={onDone}
      />
    </AuthedWrapper>,
  );
  return onDone;
}

describe('OrganizeByDateWizard', () => {
  it('renders the configure step with scope, template and policy options', () => {
    installFetchMock({});
    renderWizard();
    expect(screen.getByRole('dialog', { name: 'Organizza le foto per data' })).toBeInTheDocument();
    expect(screen.getByText('Quali foto?')).toBeInTheDocument();
    expect(screen.getByLabelText('Struttura delle cartelle')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Anteprima' })).toBeInTheDocument();
  });

  it('runs a dry-run preview and shows the summary + examples', async () => {
    const mock = installFetchMock({
      'POST /api/photo-organizer/date-taken/dry-run': () => jsonResponse(DRY_RUN),
    });
    renderWizard();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Anteprima' }));

    expect(await screen.findByText('Da spostare')).toBeInTheDocument();
    expect(screen.getByText('IMG_1.jpg')).toBeInTheDocument();
    expect(screen.getByText('/Photos/2024/2024-05-17/IMG_1.jpg')).toBeInTheDocument();
    // The dry-run request carried the chosen options.
    const call = mock.calls.find((c) => c.url.includes('/dry-run'));
    expect(call?.body).toContain('"template":"yyyy/yyyy-MM-dd"');
    expect(screen.getByRole('button', { name: /Organizza 4 foto/ })).toBeInTheDocument();
  });

  it('executes the run and polls to a result, notifying onDone', async () => {
    const onDone = vi.fn();
    installFetchMock({
      'POST /api/photo-organizer/date-taken/dry-run': () => jsonResponse(DRY_RUN),
      'POST /api/photo-organizer/date-taken/run': () =>
        jsonResponse({ runId: 'run-1', jobId: 'job-1', status: 'queued' }),
      'GET /api/photo-organizer/date-taken/runs/run-1': () =>
        jsonResponse({
          runId: 'run-1', kind: 'date_taken', status: 'succeeded', cancellationPending: false,
          template: 'yyyy/yyyy-MM-dd', targetRootName: 'Photos', scope: 'folder',
          candidateCount: 5, movedCount: 4, alreadyOrganizedCount: 0,
          skippedMissingDateCount: 1, skippedConflictCount: 0, failedCount: 0, foldersCreatedCount: 2,
          errorSummary: null, createdAt: '2026-06-15T00:00:00Z', startedAt: '2026-06-15T00:00:01Z', completedAt: '2026-06-15T00:00:05Z',
        }),
    });
    renderWizard(onDone);
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Anteprima' }));
    await user.click(await screen.findByRole('button', { name: /Organizza 4 foto/ }));

    expect(await screen.findByText(/Organizzate 4 foto\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Fatto' })).toBeInTheDocument();
    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(onDone.mock.calls[0][0]).toMatchObject({ tone: 'info' });
  });

  it('surfaces a 400 validation error from the dry-run', async () => {
    installFetchMock({
      'POST /api/photo-organizer/date-taken/dry-run': () => errorResponse(400, { error: 'Invalid folder template.' }),
    });
    renderWizard();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Anteprima' }));
    expect(await screen.findByText('Invalid folder template.')).toBeInTheDocument();
  });

  it('defaults the scope to the current selection when files are selected', () => {
    installFetchMock({});
    render(
      <AuthedWrapper>
        <OrganizeByDateWizard
          currentFolderId={null}
          currentFolderName="Home"
          selectedFileIds={['f1', 'f2']}
          onClose={vi.fn()}
          onDone={vi.fn()}
        />
      </AuthedWrapper>,
    );
    const selected = screen.getByLabelText(/Selezionate \(2\)/) as HTMLInputElement;
    expect(selected.checked).toBe(true);
  });

  it('does not render sensitive fields', async () => {
    installFetchMock({
      'POST /api/photo-organizer/date-taken/dry-run': () => jsonResponse(DRY_RUN),
    });
    renderWizard();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Anteprima' }));
    await screen.findByText('Da spostare');
    const html = document.body.innerHTML;
    for (const needle of ['storageKey', 'blobObjectId', 'sha256', 'fileItemId', 'ownerUserId', 'objects/']) {
      expect(html).not.toContain(needle);
    }
  });
});
