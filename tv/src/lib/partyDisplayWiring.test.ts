import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';
import { tvFlowReducer, type TvFlowState } from '../personal/flow.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const app = source('../../App.tsx');
const screen = source('../screens/PartyDisplayScreen.tsx');
const tvApi = source('../api/tv.ts');
const wakePolicy = source('../video/wakePolicy.ts');

// The security boundary of the party display, asserted structurally.
//
// Most of what makes this safe is an ABSENCE — no party token, no session
// cookie in the WebView, no guest join, nothing persisted — and an absence is
// exactly what a behavioural test cannot see. Comments are stripped before
// matching (testing/sourceText), so the prose explaining a rule cannot make the
// rule's assertion pass.

test('the television never holds a party token or a guest identity', () => {
  // The public party surface is a GUEST capability. A display must never
  // address it, and the native client has no code that could.
  assert.doesNotMatch(app, /\/api\/party\//);
  assert.doesNotMatch(tvApi, /\/api\/party\/\{?token/);
  assert.doesNotMatch(screen, /\/api\/party\//);

  // No guest join, ever: a display that became a participant would inflate the
  // very count the stage is showing.
  assert.doesNotMatch(app, /game\/join|joinPartyGame/);
  assert.doesNotMatch(screen, /game\/join|joinPartyGame/);

  // The mint takes no arguments: the party comes from the device's own
  // assignment, resolved server-side, so there is nothing here to point
  // somewhere else.
  assert.match(tvApi, /mintPartyDisplayGrant\(signal\?: AbortSignal\)/);
  assert.match(tvApi, /tvPost<TvPartyDisplayGrant>\('\/api\/tv\/party-display\/grant'/);
});

test('the grant is never persisted anywhere', () => {
  // It lives in memory and in a URL fragment, and a reload legitimately has
  // none — the shell mints another rather than the page inventing one.
  for (const file of [app, screen, tvApi]) {
    assert.doesNotMatch(file, /AsyncStorage[\s\S]{0,200}grant/i);
    assert.doesNotMatch(file, /localStorage|sessionStorage/);
  }
  assert.match(screen, /#grant=\$\{encodeURIComponent/);
});

test('the WebView is display-only and fails closed on navigation', () => {
  // Nothing inside the page may take focus or scroll: the D-pad belongs to the
  // shell, and this is a screen in a corner rather than a browser.
  assert.match(screen, /focusable=\{false\}/);
  assert.match(screen, /scrollEnabled=\{false\}/);

  // One document, one origin. A link, a redirect or a popup is refused rather
  // than followed.
  assert.match(screen, /originWhitelist=\{\[getBaseUrl\(\)\]\}/);
  assert.match(screen, /onShouldStartLoadWithRequest/);
  assert.match(screen, /setSupportMultipleWindows=\{false\}/);
  assert.match(screen, /javaScriptCanOpenWindowsAutomatically=\{false\}/);
  assert.match(screen, /allowFileAccess=\{false\}/);
  assert.match(screen, /mixedContentMode="never"/);

  // The TV session cookie stays in the native client. The WebView knows only
  // the grant.
  assert.match(screen, /sharedCookiesEnabled=\{false\}/);
  assert.match(screen, /thirdPartyCookiesEnabled=\{false\}/);
  assert.match(screen, /incognito/);
  assert.doesNotMatch(screen, /TvSession|NubArca\.TvSession/);
});

test('the display holds the screen through the EXISTING keep-awake, not a second one', () => {
  // One module answers "hold the screen" for the whole app. A second wake lock
  // would be the second authority wakePolicy exists to prevent.
  assert.match(screen, /useScreenAwake\(shouldKeepPartyDisplayAwake\(/);
  assert.match(wakePolicy, /export function shouldKeepPartyDisplayAwake/);
  assert.doesNotMatch(screen, /activateKeepAwake|deactivateKeepAwake/);
});

test('the assignment is polled briskly while a party is on screen', () => {
  // GENERAL keeps its minute; a party drops to five seconds, because an owner
  // ending the evening has to reach the screen in the room.
  assert.match(app, /partyRate \? 5_000 : 60_000/);
  // And the same read is the session check.
  assert.match(app, /err\.status === 401\) onSessionInvalid\(\)/);
});

test('there is no second Party Game state machine on the television', () => {
  // The shell knows about an ASSIGNMENT. It does not know what a phase is,
  // when voting opens, or which challenge is current — those stay server-side
  // and reach the room through the canonical web stage.
  for (const file of [app, screen]) {
    assert.doesNotMatch(file, /voting_open|challenge_reveal|voting_closed|reveal_result/);
    assert.doesNotMatch(file, /stageScene|PartyChallengeCard/);
  }
});

test('no spike harness reached the product', () => {
  for (const file of [app, screen]) {
    assert.doesNotMatch(file, /WebViewSpike|webviewSpike|Replica stage|memory stress/i);
  }
});

// --- the flow, which is where takeover actually happens ---------------------

const mode: TvFlowState = { name: 'mode', notice: null };
const display = (assignmentKey: string): TvFlowState => ({ name: 'partyDisplay', assignmentKey });

test('an assigned party takes the screen, and giving it up returns to the shell', () => {
  const taken = tvFlowReducer(mode, { type: 'ASSIGNMENT', kind: 'party', assignmentKey: 'a1' });
  assert.deepEqual(taken, display('a1'));

  // PARTY -> GENERAL leaves at once rather than keeping a stale party up.
  assert.deepEqual(
    tvFlowReducer(taken, { type: 'ASSIGNMENT', kind: 'general', assignmentKey: null }),
    mode);
});

test('Party A to Party B is a different state, so the display remounts', () => {
  const a = display('a1');
  // The same party is a no-op: a poll every five seconds must not restart the
  // show twenty times a minute.
  assert.equal(tvFlowReducer(a, { type: 'ASSIGNMENT', kind: 'party', assignmentKey: 'a1' }), a);

  // A different one is a different state, which is what forces a teardown and
  // a fresh grant rather than the old party flashing in the new one's place.
  assert.deepEqual(
    tvFlowReducer(a, { type: 'ASSIGNMENT', kind: 'party', assignmentKey: 'a2' }),
    display('a2'));
});

test('a party never appears over somebody standing in their own library', () => {
  const personal: TvFlowState = {
    name: 'personalLibrary', home: { displayName: 'Ada', galleryAvailable: true },
  };
  const assignment = { type: 'ASSIGNMENT' as const, kind: 'party' as const, assignmentKey: 'a1' };
  assert.equal(tvFlowReducer(personal, assignment), personal);
  assert.equal(tvFlowReducer({ name: 'pin', target: 'personal' }, assignment).name, 'pin');
  // Nor before there is a session to trust.
  assert.equal(tvFlowReducer({ name: 'loading' }, assignment).name, 'loading');
  assert.equal(
    tvFlowReducer({ name: 'pairing', incomplete: false }, assignment).name, 'pairing');
});

test('a revoked session still tears the display down like any other state', () => {
  assert.deepEqual(
    tvFlowReducer(display('a1'), { type: 'SESSION_INVALID' }),
    { name: 'pairing', incomplete: false });
});
