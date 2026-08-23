import { describe, expect, it } from 'vitest';
import { PERMISSIONS, type MediaItem } from '@nubarca/api-client';
import { buildMediaSelectionActions, type MediaSelectionActionId } from './mediaSelectionActions';
import {
  getMediaSelectionCapabilities,
  type MediaWorkspaceSourceKind,
} from './mediaSelectionCapabilities';
import type { MediaLibraryScope } from './mediaWorkspaceQuery';

// The dock's information architecture, asserted without React.
//
// Two halves have to agree for an entry to appear: the capability matrix (does
// this action make sense for THIS selection here) and the permission set (may
// this user do it at all). These tests drive the real capability function rather
// than a hand-built matrix, so a change on either side of that seam shows up.

const ALL: readonly string[] = Object.values(PERMISSIONS);
const NONE: readonly string[] = [];

const photo = { kind: 'image' } as Pick<MediaItem, 'kind'>;
const video = { kind: 'video' } as Pick<MediaItem, 'kind'>;

function model(
  items: Pick<MediaItem, 'kind'>[],
  permissions: readonly string[] = ALL,
  { source = 'library' as MediaWorkspaceSourceKind, scope = 'active' as MediaLibraryScope } = {},
) {
  return buildMediaSelectionActions({
    capabilities: getMediaSelectionCapabilities({ items, source, scope }),
    permissions,
  });
}

const ids = (actions: { id: MediaSelectionActionId }[]) => actions.map((a) => a.id);

describe('move-to destinations', () => {
  it('offers Personal, Excluded and Trash to a fully permitted user in the active library', () => {
    expect(ids(model([photo, photo]).moveTo)).toEqual(['personal', 'excluded', 'trash']);
  });

  it('omits Personal without private-vault access', () => {
    // Move to Personal IS the private vault operation, not a second name for
    // the library — a user who cannot reach the vault must not be shown it.
    const withoutVault = ALL.filter((p) => p !== PERMISSIONS.privateVaultAccess);
    expect(ids(model([photo], withoutVault).moveTo)).toEqual(['excluded', 'trash']);
  });

  it('marks only Trash as destructive', () => {
    const moveTo = model([photo]).moveTo;
    expect(moveTo.filter((a) => a.destructive).map((a) => a.id)).toEqual(['trash']);
  });

  it('offers nothing at all for an empty selection', () => {
    const empty = model([]);
    expect([...empty.moveTo, ...empty.addTo, ...empty.contextual]).toEqual([]);
  });
});

describe('add-to destinations', () => {
  it('offers Album, Plates and Beauty for an all-photo selection with every permission', () => {
    expect(ids(model([photo, photo]).addTo)).toEqual(['album', 'plates', 'beauty-lab']);
  });

  it('omits both Laboratory destinations without laboratory access', () => {
    const noLab = ALL.filter((p) => p !== PERMISSIONS.laboratoryAccess);
    expect(ids(model([photo], noLab).addTo)).toEqual(['album']);
  });

  it('offers Plates but not Beauty when only the Plates section is held', () => {
    const platesOnly = [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates];
    expect(ids(model([photo], platesOnly).addTo)).toEqual(['album', 'plates']);
  });

  it('offers Beauty but not Plates when only the Aesthetics section is held', () => {
    const aestheticsOnly = [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryAesthetics];
    expect(ids(model([photo], aestheticsOnly).addTo)).toEqual(['album', 'beauty-lab']);
  });

  it('offers no photo-only destination for a video selection', () => {
    expect(ids(model([video, video]).addTo)).toEqual(['album']);
  });

  it('offers no photo-only destination for a MIXED selection', () => {
    // A photo-only action must never run partially — the videos in the
    // selection have nowhere to go, so the destination is not offered at all.
    expect(ids(model([photo, video]).addTo)).toEqual(['album']);
  });

  it('gives an unpermissioned user only the album destination', () => {
    expect(ids(model([photo], NONE).addTo)).toEqual(['album']);
  });
});

describe('contextual actions', () => {
  it('offers Restore, and no Excluded destination, in the Excluded scope', () => {
    const excluded = model([photo], ALL, { scope: 'excluded' });
    expect(ids(excluded.contextual)).toEqual(['restore']);
    expect(ids(excluded.moveTo)).not.toContain('excluded');
    expect(ids(excluded.moveTo)).toEqual(['personal', 'trash']);
  });

  it('offers no Restore in the active library', () => {
    expect(ids(model([photo]).contextual)).toEqual([]);
  });

  it('offers Remove-from-album inside an album, outside the Move menu', () => {
    const album = model([photo], ALL, { source: 'album' });
    expect(ids(album.contextual)).toEqual(['remove-from-album']);
    // Removing a MEMBERSHIP is not a destination alongside Trash.
    expect(ids(album.moveTo)).not.toContain('remove-from-album');
    expect(ids(album.moveTo)).toEqual(['personal', 'excluded', 'trash']);
  });
});
