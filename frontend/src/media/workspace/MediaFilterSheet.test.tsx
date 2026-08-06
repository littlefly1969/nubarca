import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Person } from '@nubarca/api-client';
import { AuthedWrapper } from '../../test-utils';
import { MediaFilterSheet } from './MediaFilterSheet';
import { emptyMediaFilters, type MediaKindScope, type MediaWorkspaceFilters } from './mediaWorkspaceQuery';

afterEach(cleanup);

const people: Person[] = [
  { personId: 'p1', name: 'Mario', faceCount: 5, representative: null },
  { personId: 'p2', name: 'Anna', faceCount: 3, representative: null },
];

function renderSheet(mediaKind: MediaKindScope, applied: MediaWorkspaceFilters = emptyMediaFilters()) {
  const onApply = vi.fn();
  render(
    <AuthedWrapper>
      <MediaFilterSheet open mediaKind={mediaKind} applied={applied} people={people} onApply={onApply} onClose={vi.fn()} />
    </AuthedWrapper>,
  );
  return onApply;
}

describe('MediaFilterSheet — People (Foto tab)', () => {
  it('shows the People section only on the image tab', () => {
    renderSheet('image');
    expect(screen.getByTestId('filter-people')).toBeInTheDocument();
    cleanup();
    renderSheet('all');
    expect(screen.queryByTestId('filter-people')).not.toBeInTheDocument();
    cleanup();
    renderSheet('video');
    expect(screen.queryByTestId('filter-people')).not.toBeInTheDocument();
  });

  it('adds an included person and applies it', async () => {
    const onApply = renderSheet('image');
    // Native <select>s also have role combobox; the People pickers are inputs.
    const includeInput = screen.getAllByRole('combobox').filter((el) => el.tagName === 'INPUT')[0];
    await userEvent.type(includeInput, 'Mar');
    await userEvent.click(await screen.findByText('Mario'));
    await userEvent.click(screen.getByTestId('filter-apply'));
    expect(onApply).toHaveBeenCalledWith(
      expect.objectContaining({ photo: expect.objectContaining({ includePeople: ['p1'] }) }),
    );
  });

  it('exposes the all/any mode when more than one person is included', async () => {
    const applied = emptyMediaFilters();
    applied.photo.includePeople = ['p1', 'p2'];
    const onApply = renderSheet('image', applied);
    const modeGroup = screen.getByTestId('filter-people-mode');
    expect(modeGroup).toBeInTheDocument();
    await userEvent.click(within(modeGroup).getByLabelText(/almeno una|any/i));
    await userEvent.click(screen.getByTestId('filter-apply'));
    expect(onApply).toHaveBeenCalledWith(
      expect.objectContaining({ photo: expect.objectContaining({ includePeopleMode: 'any' }) }),
    );
  });

  it('shows a remove-similar control when a similarity anchor is set', async () => {
    const applied = emptyMediaFilters();
    applied.photo.similarTo = 'img-9';
    const onApply = renderSheet('image', applied);
    expect(screen.getByTestId('filter-similar')).toBeInTheDocument();
    await userEvent.click(screen.getByTestId('filter-remove-similar'));
    await userEvent.click(screen.getByTestId('filter-apply'));
    expect(onApply).toHaveBeenCalledWith(
      expect.objectContaining({ photo: expect.objectContaining({ similarTo: '' }) }),
    );
  });
});
