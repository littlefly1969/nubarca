import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, it, vi } from 'vitest';
import { MemoryRouter } from 'react-router';
import { WorkspaceMenu } from './WorkspaceMenu';

afterEach(cleanup);
function setup() {
  const select = vi.fn();
  render(<MemoryRouter><WorkspaceMenu label="Actions" testId="menu-trigger" actions={[
    { id: 'first', label: 'First', onSelect: select },
    { id: 'scope', label: 'Scope', checked: true, checkRole: 'radio', onSelect: select },
    { id: 'last', label: 'Print', href: '/print' },
  ]} /><button>Next control</button></MemoryRouter>);
  return { select, user: userEvent.setup() };
}

it('opens at the last item with ArrowUp, walks commands and returns focus on Escape', async () => {
  const { user } = setup();
  screen.getByTestId('menu-trigger').focus();
  await user.keyboard('{ArrowUp}');
  expect(screen.getByRole('menuitem', { name: 'Print' })).toHaveFocus();
  await user.keyboard('{Home}');
  expect(screen.getByRole('menuitem', { name: 'First' })).toHaveFocus();
  await user.keyboard('{ArrowDown}');
  expect(screen.getByRole('menuitemradio', { name: 'Scope' })).toHaveFocus();
  expect(screen.getByRole('menuitemradio')).toHaveAttribute('aria-checked', 'true');
  await user.keyboard('{Escape}');
  expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  expect(screen.getByTestId('menu-trigger')).toHaveFocus();
});

it('activates a command once and leaves the menu with Tab', async () => {
  const { user, select } = setup();
  await user.click(screen.getByTestId('menu-trigger'));
  await user.keyboard('{Enter}');
  expect(select).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  await user.click(screen.getByTestId('menu-trigger'));
  await user.tab();
  expect(screen.getByText('Next control')).toHaveFocus();
  expect(screen.queryByRole('menu')).not.toBeInTheDocument();
});
