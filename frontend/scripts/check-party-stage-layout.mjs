#!/usr/bin/env node
// The party stage LOBBY, measured in a real browser.
//
// jsdom has no layout engine, so the unit suite proves the lobby's structure
// and its CSS contract (src/party/PartyTvStageLobby.test.tsx). This script
// proves the LAYOUT those produce: it renders the lobby exactly as
// PartyTvStage emits it — the real global stylesheet, the real fonts, the real
// PartyTvStage.css, a real QR code with its quiet zone — in headless Chromium at
// the television viewports that matter, and asserts on bounding boxes:
//
//   * every element sits inside the overscan safe area, nothing clips, nothing
//     scrolls;
//   * the words start at the TOP of the safe area, at exactly the approved
//     sizes;
//   * the code is square, fills the height the words leave (or the safe width,
//     whichever is smaller), and is far larger than the 22vh it used to be.
//
// It is a dev/QA tool, not a CI job: it needs a Chromium binary. It looks for
// CHROME_BIN, then Playwright's cache, then the usual system names.
//
//   node scripts/check-party-stage-layout.mjs [--screenshots <dir>]

import { execFileSync } from 'node:child_process';
import {
  existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync,
} from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import QRCode from 'qrcode';

const here = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(here, '..');

// 960x540 is a 1080p Fire TV panel at devicePixelRatio 2 — the logical viewport
// the stage actually gets inside the NubArca TV WebView. The others are the
// panels a browser or projector route meets, the smallest 16:9 the stage is
// designed for, and one deliberately SHORT viewport to prove it is the code,
// not the words, that gives way.
const VIEWPORTS = [
  { width: 960, height: 540, label: 'Fire TV 1080p @2x' },
  { width: 1280, height: 720, label: '720p' },
  { width: 1920, height: 1080, label: '1080p' },
  { width: 854, height: 480, label: '480p 16:9' },
  { width: 640, height: 360, label: 'smallest 16:9' },
  { width: 960, height: 400, label: 'short (not 16:9)' },
];

function findChrome() {
  if (process.env.CHROME_BIN && existsSync(process.env.CHROME_BIN)) return process.env.CHROME_BIN;
  const cache = join(homedir(), '.cache', 'ms-playwright');
  if (existsSync(cache)) {
    for (const dir of readdirSync(cache).sort().reverse()) {
      for (const rel of [
        'chrome-linux/headless_shell',
        'chrome-headless-shell-linux64/chrome-headless-shell',
        'chrome-linux/chrome',
        'chrome-linux64/chrome',
      ]) {
        const candidate = join(cache, dir, rel);
        if (existsSync(candidate)) return candidate;
      }
    }
  }
  for (const name of ['chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable']) {
    try {
      return execFileSync('which', [name], { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim();
    } catch { /* not installed */ }
  }
  return null;
}

function i18n(file, key) {
  const source = readFileSync(join(frontend, 'src', 'i18n', file), 'utf8');
  const match = new RegExp(`'${key.replace('.', '\\.')}':\\s*(['"])((?:\\\\.|(?!\\1).)*)\\1`).exec(source);
  if (!match) throw new Error(`no ${key} in ${file}`);
  return match[2].replace(/\\(['"])/g, '$1');
}

// The longer of the two languages, so the words take as much room as they ever do.
const longest = (key) => [i18n('it.ts', key), i18n('en.ts', key)]
  .sort((a, b) => b.length - a.length)[0];

const escapeHtml = (value) => value
  .replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

function fontStylesheets() {
  const main = readFileSync(join(frontend, 'src', 'main.tsx'), 'utf8');
  return [...main.matchAll(/import '(@fontsource\/[^']+\.css)';/g)]
    .map((m) => join(frontend, 'node_modules', m[1]))
    .filter((path) => existsSync(path));
}

async function harness() {
  // A realistic join URL: the same length as a real party token, so the code has
  // the module count a real lobby shows. Four modules of quiet zone, as both
  // routes now draw it.
  const svg = await QRCode.toString(
    `https://nubarca.example.org/party/${'Q'.repeat(43)}/game`,
    { type: 'svg', margin: 4, width: 260 });
  const links = [
    join(frontend, 'src', 'styles.css'),
    ...fontStylesheets(),
    join(frontend, 'src', 'party', 'PartyTvStage.css'),
  ].map((path) => `<link rel="stylesheet" href="${pathToFileURL(path).href}">`).join('\n');

  // The lobby markup, exactly as PartyTvStage renders it (the structure is
  // pinned by PartyTvStageLobby.test.tsx).
  return `<!doctype html>
<html lang="it"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
${links}
</head><body>
<div id="root"><main class="party-stage" data-scene="lobby" data-testid="party-tv-stage">
  <div class="party-stage-lobby" data-testid="party-stage-lobby">
    <p class="party-stage-eyebrow">NubArca · Festa di compleanno di Anna</p>
    <h1 class="party-stage-headline">${escapeHtml(longest('partyStage.lobbyTitle'))}</h1>
    <p class="party-stage-sub">${escapeHtml(longest('partyStage.lobbyBody'))}</p>
    <div class="party-stage-qr" data-testid="party-stage-qr" aria-hidden="true">${svg}</div>
  </div>
</main></div>
<script>
(async () => {
  await document.fonts.ready;
  const box = (el) => { const b = el.getBoundingClientRect();
    return { left: b.left, top: b.top, right: b.right, bottom: b.bottom, width: b.width, height: b.height }; };
  const stage = document.querySelector('.party-stage');
  const cs = getComputedStyle(stage);
  const s = box(stage);
  const safe = {
    left: s.left + parseFloat(cs.paddingLeft), top: s.top + parseFloat(cs.paddingTop),
    right: s.right - parseFloat(cs.paddingRight), bottom: s.bottom - parseFloat(cs.paddingBottom),
  };
  const q = (sel) => document.querySelector(sel);
  const region = q('.party-stage-qr');
  const picture = box(region.querySelector('svg'));
  // The SVG scales with its viewBox and the default xMidYMid meet: the drawn
  // code is the largest centred square inside the element's box.
  const side = Math.min(picture.width, picture.height);
  const code = {
    left: picture.left + (picture.width - side) / 2, top: picture.top + (picture.height - side) / 2,
    width: side, height: side,
  };
  code.right = code.left + side; code.bottom = code.top + side;
  const size = (el) => parseFloat(getComputedStyle(el).fontSize);
  const result = {
    viewport: { width: innerWidth, height: innerHeight },
    rootFontSize: parseFloat(getComputedStyle(document.documentElement).fontSize),
    safe,
    eyebrow: box(q('.party-stage-eyebrow')), headline: box(q('.party-stage-headline')),
    sub: box(q('.party-stage-sub')), region: box(region), code,
    fontSizes: { eyebrow: size(q('.party-stage-eyebrow')), headline: size(q('.party-stage-headline')),
      sub: size(q('.party-stage-sub')) },
    scroll: { width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight },
    fonts: [...document.fonts].filter((f) => f.status === 'loaded').map((f) => f.family),
  };
  document.documentElement.dataset.layout = btoa(unescape(encodeURIComponent(JSON.stringify(result))));
})();
</script>
</body></html>`;
}

const clamp = (min, value, max) => Math.min(max, Math.max(min, value));

function check(result) {
  const failures = [];
  const eps = 0.75;
  const { safe, viewport } = result;
  const rem = result.rootFontSize;
  const inside = (name, b) => {
    if (b.left < safe.left - eps || b.top < safe.top - eps
      || b.right > safe.right + eps || b.bottom > safe.bottom + eps) {
      failures.push(`${name} leaves the safe area: ${JSON.stringify(b)} vs ${JSON.stringify(safe)}`);
    }
  };
  for (const name of ['eyebrow', 'headline', 'sub', 'region', 'code']) inside(name, result[name]);

  if (result.scroll.width > viewport.width || result.scroll.height > viewport.height) {
    failures.push(`the page scrolls: ${JSON.stringify(result.scroll)}`);
  }
  // From the top: the eyebrow starts where the safe area does.
  if (Math.abs(result.eyebrow.top - safe.top) > 2) {
    failures.push(`the words do not start at the top (${result.eyebrow.top} vs ${safe.top})`);
  }
  // Words, then the code — never overlapping.
  if (result.eyebrow.bottom > result.headline.top + eps || result.headline.bottom > result.sub.top + eps
    || result.sub.bottom > result.code.top + eps) {
    failures.push('the words and the code overlap or are out of order');
  }
  // The approved sizes, unchanged: the CSS clamp() values, evaluated here.
  const h = viewport.height;
  const expected = {
    eyebrow: clamp(1 * rem, 0.024 * h, 2.2 * rem),
    headline: clamp(2 * rem, 0.08 * h, 7 * rem),
    sub: clamp(1.1 * rem, 0.032 * h, 2.6 * rem),
  };
  for (const [name, px] of Object.entries(expected)) {
    if (Math.abs(result.fontSizes[name] - px) > 0.05) {
      failures.push(`${name} is ${result.fontSizes[name]}px, expected ${px.toFixed(2)}px`);
    }
  }
  // Square, and as large as the room the words leave allows.
  const room = Math.min(result.region.width, result.region.height);
  if (Math.abs(result.code.width - room) > 1) {
    failures.push(`the code (${result.code.width}px) does not fill its room (${room}px)`);
  }
  const before = clamp(8 * rem, 0.22 * h, 18 * rem);
  const is169 = Math.abs(viewport.width / viewport.height - 16 / 9) < 0.01;
  const factor = is169 && viewport.height >= 540 ? 2 : 1.3;
  if (result.code.width < before * factor) {
    failures.push(`the code is ${result.code.width.toFixed(0)}px, not ${factor}x its old ${before.toFixed(0)}px`);
  }
  return { failures, before };
}

async function main() {
  const chrome = findChrome();
  if (!chrome) {
    console.error('check-party-stage-layout: no Chromium found (set CHROME_BIN).');
    process.exit(2);
  }
  const shotsIndex = process.argv.indexOf('--screenshots');
  const shots = shotsIndex > 0 ? resolve(process.argv[shotsIndex + 1]) : null;
  if (shots) mkdirSync(shots, { recursive: true });

  const work = mkdtempSync(join(tmpdir(), 'nubarca-stage-layout-'));
  const page = join(work, 'lobby.html');
  writeFileSync(page, await harness());
  const common = ['--headless', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
    '--force-device-scale-factor=1', '--allow-file-access-from-files',
    '--virtual-time-budget=5000'];

  let failed = false;
  console.log(`chromium: ${chrome}`);
  for (const vp of VIEWPORTS) {
    const size = `--window-size=${vp.width},${vp.height}`;
    const dom = execFileSync(chrome, [...common, size, '--dump-dom', pathToFileURL(page).href],
      { stdio: ['ignore', 'pipe', 'ignore'], maxBuffer: 16 * 1024 * 1024 }).toString();
    const encoded = /data-layout="([^"]+)"/.exec(dom)?.[1];
    if (!encoded) {
      console.error(`${vp.width}x${vp.height}: the page produced no measurement`);
      failed = true;
      continue;
    }
    const result = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8'));
    const { failures, before } = check(result);
    const safeH = result.safe.bottom - result.safe.top;
    console.log(
      `${failures.length ? 'FAIL' : 'ok  '} ${String(vp.width).padStart(4)}x${String(vp.height).padEnd(4)} `
      + `${vp.label.padEnd(18)} code ${result.code.width.toFixed(0)}px `
      + `(was ${before.toFixed(0)}px, ${(result.code.width / before).toFixed(1)}x; `
      + `${((result.code.width / safeH) * 100).toFixed(0)}% of safe height) `
      + `text ${result.fontSizes.eyebrow}/${result.fontSizes.headline}/${result.fontSizes.sub}px `
      + `fonts [${[...new Set(result.fonts)].join(', ')}]`);
    for (const failure of failures) console.log(`       - ${failure}`);
    failed ||= failures.length > 0;
    if (shots) {
      execFileSync(chrome, [...common, size,
        `--screenshot=${join(shots, `lobby-${vp.width}x${vp.height}.png`)}`, pathToFileURL(page).href],
      { stdio: 'ignore' });
    }
  }
  if (shots) console.log(`screenshots: ${shots}`);
  process.exit(failed ? 1 : 0);
}

await main();
