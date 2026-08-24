// One Albums destination, two authorities.
//
// jsdom can prove the components render; it cannot prove that a real browser,
// against a real API, shows an owner and a recipient the same album experience
// while the recipient gains none of the owner's authority. That is what this
// spec is for: it runs a live share end to end — invite, accept, browse, filter,
// play, download, revoke — through the front door.
//
// It creates and destroys its own album. The seeded dataset is shared and read
// mostly read-only, so a spec that mutated it would make every other spec's
// result depend on run order.

import type { BrowserContext, Page } from '@playwright/test';
import { OTHER_OWNER } from '../src/env';
import { seedIds, signIn } from '../src/fixtures';
import { expect, test } from '../src/fixtures';

// One album per PROJECT: the projects run against the same database, and two of
// them sharing an album name would collide on the owner's unique-name rule.
const albumName = (project: string) => `E2E Shared ${project}`;

interface Share {
  albumId: string;
  membershipId: string;
}

/** Create an album holding one photo and one video, and invite the other owner. */
async function shareFreshAlbum(ownerPage: Page, name: string): Promise<Share> {
  const ids = seedIds();

  const created = await ownerPage.request.post('/api/albums', {
    data: { name, description: 'live share under test' },
  });
  expect(created.ok(), 'album created').toBeTruthy();
  const albumId = ((await created.json()) as { id: string }).id;

  for (const fileItemId of [ids.assignedPhoto, ids.assignedVideo]) {
    const added = await ownerPage.request.post(`/api/albums/${albumId}/items`, {
      data: { fileItemId },
    });
    expect(added.ok(), 'album item added').toBeTruthy();
  }

  const invited = await ownerPage.request.post(`/api/albums/${albumId}/members`, {
    data: { email: OTHER_OWNER.email, role: 'viewer', allowOriginalDownload: false },
  });
  expect(invited.ok(), 'member invited').toBeTruthy();
  const membershipId = ((await invited.json()) as { membershipId: string }).membershipId;

  return { albumId, membershipId };
}

async function cleanUp(ownerPage: Page, share: Share | null): Promise<void> {
  if (!share) return;
  await ownerPage.request.delete(`/api/albums/${share.albumId}`, { failOnStatusCode: false });
}

/** A second browser context, signed in as the recipient. */
async function asRecipient(context: BrowserContext): Promise<Page> {
  const page = await context.newPage();
  await signIn(page, OTHER_OWNER.email, OTHER_OWNER.password);
  return page;
}

test.describe('one Albums destination', () => {
  test('an invitation is a decision, and an accepted album joins the grid', async (
    { browser, ownerPage },
  ) => {
    const name = albumName(test.info().project.name);
    let share: Share | null = null;
    const context = await browser.newContext();
    try {
      share = await shareFreshAlbum(ownerPage, name);
      const recipient = await asRecipient(context);

      await recipient.goto('/albums');

      // A pending invitation sits ABOVE the grid, never among the cards: it is
      // a decision, not something you can open.
      const invitation = recipient.getByTestId('shared-invitation')
        .filter({ hasText: name });
      await expect(invitation).toBeVisible({ timeout: 20_000 });
      await expect(recipient.getByTestId('album-card').filter({ hasText: name }))
        .toHaveCount(0);

      await invitation.getByTestId('invitation-accept').click();

      // Accepted: now it is an album, in the same grid as the recipient's own.
      const card = recipient.getByTestId('album-card').filter({ hasText: name });
      await expect(card).toBeVisible({ timeout: 20_000 });
      await expect(card).toHaveAttribute('data-owner', 'shared');
      // Whose album it is, and what this membership may do.
      await expect(card.getByTestId('album-card-shared-owner')).toContainText('E2E Owner');
      await expect(card.getByTestId('album-card-role')).toBeVisible();
      // Somebody else's album carries no destructive control at all.
      await expect(card.getByTestId('album-delete-btn')).toHaveCount(0);
      // And it opens through the RECIPIENT's route, never the owner's.
      await expect(card.getByRole('link').first())
        .toHaveAttribute('href', `/shared-albums/${share.albumId}`);
    } finally {
      await context.close();
      await cleanUp(ownerPage, share);
    }
  });

  test('All / Mine / Shared and search work across both collections', async (
    { browser, ownerPage },
  ) => {
    const name = albumName(test.info().project.name);
    let share: Share | null = null;
    const context = await browser.newContext();
    try {
      share = await shareFreshAlbum(ownerPage, name);
      const recipient = await asRecipient(context);
      await recipient.request.post(
        `/api/shared-albums/invitations/${share.membershipId}/accept`);

      await recipient.goto('/albums');
      const shared = recipient.getByTestId('album-card').filter({ hasText: name });
      await expect(shared).toBeVisible({ timeout: 20_000 });

      // Mine hides somebody else's album; Shared shows only it.
      await recipient.getByTestId('albums-scope-mine').click();
      await expect(shared).toHaveCount(0);
      await recipient.getByTestId('albums-scope-shared').click();
      await expect(shared).toBeVisible();
      // The collection is a real address, so it survives a reload.
      await expect(recipient).toHaveURL(/scope=shared/);
      await recipient.reload();
      await expect(recipient.getByTestId('albums-scope-shared'))
        .toHaveAttribute('aria-selected', 'true');

      // Search runs over both collections: whose album it is is not something
      // the person searching has to know.
      await recipient.getByTestId('albums-scope-all').click();
      await recipient.getByTestId('albums-search').fill('E2E Shared');
      await expect(recipient.getByTestId('album-card')).toHaveCount(1);
      await expect(recipient.getByTestId('album-card')).toContainText(name);
    } finally {
      await context.close();
      await cleanUp(ownerPage, share);
    }
  });

  test('the legacy /shared-albums link still lands on the shared collection', async (
    { browser, ownerPage },
  ) => {
    let share: Share | null = null;
    const context = await browser.newContext();
    try {
      share = await shareFreshAlbum(ownerPage, albumName(test.info().project.name));
      const recipient = await asRecipient(context);

      await recipient.goto('/shared-albums');

      await expect(recipient).toHaveURL(/\/albums\?scope=shared/, { timeout: 20_000 });
      await expect(recipient.getByTestId('albums-scope-shared'))
        .toHaveAttribute('aria-selected', 'true');
    } finally {
      await context.close();
      await cleanUp(ownerPage, share);
    }
  });
});

test.describe('a recipient browses a shared album', () => {
  test('tabs, viewer and Play work without any owner authority', async (
    { browser, ownerPage },
  ) => {
    const name = albumName(test.info().project.name);
    let share: Share | null = null;
    const context = await browser.newContext();
    try {
      share = await shareFreshAlbum(ownerPage, name);
      const recipient = await asRecipient(context);
      await recipient.request.post(
        `/api/shared-albums/invitations/${share.membershipId}/accept`);

      await recipient.goto(`/shared-albums/${share.albumId}`);
      await expect(recipient.getByTestId('shared-album-page')).toBeVisible({ timeout: 20_000 });
      await expect(recipient.getByTestId('shared-album-owner')).toContainText('E2E Owner');

      // The same browsing language as the owner's album: All / Photos / Videos
      // with the album's own counts.
      await expect(recipient.getByTestId('media-kind-count-all')).toHaveText('2');
      await expect(recipient.getByTestId('media-kind-count-image')).toHaveText('1');
      await expect(recipient.getByTestId('media-kind-count-video')).toHaveText('1');
      await expect(recipient.getByTestId('shared-media-tile')).toHaveCount(2);

      // Every tile is addressed through the album, never through the owner's
      // library.
      for (const src of await recipient.getByTestId('shared-media-tile')
        .locator('img').evaluateAll((imgs) => imgs.map((i) => i.getAttribute('src') ?? ''))) {
        expect(src).toContain(`/api/shared-albums/${share.albumId}/media/`);
        expect(src).not.toContain('/api/files/');
      }

      // Photos only: the server answers the filter.
      await recipient.getByTestId('media-kind-tab-image').click();
      await expect(recipient.getByTestId('shared-media-tile')).toHaveCount(1);

      // The COMMON viewer, over the URLs the server supplied.
      await recipient.getByTestId('shared-media-tile').first().click();
      await expect(recipient.getByTestId('media-viewer')).toBeVisible();
      await expect(recipient.getByTestId('media-viewer-image'))
        .toHaveAttribute('src', new RegExp(`^/api/shared-albums/${share.albumId}/media/`));
      // No owner affordance exists in it: no metadata drawer, no download.
      await expect(recipient.getByTestId('viewer-details-toggle')).toHaveCount(0);
      await expect(recipient.getByTestId('shared-download')).toHaveCount(0);
      await recipient.keyboard.press('Escape');
      await expect(recipient.getByTestId('media-viewer')).toHaveCount(0);

      // Play: a viewer operation, so a Viewer gets it. It opens the sequence
      // and offers a way to stop.
      await recipient.getByTestId('album-play').click();
      await expect(recipient.getByTestId('media-viewer')).toBeVisible();
      await expect(recipient.getByTestId('viewer-play-stop')).toBeVisible();
      await recipient.getByTestId('viewer-play-stop').click();
      await recipient.keyboard.press('Escape');

      // Nothing on this page mutates the owner's library.
      for (const owned of [
        'media-select-control', 'album-open-settings', 'album-open-share',
        'album-open-copy', 'shared-album-edit', 'shared-album-curate', 'shared-album-add',
      ]) {
        await expect(recipient.getByTestId(owned)).toHaveCount(0);
      }
    } finally {
      await context.close();
      await cleanUp(ownerPage, share);
    }
  });

  test('download is separately permitted, and revocation stops the next request', async (
    { browser, ownerPage },
  ) => {
    const name = albumName(test.info().project.name);
    let share: Share | null = null;
    const context = await browser.newContext();
    try {
      share = await shareFreshAlbum(ownerPage, name);
      const recipient = await asRecipient(context);
      await recipient.request.post(
        `/api/shared-albums/invitations/${share.membershipId}/accept`);

      await recipient.goto(`/shared-albums/${share.albumId}`);
      await recipient.getByTestId('media-kind-tab-image').click();
      await expect(recipient.getByTestId('shared-media-tile')).toHaveCount(1);

      // Viewing is permitted; taking the original away is not.
      await recipient.getByTestId('shared-media-tile').first().click();
      await expect(recipient.getByTestId('media-viewer')).toBeVisible();
      await expect(recipient.getByTestId('shared-download')).toHaveCount(0);
      await recipient.keyboard.press('Escape');

      // The owner permits originals: the control appears, pointed at the
      // album-scoped route.
      const permitted = await ownerPage.request.patch(
        `/api/albums/${share.albumId}/members/${share.membershipId}`,
        { data: { allowOriginalDownload: true } },
      );
      expect(permitted.ok()).toBeTruthy();

      await recipient.reload();
      await recipient.getByTestId('media-kind-tab-image').click();
      await recipient.getByTestId('shared-media-tile').first().click();
      const download = recipient.getByTestId('shared-download');
      await expect(download).toBeVisible({ timeout: 20_000 });
      await expect(download).toHaveAttribute(
        'href', new RegExp(`^/api/shared-albums/${share.albumId}/media/.*/content$`));
      await recipient.keyboard.press('Escape');

      // Revoked: the very next request, with no cache to wait out.
      const revoked = await ownerPage.request.delete(
        `/api/albums/${share.albumId}/members/${share.membershipId}`);
      expect(revoked.ok()).toBeTruthy();

      await recipient.goto(`/shared-albums/${share.albumId}`);
      await expect(recipient.getByTestId('shared-album-unavailable'))
        .toBeVisible({ timeout: 20_000 });
    } finally {
      await context.close();
      await cleanUp(ownerPage, share);
    }
  });
});

test.describe('the owner keeps their album', () => {
  test('an owned album still browses, and gained Play', async ({ ownerPage }) => {
    const ids = seedIds();
    await ownerPage.goto(`/albums/${ids.albumId}`);

    await expect(ownerPage.getByTestId('album-detail-page')).toBeVisible({ timeout: 20_000 });
    // Everything the owner had: tabs, command bar, selection, settings, sharing.
    await expect(ownerPage.getByTestId('media-kind-tabs')).toBeVisible();
    await expect(ownerPage.getByTestId('album-open-settings')).toBeVisible();
    await expect(ownerPage.getByTestId('album-open-share')).toBeVisible();
    await expect(ownerPage.getByTestId('album-open-copy')).toBeVisible();

    // And the same Play the recipient gets.
    await ownerPage.getByTestId('album-play').click();
    await expect(ownerPage.getByTestId('media-viewer')).toBeVisible({ timeout: 20_000 });
    await expect(ownerPage.getByTestId('viewer-play-stop')).toBeVisible();
    await ownerPage.getByTestId('viewer-play-stop').click();
    await ownerPage.keyboard.press('Escape');
    await expect(ownerPage.getByTestId('media-viewer')).toHaveCount(0);
  });
});
