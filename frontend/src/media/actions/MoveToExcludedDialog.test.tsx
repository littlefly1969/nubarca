import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MoveToExcludedDialog } from './MoveToExcludedDialog';
import { AuthedWrapper } from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const ok = { requested: 1, changed: 1, unchanged: 0, notFoundOrNotOwned: 0 };

it('shows the pluralized title and explains the effect (no password, no deletion)', async () => {
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={2} onClose={vi.fn()} execute={vi.fn()} />
    </AuthedWrapper>,
  );
  expect(await screen.findByText('Sposta 2 elementi in Esclusi')).toBeTruthy();
  expect(screen.getByText(/restano nelle loro cartelle/)).toBeTruthy();
  expect(screen.getByText(/Spariranno da gallerie, album e ricerca/)).toBeTruthy();
  // No password field — excluding needs no unlock.
  expect(screen.queryByTestId('vault-password')).toBeNull();
});

it('runs execute on confirm and closes', async () => {
  const execute = vi.fn().mockResolvedValue(ok);
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={1} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );
  await userEvent.click(await screen.findByTestId('move-to-excluded-confirm'));
  await waitFor(() => expect(execute).toHaveBeenCalledTimes(1));
  await waitFor(() => expect(onClose).toHaveBeenCalled());
});

it('cancel closes without calling execute', async () => {
  const execute = vi.fn();
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={1} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );
  await userEvent.click(await screen.findByTestId('move-to-excluded-cancel'));
  expect(onClose).toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});

it('Escape cancels', async () => {
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={1} onClose={onClose} execute={vi.fn()} />
    </AuthedWrapper>,
  );
  await screen.findByTestId('move-to-excluded-dialog');
  await userEvent.keyboard('{Escape}');
  expect(onClose).toHaveBeenCalled();
});

it('shows a generic error and stays open on failure', async () => {
  const execute = vi.fn().mockRejectedValue(new Error('network down'));
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={1} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );
  await userEvent.click(await screen.findByTestId('move-to-excluded-confirm'));
  expect(await screen.findByTestId('move-to-excluded-error')).toBeTruthy();
  expect(onClose).not.toHaveBeenCalled();
});

it('does not double-submit while busy', async () => {
  let resolveExecute: (() => void) | undefined;
  const execute = vi.fn(() => new Promise<typeof ok>((resolve) => { resolveExecute = () => resolve(ok); }));
  render(
    <AuthedWrapper>
      <MoveToExcludedDialog count={1} onClose={vi.fn()} execute={execute} />
    </AuthedWrapper>,
  );
  const confirm = await screen.findByTestId('move-to-excluded-confirm');
  await userEvent.click(confirm);
  expect(confirm).toBeDisabled();
  await userEvent.click(confirm); // ignored while busy
  expect(execute).toHaveBeenCalledTimes(1);
  resolveExecute!();
});
