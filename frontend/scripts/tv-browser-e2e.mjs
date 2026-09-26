// A BROWSER RUNNING /tv IS A NUBARCA DISPLAY — proved end to end.
//
// This drives a real Chromium against a real NubArca (API + database + the
// built frontend behind the same origin) through the whole life of a display:
//
//   open /tv → pair (the owner approves over the API) → assign to a party →
//   the slideshow → a game takes the screen → the game hands it back →
//   reload: still paired, straight back into the party, never the mode
//   selector → the owner revokes it → pairing again.
//
// The owner side is plain HTTP with a session cookie; the display side is the
// browser, read through the DevTools protocol — what is asserted is what the
// room would see (the display's own `data-flow` and its surfaces), not an
// internal state. No new dependency: Node's WebSocket and Chromium's CDP.
//
//   APP=http://localhost:4173 NUBARCA_E2E_EMAIL=… NUBARCA_E2E_PASSWORD=… \
//     node scripts/tv-browser-e2e.mjs
//
// `scripts/tv-browser-e2e.sh` at the repository root brings the stack up
// (a throwaway PostgreSQL, the API, the owner, the built frontend) and runs it.

import { spawn, execFileSync } from 'node:child_process';
import { existsSync, readdirSync, mkdtempSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { deflateSync } from 'node:zlib';

const APP = (process.env.APP ?? 'http://localhost:4173').replace(/\/$/, '');
const EMAIL = process.env.NUBARCA_E2E_EMAIL;
const PASSWORD = process.env.NUBARCA_E2E_PASSWORD;
const PERSONAL_CODE = 'URDLSUDLR';
if (!EMAIL || !PASSWORD) {
  console.error('NUBARCA_E2E_EMAIL and NUBARCA_E2E_PASSWORD are required.');
  process.exit(2);
}

const steps = [];
function step(name) {
  steps.push(name);
  console.log(`▶ ${name}`);
}

// E2E_SHOTS=<dir> keeps a picture of what the display showed at each step.
const SHOTS = process.env.E2E_SHOTS ?? null;
async function snapshot(name) {
  if (!SHOTS) return;
  const { mkdirSync, writeFileSync } = await import('node:fs');
  mkdirSync(SHOTS, { recursive: true });
  const shot = await send('Page.captureScreenshot', { format: 'png' }, sessionId);
  writeFileSync(join(SHOTS, `${String(steps.length).padStart(2, '0')}-${name}.png`), Buffer.from(shot.data, 'base64'));
}

// ---- The owner, over HTTP --------------------------------------------------

const cookies = new Map();
async function owner(path, { method = 'GET', json, form, headers = {} } = {}) {
  const init = { method, headers: { ...headers }, redirect: 'manual' };
  if (json !== undefined) {
    init.headers['content-type'] = 'application/json';
    init.body = JSON.stringify(json);
  } else if (form !== undefined) {
    init.body = form;
  }
  if (cookies.size) init.headers.cookie = [...cookies].map(([k, v]) => `${k}=${v}`).join('; ');
  const response = await fetch(`${APP}${path}`, init);
  for (const line of response.headers.getSetCookie?.() ?? []) {
    const [pair] = line.split(';');
    const index = pair.indexOf('=');
    cookies.set(pair.slice(0, index).trim(), pair.slice(index + 1).trim());
  }
  const text = await response.text();
  const body = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : null;
  if (!response.ok) throw new Error(`${method} ${path} → ${response.status} ${typeof body === 'string' ? body : JSON.stringify(body)}`);
  return body;
}

async function partyId(albumId) {
  return (await owner(`/api/albums/${albumId}/party-settings`)).partyId;
}

async function transition(albumId, action) {
  const id = await partyId(albumId);
  const party = await owner(`/api/parties/${id}`);
  await owner(`/api/parties/${id}/${action}`, { method: 'POST', json: { version: party.version } });
}

async function setGame(albumId, enabled) {
  await owner(`/api/albums/${albumId}/party-game-settings`, {
    method: 'PATCH',
    json: {
      gameEnabled: enabled, minChallengeIntervalSeconds: 30, maxChallengeIntervalSeconds: 60,
      votesPerGuest: 3, maxChallengesPerSession: null, priorityVotingEnabled: true,
    },
  });
}

// A real picture, so the wall has something to show: a 64×40 PNG, made here.
function tinyPng() {
  const header = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const sum = Buffer.alloc(4);
    sum.writeUInt32BE(crc(body));
    return Buffer.concat([len, body, sum]);
  };
  const width = 64;
  const height = 40;
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y += 1) {
    raw[y * (width * 3 + 1)] = 0;
    for (let x = 0; x < width; x += 1) {
      const o = y * (width * 3 + 1) + 1 + x * 3;
      raw[o] = 20 + x * 3; raw[o + 1] = 90 + y * 3; raw[o + 2] = 200;
    }
  }
  return Buffer.concat([
    header, chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---- The display, in Chromium ---------------------------------------------

function findChrome() {
  if (process.env.CHROME_BIN && existsSync(process.env.CHROME_BIN)) return process.env.CHROME_BIN;
  const cache = join(homedir(), '.cache', 'ms-playwright');
  if (existsSync(cache)) {
    for (const build of readdirSync(cache).filter((d) => d.startsWith('chromium-')).sort().reverse()) {
      for (const sub of ['chrome-linux64', 'chrome-linux']) {
        const candidate = join(cache, build, sub, 'chrome');
        if (existsSync(candidate)) return candidate;
      }
    }
  }
  for (const name of ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser']) {
    try { return execFileSync('which', [name], { encoding: 'utf8' }).trim(); } catch { /* next */ }
  }
  throw new Error('No Chromium found: set CHROME_BIN.');
}

const port = 9300 + (process.pid % 500);
const profile = mkdtempSync(join(tmpdir(), 'nubarca-tv-e2e-'));
const chrome = spawn(findChrome(), [
  '--headless=new', '--disable-gpu', '--no-sandbox', '--no-first-run',
  '--autoplay-policy=no-user-gesture-required',
  `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`,
  '--window-size=1280,720', 'about:blank',
], { stdio: 'ignore' });

async function devtools() {
  for (let i = 0; i < 150; i += 1) {
    try {
      return (await (await fetch(`http://127.0.0.1:${port}/json/version`)).json()).webSocketDebuggerUrl;
    } catch { await delay(100); }
  }
  throw new Error('Chromium did not start');
}

const ws = new WebSocket(await devtools());
await new Promise((resolve) => { ws.onopen = resolve; });
let nextId = 0;
const pending = new Map();
const listeners = [];
ws.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  } else if (message.method) {
    for (const listener of listeners) listener(message);
  }
};
function send(method, params = {}, sessionId) {
  const id = ++nextId;
  return new Promise((resolve, reject) => {
    pending.set(id, (m) => (m.error ? reject(new Error(`${method}: ${m.error.message}`)) : resolve(m.result)));
    ws.send(JSON.stringify({ id, method, params, sessionId }));
  });
}

const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
await send('Page.enable', {}, sessionId);
await send('Runtime.enable', {}, sessionId);
await send('Network.enable', {}, sessionId);
// Every screen the display draws, recorded from the first frame of every load:
// it is how "never the mode selector" is checked, not by sampling.
await send('Page.addScriptToEvaluateOnNewDocument', {
  source: `
    window.__flows = [];
    new MutationObserver(() => {
      const f = document.querySelector('[data-testid="tv-display"]')?.getAttribute('data-flow');
      if (f && window.__flows[window.__flows.length - 1] !== f) window.__flows.push(f);
    }).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-flow'] });
  `,
}, sessionId);

// The pairing secret travels in the start response, as it does to a real TV;
// the test reads it there, exactly where a phone's QR code carries it from.
let pairingSecret = null;
const startRequests = new Set();
listeners.push(async (message) => {
  if (message.sessionId !== sessionId) return;
  if (message.method === 'Network.responseReceived'
    && message.params.response.url.endsWith('/api/tv/pairing/start')) {
    startRequests.add(message.params.requestId);
  }
  if (message.method === 'Network.loadingFinished' && startRequests.has(message.params.requestId)) {
    const { body } = await send('Network.getResponseBody', { requestId: message.params.requestId }, sessionId);
    pairingSecret = JSON.parse(body).pairingSecret;
  }
});

async function evaluate(expression) {
  const result = await send('Runtime.evaluate', { expression, returnByValue: true }, sessionId);
  return result.result?.value;
}

async function waitFor(description, expression, timeoutMs = 20_000) {
  const until = Date.now() + timeoutMs;
  for (;;) {
    const value = await evaluate(expression).catch(() => null);
    if (value) return value;
    if (Date.now() > until) {
      const flow = await evaluate(`document.querySelector('[data-testid="tv-display"]')?.getAttribute('data-flow')`).catch(() => null);
      throw new Error(`Timed out waiting for ${description} (display is on "${flow}")`);
    }
    await delay(250);
  }
}

const flowIs = (name) => `document.querySelector('[data-testid="tv-display"]')?.getAttribute('data-flow') === ${JSON.stringify(name)}`;
const has = (testId) => `!!document.querySelector('[data-testid="${testId}"]')`;

let exitCode = 0;
try {
  step('the owner signs in');
  await owner('/api/auth/login', { method: 'POST', json: { email: EMAIL, password: PASSWORD } });

  step('the owner prepares a live party with a photograph');
  const album = await owner('/api/albums', { method: 'POST', json: { name: `Festa E2E ${Date.now()}` } });
  const form = new FormData();
  form.append('file', new Blob([tinyPng()], { type: 'image/png' }), 'festa.png');
  const file = await owner('/api/files', { method: 'POST', form });
  await owner(`/api/albums/${album.id}/items`, { method: 'POST', json: { fileItemId: file.id } });
  await owner(`/api/albums/${album.id}/party-settings`, { method: 'PATCH', json: { enabled: true } });
  await transition(album.id, 'start-live');
  for (const title of ['Uno', 'Due']) {
    await owner(`/api/albums/${album.id}/party-challenges`, {
      method: 'POST',
      json: { title, body: 'Descrizione', kind: 'dare', mediaFileItemId: null, isEnabled: true, votingMode: 'binary' },
    });
  }

  step('a browser opens /tv and offers a pairing code');
  await send('Page.navigate', { url: `${APP}/tv` }, sessionId);
  await waitFor('the pairing code', has('tv-pairing-code'));
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('pairing');
  const publicCode = (await evaluate(`document.querySelector('[data-testid="tv-pairing-code"]').textContent`)).trim();
  for (let i = 0; i < 40 && !pairingSecret; i += 1) await delay(100);
  if (!pairingSecret) throw new Error('The pairing start response was not seen');

  step('the owner approves it from their phone');
  // An owner without a Personal Area code creates it in the same approval; one
  // who has one already sees these fields ignored.
  await owner(`/api/tv/pairing/${publicCode}/approve`, {
    method: 'POST',
    json: { pairingSecret, personalCode: PERSONAL_CODE, personalCodeConfirmation: PERSONAL_CODE },
  });
  await waitFor('the paired display', flowIs('mode'));
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('mode');

  step('it is an ordinary television in the owner’s TV Devices');
  const device = await owner(`/api/tv/pairing/${publicCode}/device`, {
    headers: { 'X-Tv-Pairing-Secret': pairingSecret },
  });
  const devices = await owner('/api/tv-devices');
  if (!devices.some((d) => d.id === device.sessionId && d.status === 'active')) {
    throw new Error('The paired browser is not listed as an active TV device');
  }

  step('the owner assigns it to the party: the slideshow takes the screen');
  await owner(`/api/tv-devices/${device.sessionId}/assignment`, {
    method: 'PATCH', json: { kind: 'party', albumId: album.id },
  });
  await waitFor('the party slideshow', `${flowIs('partySlideshow')} && ${has('tv-party-slideshow')}`);
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('slideshow');

  step('the game takes the screen');
  await setGame(album.id, true);
  await waitFor('the game stage', `${flowIs('partyGame')} && ${has('party-tv-stage')}`);
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('game');

  step('the game hands the screen back');
  await setGame(album.id, false);
  await waitFor('the slideshow again', `${flowIs('partySlideshow')} && ${has('tv-party-slideshow')}`);
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('slideshow-again');

  step('a reload keeps the pairing and goes straight back into the party');
  await send('Page.reload', { ignoreCache: true }, sessionId);
  await waitFor('the slideshow after reload', `${flowIs('partySlideshow')} && ${has('tv-party-slideshow')}`);
  const flows = await evaluate('window.__flows');
  if (flows.includes('mode') || flows.includes('pairing')) {
    throw new Error(`After the reload the display passed through: ${flows.join(' → ')}`);
  }

  step('the owner revokes it: pairing again, nothing of the party left');
  await owner(`/api/tv-devices/${device.sessionId}`, { method: 'DELETE' });
  await waitFor('the revoked pairing screen', `${flowIs('pairing')} && ${has('tv-revoked')} && ${has('tv-pairing-code')}`);
  await delay(SHOTS ? 1_500 : 0);
  await snapshot('revoked');
  if (await evaluate(`${has('tv-party-slideshow')} || !!document.querySelector('video')`)) {
    throw new Error('Party content survived the revocation');
  }

  console.log(`\n✓ A browser running /tv is a NubArca display (${steps.length} steps).`);
} catch (error) {
  exitCode = 1;
  console.error(`\n✗ ${error instanceof Error ? error.message : String(error)}`);
  try {
    const shot = await send('Page.captureScreenshot', { format: 'png' }, sessionId);
    const path = join(profile, 'failure.png');
    (await import('node:fs')).writeFileSync(path, Buffer.from(shot.data, 'base64'));
    console.error(`  screenshot: ${path}`);
    console.error(`  diagnostics: ${JSON.stringify(await evaluate('window.__nubarcaTvDiagnostics?.()'))}`);
  } catch { /* the browser may be gone */ }
} finally {
  try { ws.close(); } catch { /* closing */ }
  chrome.kill('SIGKILL');
}
process.exit(exitCode);
