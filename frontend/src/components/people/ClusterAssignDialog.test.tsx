import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ClusterAssignDialog } from './ClusterAssignDialog';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { I18nProvider } from '../../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const people = [{ personId: 'p-1', name: 'Alice', faceCount: 5, representative: null }];

it('shows cluster counts and confirms the association', async () => {
  const onDone = vi.fn();
  const mock = installFetchMock({
    'POST /api/people/p-1/clusters/c-1/assign': ({ body }) => {
      const dry = JSON.parse(String(body)).dryRun === true;
      return jsonResponse({
        assignedCount: dry ? 4 : 4,
        skippedAlreadyAssignedCount: 1,
        skippedIgnoredCount: 0,
        skippedIneligibleCount: 0,
        clusterStatus: dry ? 'suggested' : 'confirmed',
      });
    },
  });
  render(
    <I18nProvider>
      <ClusterAssignDialog clusterId="c-1" faceCount={5} people={people} onDone={onDone} onClose={() => {}} />
    </I18nProvider>,
  );
  expect(screen.getByRole('dialog', { name: 'Associa cluster a persona' })).toBeTruthy();
  expect(screen.getByText('Volti nel gruppo:')).toBeTruthy();

  // Select an existing person → dry-run preview shows counts.
  await userEvent.click(screen.getByRole('button', { name: 'Alice' }));
  await waitFor(() => expect(screen.getByText('Verranno assegnati:')).toBeTruthy());
  expect(screen.getByText(/Già assegnati ad altre persone/)).toBeTruthy();
  // The "move also assigned faces" checkbox appears because some are skipped.
  expect(screen.getByLabelText(/Sposta anche i volti già assegnati/)).toBeTruthy();

  // Confirm → real (non-dryRun) call.
  await userEvent.click(screen.getByRole('button', { name: 'Associa' }));
  await waitFor(() => {
    const real = mock.calls.find((c) => c.method === 'POST' && JSON.parse(String(c.body)).dryRun === false);
    expect(real).toBeTruthy();
  });
  expect(onDone).toHaveBeenCalled();
});

it('renders no storage internals', async () => {
  installFetchMock({});
  const { baseElement } = render(
    <I18nProvider>
      <ClusterAssignDialog clusterId="c-1" faceCount={3} people={people} onDone={() => {}} onClose={() => {}} />
    </I18nProvider>,
  );
  for (const needle of ['blobObjectId', 'storageKey', 'sha256', '/storage/objects/', 'embeddingBytes']) {
    expect(baseElement.innerHTML).not.toContain(needle);
  }
});
