import assert from 'node:assert/strict';
import test from 'node:test';
import {
  MIN_RENEW_DELAY_MS,
  MINT_RETRY_BASE_MS,
  MINT_RETRY_MAX_MS,
  NOT_ASSIGNED_RETRY_BASE_MS,
  RENDERER_PROTOCOL,
  RENEW_BEFORE_EXPIRY_MS,
  classifyMintFailure,
  mintRetryDelayMs,
  parseRendererMessage,
  renewDelayMs,
  stageUrl,
} from './partyDisplayGrant.ts';

// The display grant's lifecycle on the television, as arithmetic.

test('a mint failure is classified by where it leads, not by its number', () => {
  // The TELEVISION's session is gone: pairing.
  assert.equal(classifyMintFailure(401), 'session-invalid');
  // Paired, but nothing showable: fail closed and wait for the control plane.
  assert.equal(classifyMintFailure(404), 'not-assigned');
  // Everything else may work shortly — including no response at all. None of
  // these may ever become a permanent "unavailable".
  for (const status of [null, 408, 429, 500, 502, 503, 504]) {
    assert.equal(classifyMintFailure(status), 'transient', String(status));
  }
});

test('mint retries back off, are capped, and a 404 is asked less eagerly', () => {
  assert.equal(mintRetryDelayMs('transient', 0), MINT_RETRY_BASE_MS);
  assert.equal(mintRetryDelayMs('transient', 1), MINT_RETRY_BASE_MS * 2);
  assert.equal(mintRetryDelayMs('transient', 30), MINT_RETRY_MAX_MS);
  assert.equal(mintRetryDelayMs('not-assigned', 0), NOT_ASSIGNED_RETRY_BASE_MS);
  assert.ok(mintRetryDelayMs('not-assigned', 0) > mintRetryDelayMs('transient', 0));
  assert.equal(mintRetryDelayMs('not-assigned', 30), MINT_RETRY_MAX_MS);
  for (let attempt = 0; attempt < 40; attempt += 1) {
    assert.ok(mintRetryDelayMs('transient', attempt) <= MINT_RETRY_MAX_MS);
  }
});

test('renewal is scheduled from the server-measured lifetime, before it lapses', () => {
  const now = Date.parse('2026-09-12T21:00:00Z');
  const grant = { expiresAt: '2026-09-13T01:00:00Z', expiresInSeconds: 4 * 3_600 };
  // Four hours, renewed ten minutes early.
  assert.equal(renewDelayMs(grant, now), 4 * 3_600_000 - RENEW_BEFORE_EXPIRY_MS);

  // A television whose clock is an hour out — either way — renews at the SAME
  // moment, because the duration came from the server.
  assert.equal(renewDelayMs(grant, now + 3_600_000), renewDelayMs(grant, now));
  assert.equal(renewDelayMs(grant, now - 3_600_000), renewDelayMs(grant, now));
});

test('an older server falls back to the device clock, and nonsense cannot loop', () => {
  const now = Date.parse('2026-09-12T21:00:00Z');
  assert.equal(renewDelayMs({ expiresAt: '2026-09-13T01:00:00Z' }, now),
    4 * 3_600_000 - RENEW_BEFORE_EXPIRY_MS);
  // Already past (a badly wrong clock), unparseable, or absurdly short: never
  // a mint loop.
  assert.equal(renewDelayMs({ expiresAt: '2026-09-12T20:00:00Z' }, now), MIN_RENEW_DELAY_MS);
  assert.equal(renewDelayMs({ expiresAt: 'not a date' }, now), MIN_RENEW_DELAY_MS);
  assert.equal(renewDelayMs({ expiresAt: 'x', expiresInSeconds: 0 }, now), MIN_RENEW_DELAY_MS);
  // A short lifetime is renewed at three quarters of it.
  assert.equal(renewDelayMs({ expiresAt: 'x', expiresInSeconds: 20 * 60 }, now), 15 * 60_000);
});

test('the grant travels only in the fragment', () => {
  const url = stageUrl('https://tv.example', 'a+b/c=');
  assert.equal(url, 'https://tv.example/party-display/stage#grant=a%2Bb%2Fc%3D');
  // Nothing before the fragment carries it: no query string at all.
  assert.equal(url.split('#')[0].includes('grant'), false);
  assert.equal(url.includes('?'), false);
});

test('the bridge is parsed strictly, and anything else is silence', () => {
  assert.equal(RENDERER_PROTOCOL, 2);
  assert.deepEqual(parseRendererMessage('{"type":"display-heartbeat","protocol":2,"at":1}'),
    { kind: 'heartbeat', protocol: 2 });
  // A page from before the ready signal announces no protocol: it is protocol 1.
  assert.deepEqual(parseRendererMessage('{"type":"display-heartbeat","at":1}'),
    { kind: 'heartbeat', protocol: 1 });
  assert.deepEqual(parseRendererMessage('{"type":"display-ready"}'), { kind: 'ready' });
  assert.deepEqual(parseRendererMessage('{"type":"display-auth-failed"}'), { kind: 'auth-failed' });
  assert.deepEqual(parseRendererMessage('{"type":"display-presentation","active":true}'),
    { kind: 'presentation', active: true });
  assert.deepEqual(parseRendererMessage('{"type":"display-presentation","active":false}'),
    { kind: 'presentation', active: false });

  for (const raw of [
    '', 'not json', 'null', '42', '"display-ready"', '{}',
    '{"type":"display-presentation"}', '{"type":"display-presentation","active":"yes"}',
    // The page does not get to tell the shell about phases.
    '{"type":"voting_open"}', '{"type":"navigate","url":"https://elsewhere"}',
  ]) {
    assert.equal(parseRendererMessage(raw), null, raw);
  }
});
