import { useRef, useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Person } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { EMPTY_GALLERY_QUERY, type GalleryQuery } from '../galleryQuery';
import { GalleryFilterSheet } from './GalleryFilterSheet';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const PEOPLE: Person[] = [
  { personId: 'p1', name: 'Anna', faceCount: 5, representative: null },
  { personId: 'p2', name: 'Marco', faceCount: 3, representative: null },
];

function interpretResponse(over: Record<string, unknown> = {}, ambiguities: unknown[] = []) {
  return jsonResponse({
    draft: {
      version: 1, operation: 'replace',
      peopleInclude: [], peopleExclude: [], peopleMatch: 'all',
      favorite: null, minRating: null, hasGps: null,
      dateTakenFrom: null, dateTakenTo: null, collapseDuplicates: null,
      sort: null, sortDirection: null, metadataSearch: null,
      semanticQuery: null, semanticQueryEnglish: null, semanticTopK: 300,
      ...over,
    },
    resolvedPeople: [], ambiguities, warnings: [], requiresClarification: ambiguities.length > 0,
  });
}

function Harness({ initial, onApplied }: { initial: GalleryQuery; onApplied?: (q: GalleryQuery) => void }) {
  const [applied, setApplied] = useState(initial);
  const [open, setOpen] = useState(true);
  const ref = useRef<HTMLButtonElement>(null);
  return (
    <>
      <button ref={ref} data-testid="trigger" onClick={() => setOpen(true)}>filters</button>
      <GalleryFilterSheet
        open={open}
        appliedQuery={applied}
        people={PEOPLE}
        onApply={(q) => { setApplied(q); onApplied?.(q); setOpen(false); }}
        onClose={() => setOpen(false)}
        returnFocusRef={ref}
        announce={() => {}}
      />
    </>
  );
}

function renderSheet(initial: GalleryQuery, onApplied?: (q: GalleryQuery) => void) {
  return render(
    <AuthedWrapper>
      <Harness initial={initial} onApplied={onApplied} />
    </AuthedWrapper>,
  );
}

describe('GalleryFilterSheet', () => {
  it('opening copies the applied query into the draft (shows applied values)', async () => {
    installFetchMock({});
    renderSheet({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'vacation', favorite: true });
    await screen.findByTestId('gallery-filter-sheet');
    expect(screen.getByTestId('ws-metadata-input')).toHaveValue('vacation');
    expect(screen.getByLabelText('Solo preferiti')).toBeChecked();
  });

  it('Cancel discards the draft; reopening shows the applied values again', async () => {
    installFetchMock({});
    const onApplied = vi.fn();
    renderSheet({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'vacation' }, onApplied);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.clear(screen.getByTestId('ws-metadata-input'));
    await user.type(screen.getByTestId('ws-metadata-input'), 'changed');
    await user.click(screen.getByTestId('ws-cancel'));

    expect(onApplied).not.toHaveBeenCalled();
    // Reopen — draft is reseeded from the (unchanged) applied query.
    await user.click(screen.getByTestId('trigger'));
    await screen.findByTestId('gallery-filter-sheet');
    expect(screen.getByTestId('ws-metadata-input')).toHaveValue('vacation');
  });

  it('Apply commits all supported fields', async () => {
    installFetchMock({});
    const onApplied = vi.fn();
    renderSheet(EMPTY_GALLERY_QUERY, onApplied);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.type(screen.getByTestId('ws-metadata-input'), 'vac');
    await user.type(screen.getByTestId('ws-visual-input'), 'beach');
    await user.selectOptions(screen.getByTestId('ws-min-rating'), '4');
    fireEvent.change(screen.getByTestId('ws-date-from'), { target: { value: '2024-06-01' } });
    await user.click(screen.getByLabelText('Solo preferiti'));
    await user.click(screen.getByTestId('ws-apply'));

    expect(onApplied).toHaveBeenCalledTimes(1);
    const q = onApplied.mock.calls[0][0];
    expect(q.metadataQuery).toBe('vac');
    expect(q.visualQuery).toBe('beach');
    expect(q.minRating).toBe(4);
    expect(q.favorite).toBe(true);
    expect(q.dateTakenFrom).toBe('2024-06-01T00:00:00.000Z');
  });

  it('Reset clears the draft only (does not apply)', async () => {
    installFetchMock({});
    const onApplied = vi.fn();
    renderSheet({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'vacation', favorite: true }, onApplied);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.click(screen.getByTestId('ws-reset'));
    expect(screen.getByTestId('ws-metadata-input')).toHaveValue('');
    expect(screen.getByLabelText('Solo preferiti')).not.toBeChecked(); // favorite back to "Any"
    expect(onApplied).not.toHaveBeenCalled();
  });

  it('a visual query forces relevance (hides sort controls)', async () => {
    installFetchMock({});
    renderSheet({ ...EMPTY_GALLERY_QUERY, visualQuery: 'sunset' });
    await screen.findByTestId('gallery-filter-sheet');
    expect(screen.getByTestId('ws-sort-relevance')).toBeInTheDocument();
    expect(screen.queryByTestId('ws-sort-field')).toBeNull();
  });

  it('a physical-only query shows sort controls', async () => {
    installFetchMock({});
    renderSheet({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'x' });
    await screen.findByTestId('gallery-filter-sheet');
    expect(screen.getByTestId('ws-sort-field')).toBeInTheDocument();
    expect(screen.queryByTestId('ws-sort-relevance')).toBeNull();
  });

  it('semantic and metadata fields can coexist', async () => {
    installFetchMock({});
    const onApplied = vi.fn();
    renderSheet(EMPTY_GALLERY_QUERY, onApplied);
    await screen.findByTestId('gallery-filter-sheet');
    const user = userEvent.setup();
    await user.type(screen.getByTestId('ws-metadata-input'), 'IMG_2024');
    await user.type(screen.getByTestId('ws-visual-input'), 'dog on snow');
    await user.click(screen.getByTestId('ws-apply'));
    const q = onApplied.mock.calls[0][0];
    expect(q.metadataQuery).toBe('IMG_2024');
    expect(q.visualQuery).toBe('dog on snow');
  });

  it('the Describe parser populates the same draft fields', async () => {
    installFetchMock({
      'POST /api/images/interpret-command': () => interpretResponse({ metadataSearch: 'parsed', favorite: true }),
    });
    renderSheet(EMPTY_GALLERY_QUERY);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.click(screen.getByTestId('ws-tab-describe'));
    await user.type(screen.getByTestId('ws-describe-input'), 'favorites of the trip');
    await user.click(screen.getByTestId('ws-describe-run'));

    // It switches back to Manual with the fields populated.
    await waitFor(() => expect(screen.getByTestId('ws-metadata-input')).toHaveValue('parsed'));
    expect(screen.getByLabelText('Solo preferiti')).toBeChecked();
  });

  it('an unresolved ambiguity blocks Apply until it is chosen', async () => {
    installFetchMock({
      'POST /api/images/interpret-command': () => interpretResponse({}, [
        { text: 'Anna', mode: 'include', candidates: [
          { personId: 'p1', name: 'Anna P', faceCount: 5 },
          { personId: 'p9', name: 'Anna R', faceCount: 2 },
        ] },
      ]),
    });
    renderSheet(EMPTY_GALLERY_QUERY);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.click(screen.getByTestId('ws-tab-describe'));
    await user.type(screen.getByTestId('ws-describe-input'), 'photos of Anna');
    await user.click(screen.getByTestId('ws-describe-run'));

    await screen.findByTestId('ws-ambiguities');
    expect(screen.getByTestId('ws-apply')).toBeDisabled();

    await user.click(screen.getByRole('button', { name: /Anna P/ }));
    expect(screen.getByTestId('ws-apply')).toBeEnabled();
  });

  it('a stale parser response cannot overwrite a newer manual edit', async () => {
    let resolveInterpret: (r: Response) => void = () => {};
    installFetchMock({
      'POST /api/images/interpret-command': () =>
        new Promise<Response>((resolve) => { resolveInterpret = resolve; }),
    });
    renderSheet(EMPTY_GALLERY_QUERY);
    await screen.findByTestId('gallery-filter-sheet');

    const user = userEvent.setup();
    await user.click(screen.getByTestId('ws-tab-describe'));
    await user.type(screen.getByTestId('ws-describe-input'), 'favorites');
    await user.click(screen.getByTestId('ws-describe-run')); // interpret now in flight

    // The user switches to Manual and edits the draft while it's pending.
    await user.click(screen.getByTestId('ws-tab-manual'));
    await user.type(screen.getByTestId('ws-metadata-input'), 'MANUAL');

    // The late response arrives with a different value — it must be ignored.
    resolveInterpret(interpretResponse({ metadataSearch: 'FROM_PARSER' }));
    await new Promise((r) => setTimeout(r, 20));

    expect(screen.getByTestId('ws-metadata-input')).toHaveValue('MANUAL');
  });
});
