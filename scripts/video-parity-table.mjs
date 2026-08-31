#!/usr/bin/env node
// Print the cross-consumer /video parity table (VIDEO-DELIVERY-PARITY-01).
//
// The table is DERIVED, never hand-written: it loads the real adapter each
// consumer ships and pushes every row of shared/video-delivery/parity-matrix
// .json through it. Each project's videoDeliveryParity test asserts the same
// agreement inside its own suite; this script is how a human reads the result
// in one place.
//
//   node scripts/video-parity-table.mjs           # print the table
//   node scripts/video-parity-table.mjs --check   # non-zero if a row differs
//
// It needs a Node with TypeScript type stripping (>= 22.18). The three adapter
// modules are pure on purpose — no React, no Expo, no DOM — so they load here.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const { classifyVideoDelivery } = await import(
  resolve(ROOT, 'shared/video-delivery/videoDelivery.ts')
);
const { videoPlaybackModeFor } = await import(
  resolve(ROOT, 'frontend/src/video/webPlaybackMode.ts')
);
const { probeStateForOutcome } = await import(
  resolve(ROOT, 'mobile/src/components/videoPlayback.ts')
);
const { tvVideoModeFor } = await import(resolve(ROOT, 'tv/src/video/probeClassify.ts'));

const matrix = JSON.parse(
  readFileSync(resolve(ROOT, 'shared/video-delivery/parity-matrix.json'), 'utf8'),
);

// Each consumer names the same thing differently ('direct' vs 'progressive',
// 'unavailable' vs 'error'). The table compares MEANING, so the presentation
// names are folded back onto the verdict they must represent.
const WEB = { hls: 'hls', direct: 'prog', preparing: 'prep', error: 'err' };
const MOBILE = {
  ready: null, // refined from the verdict's mode
  preparing: 'prep',
  unavailable: 'err',
  error: 'err',
};
const TV = { hls: 'hls', direct: 'prog', preparing: 'prep', error: 'err' };

const rows = [];
let mismatches = 0;

for (const row of matrix.classification) {
  const verdict = classifyVideoDelivery(row.status, row.contentType);
  const readyCell = verdict.kind === 'ready' ? (verdict.mode === 'hls' ? 'hls' : 'prog') : null;

  const web = WEB[videoPlaybackModeFor(verdict)];
  const mobileState = probeStateForOutcome(verdict);
  const mobile = mobileState === 'ready' ? readyCell : MOBILE[mobileState];
  const tv = TV[tvVideoModeFor(verdict)];

  const agree = web === mobile && mobile === tv;
  if (!agree) mismatches += 1;
  rows.push({
    case: `${row.status} + ${row.contentType ?? '(no content-type)'}`,
    verdict: verdict.kind === 'ready' ? `ready/${verdict.mode}` : verdict.kind,
    web,
    mobile,
    tv,
    agree,
  });
}

const width = (key, head) =>
  Math.max(head.length, ...rows.map((r) => String(r[key]).length));
const w = {
  case: width('case', 'case'),
  verdict: width('verdict', 'canonical verdict'),
  web: Math.max(6, width('web', 'web')),
  mobile: Math.max(6, width('mobile', 'mobile')),
  tv: Math.max(6, width('tv', 'tv')),
};

const line = (c, v, a, b, d) =>
  `${String(c).padEnd(w.case)}  ${String(v).padEnd(w.verdict)}  ` +
  `${String(a).padEnd(w.web + 2)}${String(b).padEnd(w.mobile + 2)}${String(d)}`;

console.log(line('case', 'canonical verdict', 'web', 'mobile', 'tv'));
console.log('-'.repeat(w.case + w.verdict + w.web + w.mobile + w.tv + 10));
for (const r of rows) {
  console.log(line(r.case, r.verdict, r.web, r.mobile, r.tv) + (r.agree ? '' : '   <-- DIVERGENT'));
}
console.log('');
console.log(
  mismatches === 0
    ? `all ${rows.length} rows agree across web, mobile and tv`
    : `${mismatches} of ${rows.length} rows DIVERGE`,
);

if (process.argv.includes('--check') && mismatches > 0) process.exit(1);
