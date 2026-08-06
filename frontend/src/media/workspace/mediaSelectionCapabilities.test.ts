import { describe, expect, it } from 'vitest';
import { getMediaSelectionCapabilities } from './mediaSelectionCapabilities';

const img = { kind: 'image' as const };
const vid = { kind: 'video' as const };

describe('getMediaSelectionCapabilities', () => {
  it('empty selection offers nothing', () => {
    const c = getMediaSelectionCapabilities({ items: [], source: 'library', scope: 'active' });
    expect(c.canAddToAlbum).toBe(false);
    expect(c.canTrash).toBe(false);
    expect(c.canUsePhotoOnlyDestinations).toBe(false);
    expect(c.mixed).toBe(false);
  });

  it('all-images enables photo-only destinations', () => {
    const c = getMediaSelectionCapabilities({ items: [img, img], source: 'library', scope: 'active' });
    expect(c.allImages).toBe(true);
    expect(c.canUsePhotoOnlyDestinations).toBe(true);
  });

  it('all-videos and mixed do NOT enable photo-only destinations', () => {
    const videos = getMediaSelectionCapabilities({ items: [vid, vid], source: 'library', scope: 'active' });
    expect(videos.allVideos).toBe(true);
    expect(videos.canUsePhotoOnlyDestinations).toBe(false);

    const mixed = getMediaSelectionCapabilities({ items: [img, vid], source: 'library', scope: 'active' });
    expect(mixed.mixed).toBe(true);
    expect(mixed.canUsePhotoOnlyDestinations).toBe(false);
  });

  it('Active scope offers move-to-excluded but not restore', () => {
    const c = getMediaSelectionCapabilities({ items: [img], source: 'library', scope: 'active' });
    expect(c.canMoveToExcluded).toBe(true);
    expect(c.canRestore).toBe(false);
  });

  it('Excluded scope offers restore but not move-to-excluded', () => {
    const c = getMediaSelectionCapabilities({ items: [img], source: 'library', scope: 'excluded' });
    expect(c.canRestore).toBe(true);
    expect(c.canMoveToExcluded).toBe(false);
  });

  it('remove-from-album only in the album source', () => {
    expect(getMediaSelectionCapabilities({ items: [img], source: 'album', scope: 'active' })
      .canRemoveFromCurrentAlbum).toBe(true);
    expect(getMediaSelectionCapabilities({ items: [img], source: 'library', scope: 'active' })
      .canRemoveFromCurrentAlbum).toBe(false);
  });

  it('add-to-album / personal / trash available for any non-empty selection', () => {
    const c = getMediaSelectionCapabilities({ items: [img, vid], source: 'album', scope: 'active' });
    expect(c.canAddToAlbum).toBe(true);
    expect(c.canMoveToPersonal).toBe(true);
    expect(c.canTrash).toBe(true);
  });
});
