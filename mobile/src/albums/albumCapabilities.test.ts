// Album capabilities tests (mobile subset): the pure decision that decides
// which actions EXIST on a shared album surface. Server still enforces all of
// it independently.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { getAlbumExperienceCapabilities } from './albumCapabilities.ts';

test('a Viewer can browse/play but never contribute', () => {
  const caps = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'viewer', canEdit: false, allowOriginalDownload: true,
  });
  assert.equal(caps.browse, true);
  assert.equal(caps.playback, true);
  assert.equal(caps.play, true);
  assert.equal(caps.contribute, false);
  assert.equal(caps.download, true); // membership grants originals
});

test('a Contributor may contribute; a Viewer may not', () => {
  const contributor = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'contributor', canEdit: false, allowOriginalDownload: false,
  });
  assert.equal(contributor.contribute, true);

  const viewer = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'viewer', canEdit: false, allowOriginalDownload: false,
  });
  assert.equal(viewer.contribute, false);
});

test('an Editor whose canEdit is FALSE loses curation — the server wins', () => {
  const caps = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'editor', canEdit: false, allowOriginalDownload: false,
  });
  assert.equal(caps.curateContent, false);
  assert.equal(caps.editAlbumDetails, false);
});

test('withdrawal of own contributions survives a downgrade to Viewer', () => {
  const caps = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'viewer', canEdit: false, allowOriginalDownload: false,
  });
  assert.equal(caps.withdrawOwnContribution, true);
});

test('download follows the membership grant, not the role', () => {
  const no = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'editor', canEdit: true, allowOriginalDownload: false,
  });
  const yes = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'viewer', canEdit: false, allowOriginalDownload: true,
  });
  assert.equal(no.download, false);
  assert.equal(yes.download, true);
});

test('owner authority actions stay absent from the member surface', () => {
  const member = getAlbumExperienceCapabilities({
    ownership: 'member', role: 'editor', canEdit: true, allowOriginalDownload: true,
  });
  // The mobile v1 subset does not even expose owner-authority actions.
  assert.equal(member.deleteAlbum, false);
  assert.ok(!('manageMembers' in member));
  assert.ok(!('trash' in member));
});
