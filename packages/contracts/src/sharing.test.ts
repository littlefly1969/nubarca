// Album sharing contract (§25-§29, §49).
//
// Most of what matters here is enforced by the SHAPE of the types rather than
// by a runtime check, so these tests pin the shape: what a client is able to
// read, and what it has no way to read because the field does not exist.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  ALBUM_INVITATIONS_PATH,
  albumInvitationPath,
  albumMemberDownloadPath,
  albumMemberPartyMessagesPath,
  albumMemberPath,
  albumMemberRolePath,
  albumMembersPath,
  albumRecipientResolvePath,
  isActiveMembership,
  isHistoricalMembership,
  sharedAlbumHasMore,
  sharedAlbumCapabilities,
  sharedAlbumItemsPath,
  sharedAlbumItemsQueryToParams,
  type AlbumMember,
  type AlbumMembershipState,
  type AlbumRole,
} from './sharing.ts';
import { toQueryString } from './query.ts';

// ── membership state ────────────────────────────────────────────────────────

test('only an accepted membership can act', () => {
  const states: AlbumMembershipState[] = ['pending', 'accepted', 'declined', 'revoked'];
  assert.deepEqual(states.filter(isActiveMembership), ['accepted']);
});

test('declined and revoked are history, not live grants', () => {
  // §25: the owner sees both, and the UI must be able to tell them apart from
  // a pending invitation that can still be answered.
  assert.deepEqual(
    (['pending', 'accepted', 'declined', 'revoked'] as AlbumMembershipState[])
      .filter(isHistoricalMembership),
    ['declined', 'revoked'],
  );
  assert.equal(isHistoricalMembership('pending'), false);
});

// ── recipient capabilities (§27) ────────────────────────────────────────────

test('a viewer views and nothing more', () => {
  const c = sharedAlbumCapabilities({ role: 'viewer', allowOriginalDownload: false, canEdit: false });
  assert.deepEqual(c, {
    canView: true,
    canContribute: false,
    canWithdrawOwnContribution: false,
    canEditCollaboratively: false,
    canDownloadOriginal: false,
  });
});

test('contributors and editors may contribute and withdraw their own', () => {
  for (const role of ['contributor', 'editor'] as AlbumRole[]) {
    const c = sharedAlbumCapabilities({ role, allowOriginalDownload: false, canEdit: false });
    assert.equal(c.canContribute, true, role);
    assert.equal(c.canWithdrawOwnContribution, true, role);
  }
});

test('collaborative editing follows the SERVER answer, not the role label', () => {
  // §27: capabilities are never inferred from a label. An editor whose grant
  // the server has not confirmed does not get the controls.
  const editorWithout = sharedAlbumCapabilities({
    role: 'editor', allowOriginalDownload: false, canEdit: false,
  });
  assert.equal(editorWithout.canEditCollaboratively, false);

  const editorWith = sharedAlbumCapabilities({
    role: 'editor', allowOriginalDownload: false, canEdit: true,
  });
  assert.equal(editorWith.canEditCollaboratively, true);
});

test('original download is its own permission, orthogonal to the role', () => {
  const viewerMayDownload = sharedAlbumCapabilities({
    role: 'viewer', allowOriginalDownload: true, canEdit: false,
  });
  assert.equal(viewerMayDownload.canDownloadOriginal, true);
  assert.equal(viewerMayDownload.canContribute, false);
});

// ── privacy is in the shape (§26) ───────────────────────────────────────────

test('a member row carries a MASKED address and no identity beyond it', () => {
  const member: AlbumMember = {
    membershipId: 'm-1',
    displayName: 'Laura',
    maskedEmail: 'l***a@nubarca.local',
    role: 'viewer',
    state: 'accepted',
    allowOriginalDownload: false,
    canManagePartyMessages: false,
    invitedAt: '2026-01-01T00:00:00Z',
    acceptedAt: '2026-01-02T00:00:00Z',
    declinedAt: null,
    revokedAt: null,
  };
  const keys = Object.keys(member);
  // The fields a client CANNOT have, because the type has no room for them.
  for (const forbidden of ['email', 'userId', 'user_id', 'accountId']) {
    assert.ok(!keys.includes(forbidden), `a member must not carry ${forbidden}`);
  }
  // And the row is addressed by the membership, never by the person.
  assert.equal(albumMemberPath('a1', member.membershipId), '/api/albums/a1/members/m-1');
});

test('the party-message delegation is a flag on a membership, not a role', () => {
  // §37: it is not an AlbumRole value, so it cannot be granted by changing one.
  const roles: AlbumRole[] = ['viewer', 'contributor', 'editor'];
  assert.ok(!roles.includes('moderator' as AlbumRole));
  assert.equal(
    albumMemberPartyMessagesPath('a1', 'm-1'),
    '/api/albums/a1/members/m-1/party-messages',
  );
});

// ── routes ──────────────────────────────────────────────────────────────────

test('owner and recipient routes are canonical and distinct', () => {
  assert.equal(albumMembersPath('a1'), '/api/albums/a1/members');
  assert.equal(albumRecipientResolvePath('a1'), '/api/albums/a1/members/resolve');
  assert.equal(albumMemberRolePath('a1', 'm1'), '/api/albums/a1/members/m1/role');
  assert.equal(albumMemberDownloadPath('a1', 'm1'), '/api/albums/a1/members/m1/download');
  assert.equal(ALBUM_INVITATIONS_PATH, '/api/shared-albums/invitations');
  assert.equal(albumInvitationPath('m1', 'accept'), '/api/shared-albums/invitations/m1/accept');
  assert.equal(albumInvitationPath('m1', 'decline'), '/api/shared-albums/invitations/m1/decline');
  assert.equal(sharedAlbumItemsPath('a1'), '/api/shared-albums/a1/items');
});

// ── paging (§28) ────────────────────────────────────────────────────────────

test('a null cursor IS the end; there is no hasMore on the wire', () => {
  // The mobile copy of this type used to declare a hasMore the server never
  // sends. The derivation now lives in one place instead.
  const page = { items: [], nextCursor: null, total: 0, photoCount: 0, videoCount: 0 };
  assert.equal(sharedAlbumHasMore(page), false);
  assert.equal(sharedAlbumHasMore({ ...page, nextCursor: 'c2' }), true);
});

test('the shared listing serializes only what the server accepts', () => {
  const qs = (q: Parameters<typeof sharedAlbumItemsQueryToParams>[0]) =>
    toQueryString(sharedAlbumItemsQueryToParams(q));
  assert.equal(qs({}), '');
  assert.equal(qs({ kind: 'all' }), ''); // the default is not a filter
  assert.equal(qs({ kind: 'image' }), 'kind=image');
  assert.equal(qs({ kind: 'video', cursor: 'c1', limit: 40 }), 'kind=video&cursor=c1&limit=40');
});
