import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const app = source('../../App.tsx');
const screen = source('../screens/PartyDisplayScreen.tsx');
const slideshow = source('../screens/PartySlideshowScreen.tsx');
const surface = source('../components/PartyNativeSurface.tsx');
const tvApi = source('../api/tv.ts');
const grantModule = source('./partyDisplayGrant.ts');
const wakePolicy = source('../video/wakePolicy.ts');

// The security boundary and the wiring of the party takeover, asserted
// structurally.
//
// Most of what makes this safe is an ABSENCE — no party token, no session
// cookie in the WebView, no guest join, nothing persisted — and an absence is
// exactly what a behavioural test cannot see. Comments are stripped before
// matching (testing/sourceText), so the prose explaining a rule cannot make the
// rule's assertion pass. The DECISIONS (flow, watchdog, grant, presentation)
// are pure modules with their own behavioural tests; this file only proves the
// screens are wired to them.

test('the television never holds a party token or a guest identity', () => {
  // The public party surface is a GUEST capability. A display must never
  // address it, and the native client has no code that could.
  for (const file of [app, screen, slideshow, surface]) {
    assert.doesNotMatch(file, /\/api\/party\//);
    // No guest join, ever: a display that became a participant would inflate
    // the very count the stage is showing.
    assert.doesNotMatch(file, /game\/join|joinPartyGame/);
  }
  assert.doesNotMatch(tvApi, /\/api\/party\/\{?token/);

  // The mint takes no arguments: the party comes from the device's own
  // assignment, resolved server-side, so there is nothing here to point
  // somewhere else.
  assert.match(tvApi, /mintPartyDisplayGrant\(signal\?: AbortSignal\)/);
  assert.match(tvApi, /tvPost<TvPartyDisplayGrant>\('\/api\/tv\/party-display\/grant'/);
});

test('the grant is never persisted, logged, or put anywhere but the fragment', () => {
  // It lives in memory and in a URL fragment, and a reload legitimately has
  // none — the shell mints another rather than the page inventing one.
  for (const file of [app, screen, tvApi, grantModule]) {
    assert.doesNotMatch(file, /AsyncStorage[\s\S]{0,200}grant/i);
    assert.doesNotMatch(file, /localStorage|sessionStorage/);
  }
  assert.match(grantModule, /#grant=\$\{encodeURIComponent\(grant\)\}/);
  assert.match(screen, /source=\{\{ uri: stageUrl\(baseUrl, grant\.token\) \}\}/);
  // The debug log names events, never the credential.
  assert.doesNotMatch(screen, /tvDebug\([^)]*(token|minted\.grant|stageUrl)/);
  assert.doesNotMatch(app, /tvDebug\([^)]*(cookie|grant)/i);
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

test('every way a renderer can fail reaches the watchdog', () => {
  assert.match(screen, /onRenderProcessGone=\{\(\) => dispatch\(\{ type: 'renderer-gone' \}\)\}/);
  assert.match(screen, /onContentProcessDidTerminate=\{\(\) => dispatch\(\{ type: 'renderer-gone' \}\)\}/);
  // A document that never arrived (frontend down, proxy error) is a failed
  // renderer too, not a page to leave on screen.
  assert.match(screen, /onError=\{\(\) => dispatch\(\{ type: 'load-error' \}\)\}/);
  assert.match(screen, /onHttpError=\{\(\) => dispatch\(\{ type: 'load-error' \}\)\}/);
  // The shell's own clock drives it, and it is paused behind HOME.
  assert.match(screen, /setInterval\(\(\) => dispatch\(\{ type: 'tick' \}\), 1_000\)/);
  assert.match(screen, /dispatch\(\{ type: 'resume' \}\)/);
  // A new grant is a new renderer; the key is the watchdog's generation.
  assert.match(screen, /dispatch\(\{ type: 'start' \}\)/);
  assert.match(screen, /key=\{`stage-\$\{watchdog\.generation\}`\}/);
  // The renderer is mounted only while the watchdog says so: a probe during
  // the fallback is a REAL mount.
  assert.match(screen, /grant\.kind === 'ready' && watchdog\.mounted && \(/);
});

test('the heartbeat and the capability are two different signals', () => {
  // A living page whose grant was refused is not silent, so the watchdog would
  // never notice it. The refusal has its own path: re-mint.
  assert.match(screen, /case 'auth-failed':\s*onAuthFailed\(\);/);
  assert.match(screen, /case 'heartbeat':\s*dispatch\(\{ type: 'heartbeat' \}\);/);
  // The grant is renewed before it lapses, from the server's duration.
  assert.match(screen, /renewDelayMs\(minted, Date\.now\(\)\)/);
  // A failed mint is classified and retried; it never becomes a final state.
  assert.match(screen, /classifyMintFailure\(/);
  assert.match(screen, /mintRetryDelayMs\(failure, mintFailuresRef\.current\+\+\)/);
  // A renewal that fails transiently keeps the working stage it already has.
  assert.match(screen, /current\.kind === 'ready' && failure === 'transient'\s*\?\s*current/);
  // One pending retry answers every refusal reported meanwhile.
  assert.match(screen,
    /if \(mintingRef\.current \|\| retryPendingRef\.current \|\| authRetryRef\.current !== null\) return;/);
});

test('the stage is covered natively until it has proved itself', () => {
  // No white flash, no black flash, no browser error, no frame from before:
  // the native surface stays over the WebView until the renderer is alive AND
  // has drawn a real snapshot.
  assert.match(screen, /const visible = grant\.kind === 'ready' && !refused && rendererVisible\(watchdog\)/);
  // A refused grant takes the page off screen at once: the server stops
  // honouring a grant when the game hands the screen back, and the page's own
  // "unavailable" card must never be what the room sees in between.
  assert.match(screen, /const onAuthFailed = useCallback\(\(\) => \{\s*setRefused\(true\);/);
  assert.match(screen, /\{!visible && \(\s*<PartyNativeSurface/);
  // And the WebView's own ground is the stage's colour, not Android's white.
  assert.match(screen, /stage: \{ flex: 1, backgroundColor: PARTY_SURFACE_BACKGROUND \}/);
});

test('the display holds the screen through the EXISTING keep-awake, not a second one', () => {
  // One module answers "hold the screen" for the whole app. A second wake lock
  // would be the second authority wakePolicy exists to prevent.
  assert.match(screen,
    /useScreenAwake\(shouldKeepPartyDisplayAwake\(\{ hostActive, showing: visible, presentationActive \}\)\)/);
  assert.match(wakePolicy, /export function shouldKeepPartyDisplayAwake/);
  for (const file of [screen, slideshow]) {
    assert.doesNotMatch(file, /activateKeepAwake|deactivateKeepAwake/);
  }
});

test('the control plane is one brisk read, whatever the television is showing', () => {
  // The takeover bound: every five seconds while paired and in the foreground,
  // general included — a general television is the one waiting to be taken over.
  assert.match(app, /setInterval\(read, CONTROL_POLL_MS\)/);
  assert.doesNotMatch(app, /partyRate|60_000 : |5_000 : 60_000/);
  assert.match(app, /if \(!sessionLive \|\| !hostActive\) return;/);
  // The heartbeat is the same read once a minute, so presence is not stale and
  // there is no write every five seconds.
  assert.match(app, /beat \? heartbeatTvSession\(\) : getTvSession\(\)/);
  assert.match(tvApi, /tvPost<TvSessionStatus>\('\/api\/tv\/session\/heartbeat'/);
  // And the same read is the session check.
  assert.match(app, /err\.status === 401\) onSessionInvalid\(\)/);
});

test('the first read decides the first screen, once the association is known complete', () => {
  assert.match(app, /const assignment = toAssignmentView\(session\.assignment\);/);
  // The Personal Area status is asked BEFORE the first screen is chosen, and
  // the reducer's admission decides: an incomplete association never enters a
  // party (flow.test.ts proves it).
  assert.match(app, /const check = \(\) => \{\s*getTvPersonalStatus\(\)/);
  assert.match(app,
    /for \(const event of admissionEvents\(assignment, status\.pinConfigured\)\) dispatch\(event\);/);
  assert.doesNotMatch(app, /dispatch\(\{ type: 'SESSION_READY', assignment \}\)/);
  // Both doors — relaunch and completed pairing — go through it.
  assert.match(app, /if \(!cancelled\) admit\(session\);/);
  assert.match(app, /const onPaired = useCallback\(\(session: TvSessionStatus\) => \{\s*admit\(session\);/);
  // A status check that got no answer is retried; only a 401 unpairs.
  assert.match(app, /setTimeout\(check, backoffMs\(attempt\+\+\)\)/);
  // A television that boots before its network keeps its pairing: only a 401
  // unpairs at startup.
  assert.match(app, /err\.status === 401\) \{\s*dispatch\(\{ type: 'SESSION_INVALID' \}\);/);
  assert.match(app, /retry = setTimeout\(validate, backoffMs\(attempt\+\+\)\)/);
});

test('each assigned presentation is its own mount, keyed by the party', () => {
  assert.match(app, /<PartySlideshowScreen\s+key=\{flow\.party\.key\}/);
  assert.match(app, /<PartyDisplayScreen\s+key=\{flow\.party\.key\}/);
  // BACK at the root of an assigned party closes the app; nothing local takes
  // it back to general.
  assert.match(app, /flow\.name !== 'partyGame' && flow\.name !== 'partyUnavailable'/);
  assert.match(app, /onExit=\{exitApp\}/);
});

test('the assigned slideshow is the existing viewer, not a second slideshow', () => {
  assert.match(slideshow, /<ViewerScreen/);
  assert.match(slideshow, /autoPlay/);
  // Timing, playback, greetings, challenge holds and live refresh are the
  // viewer's. The adapter re-implements none of them.
  assert.doesNotMatch(slideshow,
    /photoSlideMs|listTvPartyMessages|getTvPartyPlayback|advanceTvPartyBoundary|TvVideoPlayer|SlideImage/);
  // It fails closed on a vanished album and waits on an empty one.
  assert.match(slideshow, /onGone=\{onGone\}/);
  assert.match(slideshow, /const onEmpty = useCallback\(\(\) => setLoad\(\{ kind: 'empty' \}\), \[\]\)/);
  assert.match(slideshow, /onEmpty=\{onEmpty\}/);
});

test('there is no second Party Game state machine on the television', () => {
  // The shell knows about an ASSIGNMENT and a PRESENTATION. It does not know
  // what a phase is, when voting opens, or which challenge is current — those
  // stay server-side and reach the room through the canonical web stage.
  for (const file of [app, screen, slideshow, grantModule]) {
    assert.doesNotMatch(file, /voting_open|challenge_reveal|challenge_active|voting_closed|reveal_result|'finished'|'lobby'/);
    assert.doesNotMatch(file, /stageScene|PartyChallengeCard/);
  }
});

test('no spike harness reached the product', () => {
  for (const file of [app, screen, slideshow]) {
    assert.doesNotMatch(file, /WebViewSpike|webviewSpike|Replica stage|memory stress/i);
  }
});
