// @vitest-environment node
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { transformSync } from 'esbuild';
import { beforeAll, describe, expect, it } from 'vitest';
import * as webAssignment from './assignmentView';
import * as webFlow from './flow';
import * as webMessages from './partyMessages';
import * as webSlideshow from './partySlideshow';
import * as webLiveItems from './liveItems';
import * as webGrant from './partyDisplayGrant';
import * as webRemote from './remoteMap';

/**
 * ONE TV PRODUCT, TWO RENDERERS.
 *
 * A browser running /tv and the native app are the same NubArca display. Their
 * rules cannot live in one file — the app is a separate React Native project
 * whose every change ships as a TV release — so the browser carries a port,
 * and THIS is what keeps the port honest: it loads the app's own modules
 * straight from tv/src and runs both implementations against the same cases.
 *
 * Two kinds of failure are caught:
 *   * the rule drifts — a timing, a threshold, a transition changes on one side
 *     and not the other;
 *   * the rule grows — the app exports something new and the browser has not
 *     ported it (every native runtime export must have a web counterpart or a
 *     stated reason not to).
 *
 * The native files are pure TypeScript with type-only imports. They are
 * transpiled here with esbuild (the app's tsconfig extends an Expo base that
 * this project cannot resolve, so they are not loaded through Vite).
 */

const TV_SRC = resolve(__dirname, '../../../../tv/src');
const work = mkdtempSync(join(tmpdir(), 'nubarca-tv-parity-'));

async function loadNative(relative: string): Promise<Record<string, unknown>> {
  const source = readFileSync(join(TV_SRC, relative), 'utf8');
  const { code } = transformSync(source, { loader: 'ts', format: 'esm', tsconfigRaw: {} });
  if (/^\s*import\s+(?!type\b)/m.test(code)) {
    throw new Error(`${relative} has a runtime import; the parity loader only supports pure modules`);
  }
  const out = join(work, relative.replace(/[/\\]/g, '__').replace(/\.ts$/, '.mjs'));
  writeFileSync(out, code);
  return import(/* @vite-ignore */ pathToFileURL(out).href) as Promise<Record<string, unknown>>;
}

type Module = Record<string, unknown>;
let native: {
  assignment: Module; flow: Module; messages: Module; slideshow: Module; liveItems: Module; grant: Module;
  remote: Module;
};

beforeAll(async () => {
  native = {
    assignment: await loadNative('lib/assignmentView.ts'),
    flow: await loadNative('personal/flow.ts'),
    messages: await loadNative('lib/partyMessages.ts'),
    slideshow: await loadNative('lib/partySlideshow.ts'),
    liveItems: await loadNative('lib/liveItems.ts'),
    grant: await loadNative('lib/partyDisplayGrant.ts'),
    remote: await loadNative('video/remoteMap.ts'),
  };
});

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const fn = (module: Module, name: string) => module[name] as (...args: any[]) => unknown;

/** Every native runtime export has a web counterpart, and every constant has the same value. */
function assertSurface(nativeModule: Module, webModule: Module, notPorted: Record<string, string> = {}) {
  for (const [name, value] of Object.entries(nativeModule)) {
    if (name in notPorted) continue;
    expect(webModule, `the browser has not ported ${name}`).toHaveProperty(name);
    if (typeof value !== 'function') expect(webModule[name], name).toEqual(value);
  }
}

describe('the browser ports every rule the app has', () => {
  it('assignment', () => assertSurface(native.assignment, webAssignment));
  it('navigation', () => assertSurface(native.flow, webFlow));
  it('greetings and Heroes', () => assertSurface(native.messages, webMessages));
  it('slideshow timing', () => assertSurface(native.slideshow, webSlideshow));
  it('live items', () => assertSurface(native.liveItems, webLiveItems));
  it('the remote in the viewer', () => assertSurface(native.remote, webRemote));
  it('game display grant', () => assertSurface(native.grant, webGrant, {
    // The app hands the stage to a WebView through a URL and a message bridge;
    // the browser renders the same stage in its own document and needs neither.
    stageUrl: 'no WebView: the browser renders the stage in-process',
    parseRendererMessage: 'no WebView bridge in the browser',
    RENDERER_PROTOCOL: 'no WebView bridge in the browser',
  }));
});

const assignments = [
  null,
  undefined,
  { kind: 'general', albumId: null, albumName: null, partyAvailable: false, presentation: 'general' },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: true, presentation: 'slideshow', assignmentKey: 'k1' },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: true, presentation: 'game', assignmentKey: 'k1' },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: false, presentation: 'unavailable', assignmentKey: 'k1' },
  { kind: 'party', albumId: null, albumName: null, partyAvailable: true, presentation: 'slideshow', assignmentKey: 'k2' },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: true },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: false },
  { kind: 'party', albumId: 'a1', albumName: 'Festa', partyAvailable: true, presentation: 'bogus', assignmentKey: null },
];

describe('the same control plane', () => {
  it.each(assignments.map((a, i) => [i, a]))('reads assignment #%s the same way', (_i, dto) => {
    expect(webAssignment.toAssignmentView(dto as never))
      .toEqual(fn(native.assignment, 'toAssignmentView')(dto));
  });

  it('heartbeats and backs off on the same clock', () => {
    for (const [last, now] of [[null, 0], [0, 59_999], [0, 60_000], [1_000, 90_000]] as const) {
      expect(webAssignment.shouldHeartbeat(last, now)).toBe(fn(native.assignment, 'shouldHeartbeat')(last, now));
    }
    for (let attempt = -2; attempt < 15; attempt += 1) {
      expect(webAssignment.backoffMs(attempt)).toBe(fn(native.assignment, 'backoffMs')(attempt));
    }
  });
});

describe('the same navigation', () => {
  const partyA = { key: 'kA', albumId: 'a1', albumName: 'Festa A' };
  const partyB = { key: 'kB', albumId: 'a2', albumName: 'Festa B' };
  const home = { displayName: 'Anna', galleryAvailable: true };
  const states = [
    { name: 'loading' },
    { name: 'pairing', incomplete: false },
    { name: 'pairing', incomplete: true },
    { name: 'mode', notice: null },
    { name: 'mode', notice: 'pinChanged' },
    { name: 'party' },
    { name: 'partySlideshow', party: partyA },
    { name: 'partyGame', party: partyA },
    { name: 'partyUnavailable', party: partyA },
    { name: 'pin', target: 'personal' },
    { name: 'pin', target: 'beautyLab' },
    { name: 'personalHome', home },
    { name: 'personalHome', home: { ...home, galleryAvailable: false } },
    { name: 'personalLibrary', home },
    { name: 'beautyLab', home },
  ];
  const view = (presentation: string, party = partyA) =>
    (presentation === 'general' ? { presentation } : { presentation, party });
  const events = [
    { type: 'SESSION_READY' },
    { type: 'SESSION_READY', assignment: view('general') },
    { type: 'SESSION_READY', assignment: view('slideshow') },
    { type: 'SESSION_READY', assignment: view('game') },
    { type: 'SESSION_INVALID' },
    { type: 'ASSOCIATION_INCOMPLETE' },
    { type: 'CHOOSE_PARTY' },
    { type: 'CHOOSE_PERSONAL' },
    { type: 'CHOOSE_BEAUTY_LAB' },
    { type: 'PIN_CANCELLED' },
    { type: 'UNLOCKED', home },
    { type: 'OPEN_LIBRARY' },
    { type: 'LIBRARY_BACK' },
    { type: 'LOCK' },
    { type: 'LOCK', reason: 'pinChanged' },
    { type: 'PARTY_EXIT' },
    { type: 'ASSIGNMENT', view: view('general') },
    { type: 'ASSIGNMENT', view: view('slideshow') },
    { type: 'ASSIGNMENT', view: view('game') },
    { type: 'ASSIGNMENT', view: view('unavailable') },
    { type: 'ASSIGNMENT', view: view('slideshow', partyB) },
    { type: 'ASSIGNMENT', view: view('slideshow', { ...partyA, albumName: 'Renamed' }) },
    { type: 'PARTY_CONTENT_GONE' },
  ];
  const cases = states.flatMap((state) => events.map((event) => [state, event] as const));

  it.each(cases.map(([s, e]) => [s.name, e.type, s, e]))(
    '%s + %s: the same next screen and the same teardown',
    (_s, _e, state, event) => {
      expect(webFlow.tvFlowReducer(state as never, event as never))
        .toEqual(fn(native.flow, 'tvFlowReducer')(state, event));
      expect(webFlow.flowEffects(state as never, event as never))
        .toEqual(fn(native.flow, 'flowEffects')(state, event));
    },
  );

  it('admits a session the same way', () => {
    for (const assignment of [view('general'), view('slideshow'), view('game'), view('unavailable')]) {
      for (const pinConfigured of [true, false]) {
        expect(webFlow.admissionEvents(assignment as never, pinConfigured))
          .toEqual(fn(native.flow, 'admissionEvents')(assignment, pinConfigured));
      }
    }
  });

  it('classifies the same states', () => {
    for (const state of states) {
      expect(webFlow.isAssignedPartyState(state as never)).toBe(fn(native.flow, 'isAssignedPartyState')(state));
      expect(webFlow.isPersonalState(state as never)).toBe(fn(native.flow, 'isPersonalState')(state));
    }
  });
});

describe('the same greetings and Heroes', () => {
  const m = (id: string, over: Record<string, unknown> = {}) => ({
    id, displayName: 'Anna', text: `ciao ${id}`, createdAt: '2027-06-12T20:00:00Z',
    isHero: false, heroPromotedAt: null, ...over,
  });
  const feeds = [
    [],
    [m('a')],
    [m('a'), m('b', { isHero: true, heroPromotedAt: '2027-06-12T20:02:00Z' })],
    [m('c', { isHero: true, heroPromotedAt: '2027-06-12T20:01:00Z' }),
      m('b', { isHero: true, heroPromotedAt: '2027-06-12T20:01:00Z' }),
      m('a', { isHero: true, heroPromotedAt: null })],
    [m('a', { text: 'changed' }), m('b')],
  ];

  it('compares, orders and remaps feeds the same way', () => {
    for (const a of feeds) {
      expect(webMessages.heroCandidates(a as never)).toEqual(fn(native.messages, 'heroCandidates')(a));
      for (const b of feeds) {
        expect(webMessages.sameMessages(a as never, b as never)).toBe(fn(native.messages, 'sameMessages')(a, b));
      }
      for (const id of [undefined, 'a', 'b', 'zz']) {
        for (const previous of [-1, 0, 1, 7]) {
          expect(webMessages.remapRibbonIndex(a as never, id, previous))
            .toBe(fn(native.messages, 'remapRibbonIndex')(a, id, previous));
        }
      }
    }
  });

  it('shows the band and the card under the same conditions', () => {
    for (let bits = 0; bits < 32; bits += 1) {
      const flags = [1, 2, 4, 8, 16].map((b) => (bits & b) !== 0);
      const ribbon = {
        partyEnabled: flags[0], messageCount: flags[1] ? 2 : 0, overlayVisible: flags[2], heroVisible: flags[3],
      };
      expect(webMessages.ribbonVisible(ribbon)).toBe(fn(native.messages, 'ribbonVisible')(ribbon));
      const hero = {
        partyEnabled: flags[0], slideshowMode: flags[1], playing: flags[2],
        faceFilterActive: flags[3], candidateCount: flags[4] ? 1 : 0,
      };
      expect(webMessages.heroEligible(hero)).toBe(fn(native.messages, 'heroEligible')(hero));
      const settle = { heroVisible: flags[1], slideshowMode: flags[2], playing: flags[3] };
      const debt = { owed: flags[0] };
      expect(webMessages.settleBoundary(debt, settle)).toEqual(fn(native.messages, 'settleBoundary')(debt, settle));
    }
    for (const [visible, messageCount] of [[true, 0], [true, 1], [true, 2], [false, 3]] as const) {
      expect(webMessages.ribbonRotating({ visible, messageCount }))
        .toBe(fn(native.messages, 'ribbonRotating')({ visible, messageCount }));
    }
  });

  it('rotates Heroes and counts boundaries the same way over a whole evening', () => {
    let webRotation = webMessages.beginHeroRotation();
    let nativeRotation = fn(native.messages, 'beginHeroRotation')();
    let webCount = 0;
    let nativeCount = 0;
    for (let step = 0; step < 60; step += 1) {
      const feed = feeds[step % feeds.length];
      const webPick = webMessages.nextHero(webRotation, feed as never);
      const nativePick = fn(native.messages, 'nextHero')(nativeRotation, feed) as typeof webPick;
      expect(webPick).toEqual(nativePick);
      webRotation = webPick.rotation;
      nativeRotation = nativePick.rotation;

      const eligible = step % 7 !== 3;
      const webOutcome = webMessages.onMediaBoundary({ boundariesSinceHero: webCount, eligible });
      const nativeOutcome = fn(native.messages, 'onMediaBoundary')({ boundariesSinceHero: nativeCount, eligible });
      expect(webOutcome).toEqual(nativeOutcome);
      webCount = webOutcome.boundariesSinceHero;
      nativeCount = (nativeOutcome as typeof webOutcome).boundariesSinceHero;
    }
    expect(webMessages.deferBoundary()).toEqual(fn(native.messages, 'deferBoundary')());
    expect(webMessages.discardBoundary()).toEqual(fn(native.messages, 'discardBoundary')());
  });
});

describe('the same slideshow clock', () => {
  const timings = [
    null,
    { photoSeconds: 8, maxVideoSeconds: 30 },
    { photoSeconds: 1, maxVideoSeconds: 1 },
    { photoSeconds: 999, maxVideoSeconds: 9999 },
    { photoSeconds: Number.NaN, maxVideoSeconds: Number.POSITIVE_INFINITY },
    { photoSeconds: 7.6, maxVideoSeconds: 12.4 },
  ];

  it('holds a photo and caps a video for the same time', () => {
    for (const timing of timings) {
      expect(webSlideshow.photoSlideMs(timing)).toBe(fn(native.slideshow, 'photoSlideMs')(timing));
      expect(webSlideshow.videoCapSeconds(timing)).toBe(fn(native.slideshow, 'videoCapSeconds')(timing));
      for (let bits = 0; bits < 8; bits += 1) {
        const input = { slideshowMode: (bits & 1) !== 0, partyEnabled: (bits & 2) !== 0, playing: (bits & 4) !== 0, timing };
        expect(webSlideshow.videoPlaybackProps(input)).toEqual(fn(native.slideshow, 'videoPlaybackProps')(input));
      }
    }
  });

  it('advances a video exactly once, on media time', () => {
    for (const cap of [null, 5, 30]) {
      let web = webSlideshow.beginVideoRotation(cap);
      let nat = fn(native.slideshow, 'beginVideoRotation')(cap) as typeof web;
      for (const [kind, time] of [['p', 1], ['p', 4.9], ['p', 5], ['p', Number.NaN], ['p', 31], ['e', 0], ['p', 40]] as const) {
        const w = kind === 'e' ? webSlideshow.onVideoEnded(web) : webSlideshow.onVideoProgress(web, time);
        const n = (kind === 'e'
          ? fn(native.slideshow, 'onVideoEnded')(nat)
          : fn(native.slideshow, 'onVideoProgress')(nat, time)) as typeof w;
        expect(w).toEqual(n);
        web = w.state;
        nat = n.state;
      }
    }
  });

  it('skips, pauses and promotes under the same conditions', () => {
    for (let bits = 0; bits < 32; bits += 1) {
      const f = [1, 2, 4, 8, 16].map((b) => (bits & b) !== 0);
      const grace = { slideshowMode: f[0], partyEnabled: f[1], playing: f[2], isVideo: f[3], videoReady: f[4] };
      expect(webSlideshow.shouldArmPreparingGrace(grace)).toBe(fn(native.slideshow, 'shouldArmPreparingGrace')(grace));
      const play = { slideshowMode: f[0], isVideo: f[1] };
      expect(webSlideshow.resolvePlayPause(play)).toBe(fn(native.slideshow, 'resolvePlayPause')(play));
      const rotating = { slideshowMode: f[0], playing: f[1] };
      expect(webSlideshow.photoRotationActive(rotating)).toBe(fn(native.slideshow, 'photoRotationActive')(rotating));
    }
  });
});

describe('the same live refresh', () => {
  const it_ = (id: string) => ({ id } as never);
  const lists = [[], [it_('a')], [it_('a'), it_('b')], [it_('z'), it_('a'), it_('b')], [it_('b')]];
  it('keeps the same item on screen', () => {
    for (const a of lists) {
      for (const b of lists) {
        expect(webLiveItems.sameItemIds(a, b)).toBe(fn(native.liveItems, 'sameItemIds')(a, b));
      }
      for (const id of [undefined, 'a', 'b', 'q']) {
        for (const previous of [-3, 0, 1, 5]) {
          expect(webLiveItems.remapIndexById(a, id, previous))
            .toBe(fn(native.liveItems, 'remapIndexById')(a, id, previous));
        }
      }
    }
  });
});

describe('the same game grant lifecycle', () => {
  it('mints, retries and renews on the same schedule', () => {
    for (const status of [null, 200, 401, 404, 429, 500, 503]) {
      expect(webGrant.classifyMintFailure(status)).toBe(fn(native.grant, 'classifyMintFailure')(status));
    }
    for (const failure of ['not-assigned', 'transient'] as const) {
      for (let attempt = -1; attempt < 14; attempt += 1) {
        expect(webGrant.mintRetryDelayMs(failure, attempt)).toBe(fn(native.grant, 'mintRetryDelayMs')(failure, attempt));
      }
    }
    const now = Date.parse('2027-06-12T20:00:00Z');
    for (const grant of [
      { expiresAt: '2027-06-12T21:00:00Z', expiresInSeconds: 3600 },
      { expiresAt: '2027-06-12T21:00:00Z' },
      { expiresAt: '2027-06-12T20:00:10Z', expiresInSeconds: 10 },
      { expiresAt: 'nonsense' },
      { expiresAt: '2027-06-12T21:00:00Z', expiresInSeconds: -5 },
    ]) {
      expect(webGrant.renewDelayMs(grant, now)).toBe(fn(native.grant, 'renewDelayMs')(grant, now));
    }
  });
});

describe('the same remote', () => {
  it('does the same thing for every press, on a photo and on a video', () => {
    const events = ['menu', 'left', 'longLeft', 'rewind', 'right', 'longRight', 'fastForward',
      'select', 'playPause', 'up', 'longUp', 'down', 'longDown', 'back', 'blur', ''];
    for (const event of events) {
      for (const isVideo of [false, true]) {
        expect(webRemote.mapViewerRemoteEvent(event, isVideo))
          .toBe(fn(native.remote, 'mapViewerRemoteEvent')(event, isVideo));
      }
    }
  });
});
