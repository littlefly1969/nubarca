import { describe, expect, it } from 'vitest';
import type { AlbumRole } from '@nubarca/api-client';
import {
  OWNER_ALBUM_CAPABILITIES,
  getAlbumExperienceCapabilities,
  type AlbumExperienceCapabilities,
} from './albumCapabilities';

// A unified EXPERIENCE is not unified AUTHORITY. These tests are the second
// half of that sentence.

function member(
  role: AlbumRole,
  over: { canEdit?: boolean; allowOriginalDownload?: boolean } = {},
): AlbumExperienceCapabilities {
  return getAlbumExperienceCapabilities({
    ownership: 'member',
    role,
    canEdit: over.canEdit ?? false,
    allowOriginalDownload: over.allowOriginalDownload ?? false,
  });
}

// Everything a member must never be able to do, whatever their role.
const OWNER_ONLY = [
  'selectMedia', 'editMetadata', 'removeFromAlbum', 'exclude', 'trash',
  'moveToPersonal', 'manageMembers', 'manageSettings', 'deleteAlbum',
  'configureParty', 'showOnTv', 'peopleActions', 'similarityActions',
] as const;

describe('album experience capabilities', () => {
  it('gives every member the full browsing experience', () => {
    for (const role of ['viewer', 'contributor', 'editor'] as const) {
      const caps = member(role);
      expect(caps.browse).toBe(true);
      expect(caps.filterByKind).toBe(true);
      expect(caps.playback).toBe(true);
      // Play mutates nothing, which is exactly why a Viewer gets it.
      expect(caps.play).toBe(true);
    }
  });

  it('gives no member ANY owner authority', () => {
    for (const role of ['viewer', 'contributor', 'editor'] as const) {
      const caps = member(role, { canEdit: true, allowOriginalDownload: true });
      for (const key of OWNER_ONLY) {
        expect({ role, key, value: caps[key] }).toEqual({ role, key, value: false });
      }
    }
  });

  it('gates download on the membership, not on the role', () => {
    expect(member('viewer').download).toBe(false);
    expect(member('editor').download).toBe(false);
    expect(member('viewer', { allowOriginalDownload: true }).download).toBe(true);
    // Viewing and downloading the original are different capabilities.
    expect(member('viewer').playback).toBe(true);
  });

  it('lets a Contributor and an Editor contribute, and a Viewer not', () => {
    expect(member('viewer').contribute).toBe(false);
    expect(member('contributor').contribute).toBe(true);
    expect(member('editor').contribute).toBe(true);
  });

  it('keeps withdrawal available after a demotion to Viewer', () => {
    // A contributor demoted to Viewer may still take their own media back out;
    // which items that is remains the per-item `canWithdraw`.
    expect(member('viewer').withdrawOwnContribution).toBe(true);
  });

  it('lets the SERVER decide curation, never the role label', () => {
    expect(member('editor', { canEdit: false }).editAlbumDetails).toBe(false);
    expect(member('editor', { canEdit: false }).curateContent).toBe(false);
    expect(member('editor', { canEdit: true }).editAlbumDetails).toBe(true);
    // And a canEdit the server sent for a lesser role is still honoured: the
    // backend is the authority in both directions.
    expect(member('viewer', { canEdit: true }).curateContent).toBe(true);
  });

  it('gives the owner their own album in full', () => {
    const owner = OWNER_ALBUM_CAPABILITIES;
    for (const key of OWNER_ONLY) {
      expect({ key, value: owner[key] }).toEqual({ key, value: true });
    }
    expect(owner.download).toBe(true);
    expect(owner.play).toBe(true);
    expect(owner.curateContent).toBe(true);
    // Contributing is linking your media into somebody ELSE's album; the owner
    // adding their own is a different action on a different endpoint.
    expect(owner.contribute).toBe(false);
    expect(owner.withdrawOwnContribution).toBe(false);
  });
});
