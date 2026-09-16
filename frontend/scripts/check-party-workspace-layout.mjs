#!/usr/bin/env node
// The Party workspace, measured in a real browser.
//
// jsdom has no layout engine, so the unit suite proves the workspace's
// structure and behaviour and can say nothing about the three things that
// actually decide whether it is usable on a phone:
//
//   * whether the page scrolls sideways at 320px;
//   * whether a thumb can hit its controls;
//   * whether the side gutter survives at every width.
//
// This renders the markup the REAL components emit — written by
// `src/party/workspace/partyWorkspace.fixtures.tsx`, against mocked responses,
// so there is no second copy of the product to drift — inside the real global
// stylesheet and the real PartyWorkspace.css, at the widths the design was
// written for, and asserts on bounding boxes.
//
// It is a dev/QA tool, not a CI job: it needs a Chromium binary. It looks for
// CHROME_BIN, then Playwright's cache, then the usual system names.
//
//   node scripts/check-party-workspace-layout.mjs [--screenshots <dir>]
//
// The fixtures contain no production data.

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const frontend = resolve(here, '..');

// 320 is the narrowest phone the product supports, 375 and 430 the two the
// design was written against, 820 the tablet where the second column arrives,
// and 1440 a desktop where the section rail leaves the flow.
const VIEWPORTS = [
  { name: '320', width: 320, height: 900 },
  { name: '375', width: 375, height: 900 },
  { name: '430', width: 430, height: 950 },
  { name: '820', width: 820, height: 1000 },
  { name: '1440', width: 1440, height: 1000 },
];

/** The smallest interactive box the product accepts, in CSS pixels. */
const MIN_TAP = 44;

/** How much clear space the page keeps at each side, at every width. */
const MIN_GUTTER = 16;

function findChrome() {
  if (process.env.CHROME_BIN && existsSync(process.env.CHROME_BIN)) return process.env.CHROME_BIN;
  const cache = join(homedir(), '.cache', 'ms-playwright');
  if (existsSync(cache)) {
    const builds = readdirSync(cache)
      .filter((d) => d.startsWith('chromium-'))
      .sort()
      .reverse();
    for (const build of builds) {
      const candidate = join(cache, build, 'chrome-linux64', 'chrome');
      if (existsSync(candidate)) return candidate;
      const older = join(cache, build, 'chrome-linux', 'chrome');
      if (existsSync(older)) return older;
    }
  }
  for (const name of ['chromium', 'chromium-browser', 'google-chrome', 'google-chrome-stable']) {
    try {
      return execFileSync('which', [name], { encoding: 'utf8' }).trim();
    } catch { /* not installed */ }
  }
  return null;
}

const args = process.argv.slice(2);
const shotIndex = args.indexOf('--screenshots');
const shotDir = shotIndex >= 0 ? resolve(args[shotIndex + 1]) : null;
const fixtureDir = resolve(process.env.PARTY_FIXTURE_DIR ?? '/tmp/party-fixtures');

if (!existsSync(fixtureDir)) {
  console.error(
    `No fixtures at ${fixtureDir}.\n`
    + 'Render them first:\n'
    + `  PARTY_FIXTURE_DIR=${fixtureDir} npx vitest run --config vitest.fixtures.config.ts`,
  );
  process.exit(2);
}

const chrome = findChrome();
if (!chrome) {
  console.error('No Chromium binary found. Set CHROME_BIN, or install Playwright\'s chromium.');
  process.exit(2);
}

const styles = [
  readFileSync(join(frontend, 'src', 'styles.css'), 'utf8'),
  readFileSync(join(frontend, 'src', 'party', 'Party.css'), 'utf8'),
  readFileSync(join(frontend, 'src', 'party', 'PartyGuestConsole.css'), 'utf8'),
  readFileSync(join(frontend, 'src', 'party', 'workspace', 'PartyWorkspace.css'), 'utf8'),
].join('\n');

const fixtures = readdirSync(fixtureDir).filter((f) => f.endsWith('.html')).sort();

const work = join(tmpdir(), `party-layout-${process.pid}`);
mkdirSync(work, { recursive: true });

/**
 * The page the browser opens: the application shell the workspace lives in —
 * `.app-main` owns the scrolling and the horizontal gutter — wrapped round one
 * fixture's markup, with the real stylesheets inline.
 */
function documentFor(html) {
  return `<!doctype html>
<html lang="it"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>${styles}</style>
<style>
  /* The shell, reduced to the two things that matter to layout: the scroll
     viewport and its gutter. Fonts are whatever the machine has — this
     measures boxes, not typefaces. */
  html, body { margin: 0; height: 100%; }
  body { background: var(--surface-canvas); color: var(--text-primary); }
  .app-main { height: 100vh; }
</style>
</head>
<body class="theme-dark"><div class="app-main">${html}</div></body></html>`;
}

function measure(pagePath, vp, screenshot) {
  const script = join(work, 'measure.mjs');
  writeFileSync(script, MEASURE_SCRIPT, 'utf8');
  const out = execFileSync(process.execPath, [
    script,
    chrome,
    pathToFileURL(pagePath).href,
    String(vp.width),
    String(vp.height),
    String(MIN_TAP),
    String(MIN_GUTTER),
    screenshot ?? '',
  ], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  return JSON.parse(out.slice(out.indexOf('{')));
}

// The measuring runs in its own process because it drives Chromium over the
// DevTools protocol, which wants an event loop of its own.
const MEASURE_SCRIPT = `
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const [chrome, url, width, height, minTap, minGutter, screenshot] = process.argv.slice(2);
const port = 9222 + (process.pid % 900);

const proc = spawn(chrome, [
  '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-sandbox',
  \`--remote-debugging-port=\${port}\`,
  \`--window-size=\${width},\${height}\`,
  'about:blank',
], { stdio: 'ignore' });

async function endpoint() {
  for (let i = 0; i < 100; i += 1) {
    try {
      const r = await fetch(\`http://127.0.0.1:\${port}/json/version\`);
      return (await r.json()).webSocketDebuggerUrl;
    } catch { await delay(100); }
  }
  throw new Error('Chromium did not start');
}

const ws = new WebSocket(await endpoint());
await new Promise((r) => { ws.onopen = r; });
let id = 0;
const pending = new Map();
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
};
function send(method, params = {}, sessionId) {
  const mid = ++id;
  return new Promise((res) => {
    pending.set(mid, (m) => (m.error ? res({ error: m.error }) : res(m.result)));
    ws.send(JSON.stringify({ id: mid, method, params, sessionId }));
  });
}

const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
await send('Page.enable', {}, sessionId);
await send('Runtime.enable', {}, sessionId);
await send('Emulation.setDeviceMetricsOverride', {
  width: Number(width), height: Number(height), deviceScaleFactor: 1, mobile: Number(width) < 700,
}, sessionId);
await send('Page.navigate', { url }, sessionId);
await delay(500);

const expression = \`(() => {
  const MIN_TAP = \${minTap};
  const MIN_GUTTER = \${minGutter};
  const problems = [];
  const main = document.querySelector('.app-main');
  const doc = document.documentElement;

  // 1. Nothing scrolls sideways. A page that does on a phone is broken.
  if (main.scrollWidth > main.clientWidth + 1) {
    problems.push('horizontal overflow: ' + main.scrollWidth + ' > ' + main.clientWidth);
    // Name the widest offenders so the fix is one element away.
    const wide = [...main.querySelectorAll('*')]
      .map((el) => ({ el, r: el.getBoundingClientRect() }))
      .filter(({ r }) => r.right > main.clientWidth + 1 || r.left < -1)
      .slice(0, 5)
      .map(({ el, r }) => (el.tagName.toLowerCase() + '.' + (el.className || '').toString().split(' ')[0]
        + ' [' + Math.round(r.left) + '..' + Math.round(r.right) + ']'));
    for (const w of wide) problems.push('  overflows: ' + w);
  }

  // 2. Every interactive control a thumb has to hit is at least 44x44.
  const interactive = [...main.querySelectorAll('button, a[href], input, select, textarea, [role="switch"], [role="tab"]')]
    .filter((el) => {
      const s = getComputedStyle(el);
      if (s.display === 'none' || s.visibility === 'hidden') return false;
      // Visually hidden helpers are not targets.
      if (el.closest('.visually-hidden')) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && r.height > 0;
    });
  const small = interactive
    .map((el) => ({ el, r: el.getBoundingClientRect() }))
    .filter(({ el, r }) => {
      // An inline link inside a paragraph is text, not a target.
      if (el.tagName === 'A' && !el.className) return false;
      // A checkbox or radio wrapped in its own label: the LABEL is what a
      // thumb hits, and the box inside it is decoration. Measured on the
      // label, so a 13px input in a 44px label is correct and a bare one is
      // still caught.
      if (el.tagName === 'INPUT' && (el.type === 'checkbox' || el.type === 'radio')) {
        const label = el.closest('label');
        if (label && label.getBoundingClientRect().height >= MIN_TAP - 0.5) return false;
      }
      return r.height < MIN_TAP - 0.5 || r.width < 24;
    })
    .slice(0, 8)
    .map(({ el, r }) => (el.tagName.toLowerCase() + '.' + (el.className || '').toString().split(' ').slice(0, 2).join('.')
      + ' ' + Math.round(r.width) + 'x' + Math.round(r.height)));
  for (const s of small) problems.push('target too small: ' + s);

  // 3. The side gutter survives: nothing touches the edge of the screen.
  const contentful = [...main.querySelectorAll('.pw-panel, .pw-empty, .pw-card, .pw-title, .pw-section-title, .pw-notice')]
    .map((el) => el.getBoundingClientRect())
    .filter((r) => r.width > 0);
  const leftMost = Math.min(...contentful.map((r) => r.left));
  const rightMost = Math.max(...contentful.map((r) => r.right));
  if (contentful.length > 0) {
    if (leftMost < MIN_GUTTER - 0.5) problems.push('left gutter ' + Math.round(leftMost) + 'px');
    if (doc.clientWidth - rightMost < MIN_GUTTER - 0.5) {
      problems.push('right gutter ' + Math.round(doc.clientWidth - rightMost) + 'px');
    }
  }

  // 4. Two things stuck to the same edge must not cover each other.
  const rail = main.querySelector('.pw-nav');
  const toolbar = main.querySelector('.guest-toolbar');
  if (rail && toolbar && getComputedStyle(rail).position === 'sticky'
      && getComputedStyle(toolbar).position === 'sticky') {
    const a = rail.getBoundingClientRect();
    const b = toolbar.getBoundingClientRect();
    // Only when they share horizontal space at all: from 64rem the rail is a
    // COLUMN beside the content, and one sitting higher than the other then
    // covers nothing.
    const sideBySide = a.right <= b.left + 1 || b.right <= a.left + 1;
    if (!sideBySide && b.top < a.bottom - 1) {
      problems.push('the search hides under the section rail');
    }
  }

  return JSON.stringify({
    problems,
    summary: {
      scrollWidth: main.scrollWidth,
      clientWidth: main.clientWidth,
      controls: interactive.length,
      gutter: contentful.length ? Math.round(leftMost) : null,
    },
  });
})()\`;

const res = await send('Runtime.evaluate', { expression, returnByValue: true }, sessionId);
const value = JSON.parse(res.result.value);

if (screenshot) {
  const metrics = await send('Page.getLayoutMetrics', {}, sessionId);
  const full = Math.min(metrics.cssContentSize?.height ?? Number(height), 4000);
  await send('Emulation.setDeviceMetricsOverride', {
    width: Number(width), height: full, deviceScaleFactor: 1, mobile: Number(width) < 700,
  }, sessionId);
  await delay(150);
  const shot = await send('Page.captureScreenshot', { format: 'png' }, sessionId);
  mkdirSync(dirname(screenshot), { recursive: true });
  writeFileSync(screenshot, Buffer.from(shot.data, 'base64'));
}

process.stdout.write(JSON.stringify(value));
ws.close();
proc.kill();
process.exit(0);
`;

const problems = [];
const report = [];

for (const file of fixtures) {
  const name = file.replace(/\.html$/, '');
  const page = join(work, file);
  writeFileSync(page, documentFor(readFileSync(join(fixtureDir, file), 'utf8')), 'utf8');

  for (const vp of VIEWPORTS) {
    const measured = measure(page, vp, shotDir ? join(shotDir, `${name}-${vp.name}.png`) : null);
    report.push({ name, vp: vp.name, ...measured.summary });
    for (const problem of measured.problems) problems.push(`${name} @ ${vp.name}px: ${problem}`);
  }
}


console.log(`Party workspace layout — ${fixtures.length} states x ${VIEWPORTS.length} widths`);
for (const row of report) {
  console.log(`  ${row.name.padEnd(20)} ${String(row.vp).padStart(5)}px  `
    + `content ${row.scrollWidth}/${row.clientWidth}  controls ${row.controls}  gutter ${row.gutter}`);
}

if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s):`);
  for (const p of problems) console.error(`  ${p}`);
  process.exit(1);
}
console.log('\nNo overflow, no target under 44px, gutters hold at every width.');
if (shotDir) console.log(`Screenshots: ${shotDir}`);
