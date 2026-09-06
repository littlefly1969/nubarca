// A whole party, in real browsers, with the four surfaces open at once.
//
// The backend suite proves the rules; this proves the EVENING — that a host, a
// television and two phones can be pointed at the same game and stay agreed
// about it, through a browser, over the network, with nothing shared between
// them but the server.
//
// Four contexts, deliberately: separate contexts mean separate cookie jars, so
// the two guests are as anonymous to each other here as two people at a party
// are. The television gets a 16:9 viewport and the phones get a phone one,
// whatever project is running, because that is what those surfaces ARE — the
// project's own viewport drives the owner's control room, which is the surface
// that genuinely changes shape between desktop and mobile.

import type { BrowserContext, Page } from '@playwright/test';
import { expect, test } from '../src/fixtures';
import { get, login, patch, post } from '../src/api';
import { OWNER, WEB_URL } from '../src/env';

interface PartySettings { partyUrl: string | null }

const TV_VIEWPORT = { width: 1280, height: 720 };
const PHONE_VIEWPORT = { width: 390, height: 844 };

/** The one command the control room is offering right now. */
const primary = (page: Page) => page.getByTestId('party-control-primary');

/** Press it, and wait until the server-confirmed phase has actually moved. */
async function advance(page: Page, expected: string): Promise<void> {
  await expect(primary(page)).toBeEnabled();
  await primary(page).click();
  await expect(primary(page)).toHaveAttribute('data-command', expected, { timeout: 20_000 });
}

test.describe('the party game', () => {
  test('one host, one television and two phones run an evening together', async (
    { browser, ownerPage, health },
  ) => {
    void health;

    // --- A party with two activities, prepared through the API ---------------
    const session = await login(OWNER.email, OWNER.password);
    const album = await post<{ id: string }>(session, '/api/albums', {
      name: `E2E Party Game ${Date.now()}`,
    });
    // Party mode mints the public token the television and the phones are
    // reached with; the game switch is what makes the runtime answer at all.
    await patch(session, `/api/albums/${album.id}/party-settings`, { enabled: true });
    await patch(session, `/api/albums/${album.id}/party-game-settings`, {
      gameEnabled: true, minChallengeIntervalSeconds: 30, maxChallengeIntervalSeconds: 60,
      votesPerGuest: 3, maxChallengesPerSession: null,
    });
    await post(session, `/api/albums/${album.id}/party-challenges`, {
      title: 'Canta una canzone', body: 'Sali sul tavolo.', kind: 'dare',
      mediaFileItemId: null, isEnabled: true, votingMode: 'binary',
      voteQuestion: 'Ha superato la sfida?',
    });
    await post(session, `/api/albums/${album.id}/party-challenges`, {
      title: 'Brindisi finale', body: 'Alza il calice.', kind: 'custom',
      mediaFileItemId: null, isEnabled: true, votingMode: 'none',
    });

    const settings = await get<PartySettings>(session, `/api/albums/${album.id}/party-settings`);
    const token = settings.partyUrl!.replace('/party/', '');

    // --- The four surfaces --------------------------------------------------
    const contexts: BrowserContext[] = [];
    const open = async (viewport: { width: number; height: number }, path: string) => {
      const context = await browser.newContext({ viewport });
      contexts.push(context);
      const page = await context.newPage();
      await page.goto(new URL(path, WEB_URL).toString());
      return page;
    };

    try {
      const tv = await open(TV_VIEWPORT, `/party/${token}/tv`);
      const guestA = await open(PHONE_VIEWPORT, `/party/${token}/game`);
      const guestB = await open(PHONE_VIEWPORT, `/party/${token}/game`);

      // The television is a display before anything starts, and it shows the
      // way in rather than an empty screen.
      await expect(tv.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'lobby', { timeout: 20_000 });
      await expect(tv.getByTestId('party-stage-qr')).toBeVisible();
      await expect(guestA.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'lobby');

      await ownerPage.goto(`/albums/${album.id}/party-game`);
      await expect(ownerPage.getByTestId('party-control-room')).toBeVisible({ timeout: 20_000 });
      // The host can see the room: two phones are in it, and a screen is on.
      await expect(ownerPage.getByTestId('party-control-tv'))
        .toHaveAttribute('data-state', 'connected', { timeout: 20_000 });
      await expect(ownerPage.getByTestId('party-control-guests')).toHaveText('2', { timeout: 20_000 });

      // --- The first activity, to a vote ------------------------------------
      await advance(ownerPage, 'start_challenge');
      await expect(tv.getByTestId('party-stage-card')).toContainText('Canta una canzone', { timeout: 20_000 });
      await expect(guestA.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'watch', { timeout: 20_000 });

      await advance(ownerPage, 'open_voting');
      await advance(ownerPage, 'close_voting');

      // Both phones are asked the host's own question.
      await expect(tv.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'vote', { timeout: 20_000 });
      for (const guest of [guestA, guestB]) {
        await expect(guest.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'vote', { timeout: 20_000 });
        await expect(guest.getByRole('heading', { name: /Ha superato la sfida\?/i })).toBeVisible();
      }

      // One each way, then A changes their mind, then taps the same answer
      // again — three taps, one answer.
      await guestA.getByTestId('party-game-vote-yes').click();
      await expect(guestA.getByTestId('party-game-confirmed')).toBeVisible();
      await guestB.getByTestId('party-game-vote-no').click();
      await guestA.getByTestId('party-game-vote-no').click();
      await guestA.getByTestId('party-game-vote-no').click();
      await expect(guestA.getByTestId('party-game-vote-no')).toHaveAttribute('aria-pressed', 'true');

      // The television counts participation and never the split.
      await expect(tv.getByTestId('party-stage-tally')).toContainText('2', { timeout: 20_000 });
      await expect(tv.locator('body')).not.toContainText('%');

      // --- Closing, and who learns what -------------------------------------
      await advance(ownerPage, 'reveal_result');
      // The host is told; the room is not, until the host says so.
      await expect(ownerPage.getByTestId('party-control-split')).toContainText('0', { timeout: 20_000 });
      await expect(tv.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'closed', { timeout: 20_000 });
      await expect(tv.locator('body')).not.toContainText('%');
      await expect(guestA.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'waiting', { timeout: 20_000 });

      await advance(ownerPage, 'next_challenge');
      await expect(tv.getByTestId('party-stage-percent')).toHaveText('0%', { timeout: 20_000 });
      await expect(tv.getByTestId('party-stage-verdict')).toHaveAttribute('data-passed', 'false');
      await expect(guestA.getByTestId('party-game-percent')).toHaveText('0%', { timeout: 20_000 });

      // --- Everything recovers by reading again -----------------------------
      await tv.reload();
      await expect(tv.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'result', { timeout: 20_000 });
      await guestB.reload();
      await expect(guestB.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'result', { timeout: 20_000 });
      await ownerPage.reload();
      await expect(primary(ownerPage)).toHaveAttribute('data-command', 'next_challenge', { timeout: 20_000 });

      // --- The second activity is not voted on ------------------------------
      // Its primary action is the result, and the vote is ABSENT rather than
      // offered and refused.
      await advance(ownerPage, 'start_challenge');
      await expect(tv.getByTestId('party-stage-card')).toContainText('Brindisi finale', { timeout: 20_000 });
      await primary(ownerPage).click();
      await expect(primary(ownerPage)).toHaveAttribute('data-command', 'reveal_result', { timeout: 20_000 });
      await expect(guestA.getByTestId('party-game-vote-yes')).toHaveCount(0);

      // --- The host ends the evening ----------------------------------------
      await primary(ownerPage).click();
      await expect(ownerPage.getByTestId('party-control-finish')).toBeVisible({ timeout: 20_000 });
      await ownerPage.getByTestId('party-control-finish').click();
      await ownerPage.getByTestId('party-control-finish-confirm').click();

      await expect(ownerPage.getByTestId('party-control-status')).toContainText(/Concluso/i, { timeout: 20_000 });
      await expect(primary(ownerPage)).toHaveCount(0);
      await expect(tv.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'finished', { timeout: 20_000 });
      await expect(guestA.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'finished', { timeout: 20_000 });

      // The television never grew a control, at any point in the evening.
      await expect(tv.locator('button, a, input, select, textarea')).toHaveCount(0);
    } finally {
      for (const context of contexts) await context.close();
    }
  });
});
