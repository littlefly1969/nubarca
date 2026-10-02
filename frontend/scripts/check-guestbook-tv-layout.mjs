#!/usr/bin/env node
// The guest book on the TELEVISION, measured in a real browser.
//
// jsdom has no layout engine, so the unit suite proves what the stage draws
// and its rules (src/party/PartyGuestbookTvStage.test.tsx). This script proves
// the LAYOUT: it bundles the real PartyGuestbookTvStage — the real memory card,
// the real stylesheets, the real fonts, the real measured fit — renders it in
// headless Chromium at the television viewports that matter, and asserts on
// what the browser actually laid out:
//
//   * the memory sits inside the screen, and the page never scrolls;
//   * the dedication is WHOLE: its column does not overflow, and every
//     character it was given is in the document — no ellipsis, no clamp;
//   * the signature is drawn, inside the card;
//   * every template, a portrait and a landscape photograph, a short, a long,
//     a many-lined and a pathological dedication.
//
// A dev/QA tool, not a CI job: it needs a Chromium binary. It looks for
// CHROME_BIN, then Playwright's cache, then the usual system names.
//
//   node scripts/check-guestbook-tv-layout.mjs [--screenshots <dir>]

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { build } from 'esbuild';

const here = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(here, '..');
const repo = resolve(frontend, '..');

// 960x540 is a 1080p Fire TV panel at devicePixelRatio 2 — the logical viewport
// a browser /tv on that panel gets. 720p and 1080p are the two the product
// promises.
const VIEWPORTS = [
  { width: 960, height: 540, label: 'Fire TV 1080p @2x' },
  { width: 1280, height: 720, label: '720p' },
  { width: 1920, height: 1080, label: '1080p' },
];

const TEMPLATES = ['nubarca', 'polaroid', 'editorial', 'celebration'];

// The dedications that stress a column: what a guest is allowed to write, at
// its extremes. 1000 code points is the server's limit; the server keeps every
// inner line break, so a "dedication" of blank lines is legal too.
const BODIES = {
  short: 'Auguri, Anna!',
  long: Array.from({ length: 1000 }, (_, i) => 'Che serata indimenticabile insieme '[i % 35]).join(''),
  lines: Array.from({ length: 24 }, (_, i) => `Riga numero ${i + 1} della dedica`).join('\n'),
  blank: `Cara Anna,${'\n'.repeat(80)}un abbraccio`,
  word: 'W'.repeat(400),
};

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

// A photograph of a known shape, with no network and no file to ship.
function photo(width, height) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}">`
    + '<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
    + '<stop offset="0" stop-color="#1565ff"/><stop offset="1" stop-color="#9a6cff"/></linearGradient></defs>'
    + `<rect width="${width}" height="${height}" fill="url(#g)"/>`
    + `<circle cx="${width / 2}" cy="${height / 2}" r="${Math.min(width, height) / 4}" fill="#00d4ff"/></svg>`;
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`;
}

function cases() {
  const out = [];
  for (const key of TEMPLATES) {
    for (const [bodyName, body] of Object.entries(BODIES)) {
      for (const [shape, w, h] of [['landscape', 1600, 1200], ['portrait', 1200, 1600]]) {
        out.push({
          name: `${key}/${bodyName}/${shape}`,
          entry: {
            id: `${key}-${bodyName}-${shape}`,
            authorDisplayName: 'Anna Maria Bianchi De Santis',
            body,
            createdAt: '2027-06-12T20:00:00Z',
            template: { key, version: 1 },
            media: {
              url: photo(w, h), width: w, height: h, orientation: shape,
              crop: { centerX: 0.4, centerY: 0.6, zoom: 1.5 },
            },
          },
        });
      }
    }
  }
  return out;
}

const ENTRY = `
import { createRoot } from 'react-dom/client';
import { I18nProvider } from '../src/i18n';
import { PartyGuestbookTvStage } from '../src/party/PartyGuestbookTvStage';
import '@fontsource/space-grotesk/latin-500.css';
import '@fontsource/space-grotesk/latin-600.css';
import '@fontsource/space-grotesk/latin-700.css';
import '@fontsource/exo-2/latin-400.css';
import '@fontsource/exo-2/latin-500.css';
import '@fontsource/exo-2/latin-600.css';
import '../src/styles.css';

const entry = window.__GUESTBOOK_CASE__;
createRoot(document.getElementById('root')).render(
  <I18nProvider><PartyGuestbookTvStage entries={[entry]} /></I18nProvider>,
);

(async () => { try {
  await document.fonts.ready;
  // Headless virtual time advances timers, not animation frames: wait on the
  // clock. The fit runs in layout effects and has settled long before this.
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  await wait(300);
  const img = document.querySelector('.guestbook-memory-photo img');
  if (img && !img.complete) await Promise.race([wait(2000), new Promise((r) => { img.onload = r; img.onerror = r; })]);
  await wait(300);
  const box = (el) => { const b = el.getBoundingClientRect();
    return { left: b.left, top: b.top, right: b.right, bottom: b.bottom, width: b.width, height: b.height }; };
  const card = document.querySelector('.guestbook-memory');
  const words = document.querySelector('.guestbook-memory-words');
  const body = document.querySelector('.guestbook-memory-body');
  const author = document.querySelector('.guestbook-memory-author');
  const cs = getComputedStyle(body);
  const result = {
    viewport: { width: innerWidth, height: innerHeight },
    card: box(card), words: box(words), author: box(author), body: box(body),
    photo: box(document.querySelector('.guestbook-memory-photo')),
    wordsScroll: words.scrollHeight, wordsClient: words.clientHeight,
    wordsScrollWidth: words.scrollWidth, wordsClientWidth: words.clientWidth,
    bodyText: body.textContent, authorText: author.textContent,
    fontPx: parseFloat(getComputedStyle(words).getPropertyValue('--guestbook-tv-font')),
    bodyStyle: { textOverflow: cs.textOverflow, lineClamp: cs.webkitLineClamp || cs.lineClamp || 'none',
      overflow: cs.overflow, whiteSpace: cs.whiteSpace },
    scroll: { width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight },
  };
  document.documentElement.dataset.layout = btoa(unescape(encodeURIComponent(JSON.stringify(result))));
} catch (error) {
  document.documentElement.dataset.layoutError = String(error && error.stack || error);
} })();
`;

async function bundle(work) {
  const entry = join(frontend, 'scripts', '.guestbook-tv-layout-entry.tsx');
  writeFileSync(entry, ENTRY);
  try {
    await build({
      entryPoints: [entry],
      bundle: true,
      outdir: work,
      format: 'iife',
      jsx: 'automatic',
      loader: { '.woff': 'file', '.woff2': 'file', '.png': 'file', '.svg': 'file' },
      alias: {
        '@nubarca/api-client': join(frontend, 'packages', 'api-client', 'src', 'index.ts'),
        '@nubarca/contracts': join(repo, 'packages', 'contracts', 'src', 'index.ts'),
      },
      define: { 'process.env.NODE_ENV': '"production"', 'import.meta.env.DEV': 'false' },
      logLevel: 'error',
    });
  } finally {
    rmSync(entry, { force: true });
  }
  return { js: join(work, '.guestbook-tv-layout-entry.js'), css: join(work, '.guestbook-tv-layout-entry.css') };
}

function page(work, assets, c) {
  const html = `<!doctype html>
<html lang="it"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="stylesheet" href="${pathToFileURL(assets.css).href}">
</head><body><div id="root"></div>
<script>window.__GUESTBOOK_CASE__ = ${JSON.stringify(c.entry)};</script>
<script src="${pathToFileURL(assets.js).href}"></script>
</body></html>`;
  const file = join(work, `${c.entry.id}.html`);
  writeFileSync(file, html);
  return file;
}

function check(result, c) {
  const failures = [];
  const eps = 1;
  const { viewport } = result;
  const within = (name, b) => {
    if (b.left < -eps || b.top < -eps || b.right > viewport.width + eps || b.bottom > viewport.height + eps) {
      failures.push(`${name} leaves the screen: ${JSON.stringify(b)}`);
    }
  };
  within('card', result.card);
  within('author', result.author);
  if (result.scroll.width > viewport.width || result.scroll.height > viewport.height) {
    failures.push(`the page scrolls: ${JSON.stringify(result.scroll)}`);
  }
  if (result.wordsScroll > result.wordsClient + 1) {
    failures.push(`the dedication overflows its column: ${result.wordsScroll} > ${result.wordsClient}`);
  }
  if (result.wordsScrollWidth > result.wordsClientWidth + 1) {
    failures.push(`the dedication overflows sideways: ${result.wordsScrollWidth} > ${result.wordsClientWidth}`);
  }
  // Inside the CARD, not just the screen: the card rounds its corners by
  // clipping, so anything past its edge is cut.
  const inCard = (name, b) => {
    const c = result.card;
    if (b.left < c.left - eps || b.top < c.top - eps || b.right > c.right + eps || b.bottom > c.bottom + eps) {
      failures.push(`${name} is outside the card: ${JSON.stringify(b)} vs ${JSON.stringify(c)}`);
    }
  };
  inCard('the words', result.words);
  inCard('the dedication', result.body);
  inCard('the signature', result.author);
  inCard('the photograph', result.photo);
  if (result.bodyText !== c.entry.body) failures.push('the dedication on screen is not the dedication written');
  if (!result.authorText.includes(c.entry.authorDisplayName)) failures.push('the signature is missing');
  if (result.bodyStyle.textOverflow === 'ellipsis' || String(result.bodyStyle.lineClamp) !== 'none') {
    failures.push(`the dedication is clamped: ${JSON.stringify(result.bodyStyle)}`);
  }
  if (result.bodyStyle.whiteSpace !== 'pre-wrap') failures.push('line breaks are not kept');
  return failures;
}

async function main() {
  const chrome = findChrome();
  if (!chrome) {
    console.error('check-guestbook-tv-layout: no Chromium found (set CHROME_BIN).');
    process.exit(2);
  }
  const shotsIndex = process.argv.indexOf('--screenshots');
  const shots = shotsIndex > 0 ? resolve(process.argv[shotsIndex + 1]) : null;
  if (shots) mkdirSync(shots, { recursive: true });

  const work = mkdtempSync(join(tmpdir(), 'nubarca-guestbook-tv-'));
  const profile = mkdtempSync(join(tmpdir(), 'nubarca-guestbook-tv-profile-'));
  const assets = await bundle(work);
  const common = ['--headless', '--no-sandbox', '--disable-gpu', '--hide-scrollbars',
    '--force-device-scale-factor=1', '--allow-file-access-from-files',
    `--user-data-dir=${profile}`, '--virtual-time-budget=8000'];

  let failed = false;
  let measured = 0;
  console.log(`chromium: ${chrome}`);
  try {
    const only = process.env.ONLY;
    for (const c of cases().filter((x) => !only || x.name === only)) {
      const file = page(work, assets, c);
      for (const vp of VIEWPORTS) {
        const size = `--window-size=${vp.width},${vp.height}`;
        const dom = execFileSync(chrome, [...common, size, '--dump-dom', pathToFileURL(file).href],
          { stdio: ['ignore', 'pipe', 'ignore'], maxBuffer: 32 * 1024 * 1024 }).toString();
        const encoded = /data-layout="([^"]+)"/.exec(dom)?.[1];
        if (!encoded) {
          const error = /data-layout-error="([^"]*)"/.exec(dom)?.[1] ?? 'no error recorded';
          console.log(`FAIL ${c.name} @ ${vp.width}x${vp.height}: the page produced no measurement (${error})`);
          failed = true;
          continue;
        }
        const result = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8'));
        const failures = check(result, c);
        measured += 1;
        if (failures.length > 0 || process.argv.includes('--verbose')) {
          console.log(`${failures.length ? 'FAIL' : 'ok  '} ${c.name.padEnd(32)} ${vp.label.padEnd(18)} `
            + `type ${result.fontPx}px photo ${result.photo.width.toFixed(0)}x${result.photo.height.toFixed(0)}`);
          for (const failure of failures) console.log(`       - ${failure}`);
        }
        failed ||= failures.length > 0;
        if (shots) {
          execFileSync(chrome, [...common, size,
            `--screenshot=${join(shots, `${c.entry.id}-${vp.width}x${vp.height}.png`)}`, pathToFileURL(file).href],
          { stdio: 'ignore' });
        }
      }
    }
  } finally {
    if (!process.env.KEEP_WORK) rmSync(work, { recursive: true, force: true }); else console.log(`work: ${work}`);
    rmSync(profile, { recursive: true, force: true });
  }
  console.log(`${failed ? 'FAILED' : 'ok'}: ${measured} layouts measured`
    + ` (${TEMPLATES.length} templates × ${Object.keys(BODIES).length} dedications × 2 shapes × ${VIEWPORTS.length} screens)`);
  if (shots) console.log(`screenshots: ${shots}`);
  process.exit(failed ? 1 : 0);
}

await main();
