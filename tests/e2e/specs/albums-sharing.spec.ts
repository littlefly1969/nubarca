// Albums and sharing, including the boundary that matters most: one owner's
// media must never be reachable by another.

import { OTHER_OWNER, SEED } from '../src/env';
import { seedIds, signIn } from '../src/fixtures';
import { expect, test } from '../src/fixtures';

test.describe('albums and sharing', () => {
  test('the seeded album detail loads', async ({ ownerPage }) => {
    const ids = seedIds();
    await ownerPage.goto(`/albums/${ids.albumId}`);
    await expect(ownerPage.getByTestId('album-detail-page')).toBeVisible({ timeout: 20_000 });
    await expect(ownerPage.getByText(SEED.photoAlbum, { exact: false }).first()).toBeVisible();
  });

  test('album membership can be mutated and restored', async ({ ownerPage }) => {
    const ids = seedIds();

    // The endpoint answers with a bare array of members.
    const members = async (): Promise<{ fileItemId: string }[]> => {
      const response = await ownerPage.request.get(`/api/albums/${ids.albumId}/items`);
      expect(response.ok()).toBeTruthy();
      return (await response.json()) as { fileItemId: string }[];
    };

    // Mutate an item this test adds itself. Asserting against SEEDED membership
    // would make the result depend on run order and on whether an earlier failure
    // left the album dirty — which is exactly how this test first failed.
    const before = await members();
    const subject = ids.unassignedPhoto;
    expect(before.map((m) => m.fileItemId)).not.toContain(subject);

    const added = await ownerPage.request.post(`/api/albums/${ids.albumId}/items`, {
      data: { fileItemId: subject },
    });
    expect(added.ok()).toBeTruthy();

    const mid = await members();
    expect(mid.length).toBe(before.length + 1);
    expect(mid.map((m) => m.fileItemId)).toContain(subject);

    const removed = await ownerPage.request.delete(
      `/api/albums/${ids.albumId}/items/${subject}`,
    );
    expect(removed.ok()).toBeTruthy();

    // Back to exactly the starting state: this test leaves no residue.
    const end = await members();
    expect(end.length).toBe(before.length);
    expect(end.map((m) => m.fileItemId)).not.toContain(subject);

    // The album page still renders after the mutation.
    await ownerPage.goto(`/albums/${ids.albumId}`);
    await expect(ownerPage.getByTestId('album-detail-page')).toBeVisible({ timeout: 20_000 });
  });

  test('an invalid public share does not expose anything', async ({ page, health }) => {
    // The SPA answers unknown routes, so a bogus token must not yield media.
    health.allowFailures(/\/s\//);
    const response = await page.request.get('/s/this-token-does-not-exist', {
      maxRedirects: 0,
      failOnStatusCode: false,
    });
    expect([400, 401, 403, 404, 410]).toContain(response.status());
  });

  test("a second owner cannot see the first owner's media", async ({ page, health }) => {
    void health;
    const ids = seedIds();
    await signIn(page, OTHER_OWNER.email, OTHER_OWNER.password);

    // The other owner's own library must not contain the first owner's files.
    const listed = await page.request.get('/api/media?limit=100');
    expect(listed.ok()).toBeTruthy();
    const body = (await listed.json()) as { items?: Record<string, unknown>[] };
    const visible = JSON.stringify(body.items ?? []);
    for (const foreign of [ids.unassignedPhoto, ids.assignedPhoto, ids.unassignedVideo]) {
      expect(visible, 'foreign owner media must not be listed').not.toContain(foreign);
    }

    // And addressing the first owner's album directly must be refused.
    const album = await page.request.get(`/api/albums/${ids.albumId}`, {
      failOnStatusCode: false,
    });
    expect([401, 403, 404]).toContain(album.status());
  });
});
