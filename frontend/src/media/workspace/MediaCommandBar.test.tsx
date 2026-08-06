import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper } from '../../test-utils';
import { MediaCommandBar } from './MediaCommandBar';

afterEach(cleanup);

type BarProps = Parameters<typeof MediaCommandBar>[0];

// The spies are kept OUT of the props spread so their Mock types survive the
// `Partial<BarProps>` override merge and stay assertable.
function renderBar(overrides: Partial<BarProps> = {}) {
  const spies = {
    onSearchText: vi.fn(),
    onSubmitSearch: vi.fn(),
    onOpenFilters: vi.fn(),
    onChangeSort: vi.fn(),
    onChangeScope: vi.fn(),
  };
  const props: BarProps = {
    searchPlaceholder: 'Cerca nella libreria',
    searchText: '',
    activeFilterCount: 0,
    showSort: true,
    sort: 'created',
    direction: 'desc',
    scope: 'active',
    ...spies,
    ...overrides,
  };
  render(<AuthedWrapper><MediaCommandBar {...props} /></AuthedWrapper>);
  return spies;
}

describe('MediaCommandBar', () => {
  it('groups search, filters, sort and library scope into one toolbar', () => {
    renderBar();
    const bar = within(screen.getByTestId('ws-command-bar'));
    expect(bar.getByTestId('ws-search-input')).toBeInTheDocument();
    expect(bar.getByTestId('ws-open-filters')).toBeInTheDocument();
    expect(bar.getByTestId('ws-sort')).toBeInTheDocument();
    // The scope selector lives INSIDE the command bar, not as a separate
    // full-width tab row above the grid.
    expect(bar.getByTestId('media-scope-tabs')).toBeInTheDocument();
  });

  it('exposes the search field as a search landmark', () => {
    renderBar();
    const search = screen.getByRole('search');
    expect(within(search).getByLabelText('Cerca nella libreria')).toBeInTheDocument();
  });

  it('submits the search on Enter and on blur', async () => {
    const props = renderBar({ searchText: 'sunset' });
    const user = userEvent.setup();
    const input = screen.getByTestId('ws-search-input');

    await user.click(input);
    await user.keyboard('{Enter}');
    expect(props.onSubmitSearch).toHaveBeenCalled();

    props.onSubmitSearch.mockClear();
    await user.tab();
    expect(props.onSubmitSearch).toHaveBeenCalled();
  });

  it('shows no filter badge when nothing is applied', () => {
    renderBar({ activeFilterCount: 0 });
    expect(screen.queryByTestId('ws-filter-count')).not.toBeInTheDocument();
    expect(screen.getByTestId('ws-open-filters')).toHaveAttribute('aria-label', 'Filtri');
    expect(screen.getByTestId('ws-open-filters').className).not.toContain('has-active');
  });

  it('shows the active-filter count on the filter trigger', () => {
    renderBar({ activeFilterCount: 3 });
    expect(screen.getByTestId('ws-filter-count')).toHaveTextContent('3');
    // Not colour alone: the count is also in the accessible name.
    expect(screen.getByTestId('ws-open-filters')).toHaveAttribute('aria-label', 'Filtri (3 attivi)');
    expect(screen.getByTestId('ws-open-filters').className).toContain('has-active');
  });

  it('opens the filter sheet from the trigger', async () => {
    const props = renderBar();
    await userEvent.setup().click(screen.getByTestId('ws-open-filters'));
    expect(props.onOpenFilters).toHaveBeenCalled();
  });

  it('changes sort field and direction with unchanged semantics', async () => {
    const props = renderBar({ sort: 'created', direction: 'desc' });
    await userEvent.setup().selectOptions(
      within(screen.getByTestId('ws-sort')).getByRole('combobox'),
      'name:asc',
    );
    expect(props.onChangeSort).toHaveBeenCalledWith('name', 'asc');
  });

  it('hides sort while a semantic search is ranking by relevance', () => {
    renderBar({ showSort: false });
    expect(screen.queryByTestId('ws-sort')).not.toBeInTheDocument();
  });

  it('switches the library scope with the same two options', async () => {
    const props = renderBar({ scope: 'active' });
    const scope = within(screen.getByTestId('media-scope-tabs'));
    expect(scope.getAllByRole('tab')).toHaveLength(2);
    expect(screen.getByTestId('media-scope-tab-active')).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByTestId('media-scope-tab-excluded')).toHaveAttribute('aria-selected', 'false');

    await userEvent.setup().click(screen.getByTestId('media-scope-tab-excluded'));
    expect(props.onChangeScope).toHaveBeenCalledWith('excluded');
  });

  it('marks the selected scope with a fill and weight, not colour alone', () => {
    renderBar({ scope: 'excluded' });
    expect(screen.getByTestId('media-scope-tab-excluded').className).toContain('is-active');
    expect(screen.getByTestId('media-scope-tab-active').className).not.toContain('is-active');
  });
});
