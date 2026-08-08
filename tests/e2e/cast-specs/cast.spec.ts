// Google Cast in a real browser.
//
// SCOPE, stated up front, because this file deliberately does NOT cover the
// whole feature and a reader should not have to infer that.
//
// What a browser adds here, and what these tests assert:
//   * the grant endpoint reached through nginx as a real authenticated user;
//   * a COOKIE-LESS fetch of the resulting bearer URL — exactly what a
//     television does, and the one assertion no unit test can fake;
//   * real HTTP Range delivery of that URL through the front door;
//   * the CORS boundary, including a real preflight;
//   * that NubArca REFUSES to offer casting from this origin, and says why.
//
// What is NOT here, and why. The sender flow — launcher, session, MediaInfo,
// position handoff, receiver mirroring, mini controller, stop-and-revoke — needs
// a secure origin that a receiver could also reach. The ephemeral stack is http
// on loopback: Chromium calls that secure, NubArca correctly calls it
// unreachable, and `--unsafely-treat-insecure-origin-as-secure` does not move
// `isSecureContext` in Playwright's Chromium (measured, not assumed). Giving the
// stack TLS to satisfy one spec would put a certificate, a second port and
// forwarded-header trust into the shared release gate for every other spec to
// trip over. So that half is covered at DOM level in
// `frontend/src/cast/CastProvider.test.tsx` against the same framework double,
// and the definitive proof is the physical-device acceptance in
// `docs/google-cast.md`.
//
// The refusal assertion is not a consolation prize: "Cast must not pretend to be
// operational on an origin a television cannot resolve" is a product
// requirement, and a regression in it would ship a button that always fails.

import { request as playwrightRequest } from '@playwright/test';
import { expect, test } from '../src/fixtures';
import { CAST_RECEIVER_ORIGIN, WEB_URL } from '../src/env';

/** The seeded video, by the display name the seeder gives it. */
const VIDEO = 'e2e-video-unassigned.mp4';

/** Mint a grant through the real API, as the signed-in owner's browser would. */
async function mintGrant(page: import('@playwright/test').Page): Promise<{
  grantId: string; contentPath: string; posterPath: string; contentType: string; mode: string;
}> {
  const videoId = await page.evaluate(async () => {
    const response = await fetch('/api/media?kind=video&limit=1', { credentials: 'include' });
    const body = await response.json() as { items: Array<{ id: string }> };
    return body.items[0]?.id ?? null;
  });
  expect(videoId, 'the seeded library has a video').not.toBeNull();

  const grant = await page.evaluate(async (id: string) => {
    const response = await fetch(`/api/cast/videos/${id}/grant`, {
      method: 'POST', credentials: 'include',
    });
    if (response.status !== 201) return { error: response.status };
    return await response.json();
  }, videoId!);

  expect(grant, `grant creation: ${JSON.stringify(grant)}`).not.toHaveProperty('error');
  return grant as Awaited<ReturnType<typeof mintGrant>>;
}

test.describe('Google Cast', () => {
  test('a bearer media URL plays for a receiver that holds no NubArca cookie', async ({
    ownerPage, health,
  }) => {
    void health;
    await ownerPage.goto('/media');
    const grant = await mintGrant(ownerPage);

    // The ephemeral stack runs the progressive contract (no HLS provider), so
    // this also covers Range delivery through nginx.
    expect(grant.mode).toBe('direct');
    expect(grant.contentType).toBe('video/mp4');
    expect(grant.contentPath).toContain('token=');
    // The receiver is never pointed at an owner endpoint it has no cookie for.
    expect(grant.contentPath).not.toContain('/api/files/');
    expect(grant.contentPath.startsWith(`/api/cast/media/${grant.grantId}/`)).toBe(true);

    // A context with no cookie jar of its own: the television's position.
    const receiver = await playwrightRequest.newContext();
    try {
      const whole = await receiver.get(`${WEB_URL}${grant.contentPath}`);
      expect(whole.status()).toBe(200);
      expect(whole.headers()['content-type']).toContain('video/mp4');
      // A playback URL, never a download.
      expect(whole.headers()['content-disposition']).toBeUndefined();
      // A capability that can be withdrawn must not be stored by an intermediary.
      expect(whole.headers()['cache-control']).toContain('no-store');

      // Seeking needs real Range semantics.
      const ranged = await receiver.get(
        `${WEB_URL}${grant.contentPath}`, { headers: { Range: 'bytes=0-99' } });
      expect(ranged.status()).toBe(206);
      expect(ranged.headers()['accept-ranges']).toBe('bytes');
      expect(ranged.headers()['content-range']).toMatch(/^bytes 0-99\/\d+$/);
      expect((await ranged.body()).length).toBe(100);

      // The poster is reachable on the same terms and nothing else is.
      expect((await receiver.get(`${WEB_URL}${grant.posterPath}`)).status()).toBe(200);
      const noToken = grant.contentPath.split('?')[0];
      expect((await receiver.get(`${WEB_URL}${noToken}`)).status()).toBe(404);
      expect((await receiver.get(`${WEB_URL}${noToken}?token=wrong`)).status()).toBe(404);
    } finally {
      await receiver.dispose();
    }
  });

  test('revoking a grant stops playback for everybody, immediately', async ({
    ownerPage, health,
  }) => {
    void health;
    await ownerPage.goto('/media');
    const grant = await mintGrant(ownerPage);

    const receiver = await playwrightRequest.newContext();
    try {
      expect((await receiver.get(`${WEB_URL}${grant.contentPath}`)).status()).toBe(200);

      const status = await ownerPage.evaluate(async (id: string) => {
        const response = await fetch(`/api/cast/grants/${id}`, {
          method: 'DELETE', credentials: 'include',
        });
        return response.status;
      }, grant.grantId);
      expect(status).toBe(204);

      // The very next request, not the next session.
      expect((await receiver.get(`${WEB_URL}${grant.contentPath}`)).status()).toBe(404);
    } finally {
      await receiver.dispose();
    }
  });

  test('the receiver origin allowlist is exact, and reaches nothing else', async ({
    ownerPage, health,
  }) => {
    void health;
    await ownerPage.goto('/media');
    const grant = await mintGrant(ownerPage);

    const receiver = await playwrightRequest.newContext();
    try {
      const allowed = await receiver.fetch(`${WEB_URL}${grant.contentPath}`, {
        method: 'OPTIONS',
        headers: {
          Origin: CAST_RECEIVER_ORIGIN,
          'Access-Control-Request-Method': 'GET',
          'Access-Control-Request-Headers': 'range',
        },
      });
      expect(allowed.headers()['access-control-allow-origin']).toBe(CAST_RECEIVER_ORIGIN);
      expect(allowed.headers()['access-control-allow-methods']).toContain('GET');
      expect(allowed.headers()['access-control-allow-headers']?.toLowerCase()).toContain('range');

      // A player that cannot READ these cannot seek.
      const actual = await receiver.get(
        `${WEB_URL}${grant.contentPath}`, { headers: { Origin: CAST_RECEIVER_ORIGIN } });
      const exposed = actual.headers()['access-control-expose-headers']?.toLowerCase() ?? '';
      for (const header of ['content-type', 'content-length', 'content-range', 'accept-ranges']) {
        expect(exposed).toContain(header);
      }

      // An unlisted origin gets nothing — never a wildcard, on a URL that
      // carries a bearer secret.
      const foreign = await receiver.fetch(`${WEB_URL}${grant.contentPath}`, {
        method: 'OPTIONS',
        headers: {
          Origin: 'https://attacker.nubarca.test',
          'Access-Control-Request-Method': 'GET',
        },
      });
      expect(foreign.headers()['access-control-allow-origin']).toBeUndefined();

      // And CORS is attached to the Cast media family and to nothing else.
      const elsewhere = await receiver.get(
        `${WEB_URL}/api/auth/me`, { headers: { Origin: CAST_RECEIVER_ORIGIN } });
      expect(elsewhere.headers()['access-control-allow-origin']).toBeUndefined();
    } finally {
      await receiver.dispose();
    }
  });

  // The four explanations NubArca is willing to give for not offering Cast.
  // Asserting membership of this set — rather than one of them — is what keeps
  // the test honest across environments: a headless CI browser has no
  // `chrome.cast` bridge at all, while a headed browser on this loopback stack
  // gets as far as the reachability rule. Both are correct refusals; a fifth,
  // unexplained state would not be, and would fail here.
  const KNOWN_REFUSALS = [
    /Questo browser non può trasmettere/,       // no Cast bridge (headless, Firefox, iOS)
    /richiede una connessione HTTPS/,            // insecure origin
    /non può raggiungere questo indirizzo/,      // loopback: no TV can resolve it
    /Impossibile caricare il componente Google Cast/, // SDK load failure
  ];

  test('the viewer refuses to offer casting when it would not work, and says why', async ({
    ownerPage, health,
  }) => {
    void health;

    // Nothing may be fetched from Google when Cast is not on offer.
    const googleRequests: string[] = [];
    ownerPage.on('request', (request) => {
      if (request.url().includes('gstatic.com')) googleRequests.push(request.url());
    });

    await ownerPage.goto('/media');
    const tile = ownerPage
      .getByTestId('media-grid')
      .locator('[data-kind]')
      .filter({ has: ownerPage.locator(`[data-testid="media-open"][aria-label*="${VIDEO}" i]`) })
      .first();
    await expect(tile).toBeVisible({ timeout: 20_000 });
    await tile.getByTestId('media-open').first().click();
    await expect(ownerPage.getByTestId('media-viewer-title')).toBeVisible({ timeout: 20_000 });

    // The account HOLDS cast.access — it is a Member — so an ENVIRONMENT gate is
    // what is talking here, not the permission gate. (Without the permission the
    // control is absent entirely; that case is covered in the vitest suite.)
    // Present and disabled with a reason, rather than absent: a control that
    // silently vanishes teaches the user nothing about why their television is
    // not an option.
    const disabled = ownerPage.getByTestId('cast-unavailable');
    await expect(disabled).toBeVisible();
    await expect(disabled).toBeDisabled();

    const reason = await disabled.getAttribute('title');
    expect(reason, 'a refusal must carry an explanation').toBeTruthy();
    expect(
      KNOWN_REFUSALS.some((pattern) => pattern.test(reason!)),
      `unrecognised Cast refusal: "${reason}"`,
    ).toBe(true);

    // No launcher, no session, and no mini controller anywhere in the shell.
    await expect(ownerPage.getByTestId('cast-launcher')).toHaveCount(0);
    await expect(ownerPage.getByTestId('cast-mini-controller')).toHaveCount(0);
    expect(googleRequests, 'no Cast SDK is fetched when casting is not offered').toEqual([]);
  });
});
