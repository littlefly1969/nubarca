import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { AuthedWrapper } from '../../test-utils';
import { MediaFilterChips } from './MediaFilterChips';
import type { PeopleIndex } from '../../gallery/workspace/usePeopleIndex';
import { emptyIdentity, type MediaWorkspaceIdentity } from './mediaWorkspaceQuery';

afterEach(cleanup);

const peopleIndex: PeopleIndex = {
  people: [],
  loaded: true,
  nameOf: (id) => ({ p1: 'Mario', p2: 'Anna' }[id] ?? null),
};

const anchor: MediaItem = {
  id: 'img-9', kind: 'image', name: 'IMG_1234.jpg', title: null, displayName: 'IMG_1234.jpg',
  mimeType: 'image/jpeg', sizeBytes: 1, width: 1, height: 1, createdAt: 'x', updatedAt: null,
  takenAt: null, favorite: false, rating: null, thumbnailUrl: '/x', occurrenceCount: 1,
  hasDuplicates: false, hasGps: null,
};

function imageIdentity(): MediaWorkspaceIdentity {
  const id = emptyIdentity({ kind: 'library' });
  id.mediaKind = 'image';
  return id;
}

function renderChips(identity: MediaWorkspaceIdentity, onRemove = vi.fn()) {
  render(
    <AuthedWrapper>
      <MediaFilterChips identity={identity} people={peopleIndex} items={[anchor]} onRemove={onRemove} onClearAll={vi.fn()} />
    </AuthedWrapper>,
  );
  return onRemove;
}

describe('MediaFilterChips — People + Similar', () => {
  it('resolves included/excluded person ids to names', () => {
    const id = imageIdentity();
    id.filters.photo.includePeople = ['p1'];
    id.filters.photo.excludePeople = ['p2'];
    renderChips(id);
    expect(screen.getByTestId('media-chip-people-include')).toHaveTextContent('Mario');
    expect(screen.getByTestId('media-chip-people-exclude')).toHaveTextContent('Anna');
  });

  it('shows the all/any mode for multi-person include', () => {
    const id = imageIdentity();
    id.filters.photo.includePeople = ['p1', 'p2'];
    id.filters.photo.includePeopleMode = 'any';
    renderChips(id);
    expect(screen.getByTestId('media-chip-people-include')).toHaveTextContent(/almeno una|any/i);
  });

  it('labels the similarity chip with the anchor display name and removes it', async () => {
    const id = imageIdentity();
    id.filters.photo.similarTo = 'img-9';
    const onRemove = renderChips(id);
    expect(screen.getByTestId('media-chip-similar')).toHaveTextContent('IMG_1234.jpg');
    await userEvent.click(screen.getByTestId('media-chip-remove-similar'));
    expect(onRemove).toHaveBeenCalledWith('similar');
  });
});
