import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const viewer = source('../screens/ViewerScreen.tsx');
const ribbon = source('../components/PartyMessageRibbon.tsx');
const heroCard = source('../components/PartyHeroMessage.tsx');
const tvApi = source('../api/tv.ts');

// Structural guarantees about the party MESSAGE surfaces. These are the
// properties that cannot be reached from the pure policy module — that the
// screen actually DELEGATES to it, that the message feed stays out of the media
// contract, and that nothing shortens a video to make room for a card.
//
// Source-text assertions, in the same style as tvMediaGridWiring: comments are
// stripped before matching (see testing/sourceText), so the prose in these files
// cannot make a negative assertion pass.

test('the message feed is separate from the media carousel', () => {
  // TvAlbumItem stays exactly `image | video`, which is what lets an older TV
  // APK keep working: it never learns this endpoint exists.
  assert.match(tvApi, /mediaType:\s*'image'\s*\|\s*'video'/);
  assert.doesNotMatch(tvApi, /mediaType:[^;]*'message'/);
  // Messages have their own type and their own route.
  assert.match(tvApi, /interface TvPartyMessage\b/);
  assert.match(tvApi, /\/party-messages/);
  // And they are never folded into the album item list.
  assert.doesNotMatch(tvApi, /interface TvAlbumItems[\s\S]{0,400}messages:/);
});

test('the viewer polls messages on their own timer, not the media one', () => {
  assert.match(viewer, /listTvPartyMessages\(/);
  assert.match(viewer, /setInterval\(poll,\s*MESSAGES_POLL_MS\)/);
  // The existing 15s media poll is untouched — a message arriving must not
  // reload the slideshow, and a media refresh must not reset the ribbon.
  assert.match(viewer, /const PARTY_ITEMS_POLL_MS = 15_000;/);
  assert.match(viewer, /setInterval\(refresh,\s*PARTY_ITEMS_POLL_MS\)/);
});

test('a message refresh never moves the media slideshow', () => {
  // The message effect may touch the feed, the ribbon and the Hero, and
  // nothing else. If it ever calls setIndex or setItems, a guest typing on
  // their phone would jump the photograph on the wall.
  const effect = viewer.slice(
    viewer.indexOf('listTvPartyMessages('),
    viewer.indexOf('MESSAGES_POLL_MS)'),
  );
  assert.ok(effect.length > 0);
  assert.doesNotMatch(effect, /setIndex\(/);
  assert.doesNotMatch(effect, /setItems\(/);
  assert.doesNotMatch(effect, /setPlaying\(/);
});

test('the ribbon keeps its place across a refresh by message id', () => {
  assert.match(viewer, /remapRibbonIndex\(/);
  assert.match(viewer, /ribbonMessageIdRef/);
});

test('the ribbon is driven by the tested visibility policy, not ad-hoc conditions', () => {
  assert.match(viewer, /ribbonVisible\(\{/);
  assert.match(viewer, /overlayVisible,/);
  assert.match(viewer, /ribbonRotating\(\{/);
  assert.match(viewer, /setInterval\([\s\S]{0,120}RIBBON_ROTATE_MS\)/);
});

test('every automatic advance goes through the Hero boundary policy', () => {
  assert.match(viewer, /const handleMediaBoundary = useCallback/);
  assert.match(viewer, /onMediaBoundary\(\{/);
  // Photo dwell, video end and video cap all route through it.
  assert.match(viewer, /setTimeout\(handleMediaBoundary,\s*photoMs\)/);
  assert.match(viewer, /onEnded=\{handleMediaBoundary\}/);
  assert.match(viewer, /onCapReached=\{handleMediaBoundary\}/);
});

test('nothing truncates a video to make room for a Hero', () => {
  // A video's only automatic boundaries remain its natural end and the owner's
  // configured cap. No message-driven timer reaches the player: the ONLY thing
  // a Hero changes about it is the controlled play intent, which is the
  // player's existing authority rather than a second one.
  const player = viewer.slice(viewer.indexOf('<TvVideoPlayer'), viewer.indexOf('controlsRef='));
  assert.ok(player.length > 0);
  assert.doesNotMatch(player, /HERO_DURATION_MS|setHero\(|setTimeout/);
  assert.doesNotMatch(player, /videoControlsRef/);
});

test('a video does not keep playing underneath a Hero raised at its cap', () => {
  // The cap is a boundary the slideshow OBSERVES; it does not stop the player.
  // Without withholding the play intent, a card raised at the cap would sit
  // over a clip still running, audio included.
  assert.match(viewer, /playing: playing && hero === null/);
  // And the wall then moves to the NEXT media, so the paused clip is never
  // resumed underneath.
  assert.match(viewer, /if \(settled\.advance\) goNext\(\);/);
});

test('a Hero holds the current media rather than moving the index', () => {
  // The boundary handler returns WITHOUT advancing when it shows a card...
  assert.match(viewer, /setHero\(pick\.message\);\s*return;/);
  // ...and the photo dwell timer is suspended underneath it, so the media does
  // not advance out from behind the card.
  assert.match(viewer, /hero === null;/);
});

test('a deferred boundary is explicit, and the Hero timer does not settle it', () => {
  // Tracked as its own flag rather than inferred from `hero !== null`: a video
  // that has already ended can produce no further boundary, so a card withdrawn
  // early would otherwise strand the wall on its last frame.
  assert.match(viewer, /boundaryDebtRef/);
  assert.match(viewer, /boundaryDebtRef\.current = deferBoundary\(\);\s*setHero\(pick\.message\)/);
  // The card's own timer only takes the card down.
  assert.match(viewer, /setTimeout\(\(\) => setHero\(null\),\s*HERO_DURATION_MS\)/);
});

test('exactly one place settles a deferred boundary', () => {
  // The whole exactly-once guarantee rests on there being ONE consumer, so a
  // card timing out in the same tick the poll withdraws it cannot advance
  // twice. Every other site may only clear the flag, never spend it.
  const settles = viewer.match(/settleBoundary\(/g) ?? [];
  assert.equal(settles.length, 1);
  // It writes the returned ledger back BEFORE acting on it, so the debt is
  // cleared whether or not the advance happens.
  assert.match(
    viewer,
    /boundaryDebtRef\.current = settled\.debt;\s*if \(settled\.advance\) goNext\(\);/,
  );
  // No other site may advance off the ledger; they may only clear it.
  assert.doesNotMatch(viewer, /discardBoundary\(\);[\s\S]{0,80}goNext\(\)/);
});

test('the Hero comes down when the conditions that allowed it stop holding', () => {
  // A viewer looking at something else discards the debt; a merely PAUSED wall
  // keeps it, because a finished video cannot raise the boundary again.
  assert.match(
    viewer,
    /if \(faceFilter !== null \|\| !slideshowMode\) \{\s*boundaryDebtRef\.current = discardBoundary\(\);\s*setHero\(null\);\s*return;\s*\}\s*if \(!playing\) setHero\(null\);/,
  );
  // The consumer refuses to move a paused or manual wall, so the retained debt
  // is settled only when playback resumes.
  assert.match(viewer, /heroVisible: hero !== null,\s*slideshowMode,\s*playing,/);
  // A Hero the server stops sending leaves on the next poll rather than
  // serving out its six seconds — and that path settles the boundary, because
  // it is a withdrawal of content, not a change of viewing intent.
  assert.match(viewer, /!feed\.messages\.some\(\(m\) => m\.id === current\.id && m\.isHero\)/);
  const poll = viewer.slice(
    viewer.indexOf('listTvPartyMessages('), viewer.indexOf('MESSAGES_POLL_MS)'));
  assert.doesNotMatch(poll, /discardBoundary/);
});

test('manual navigation takes over from a Hero rather than queueing behind it', () => {
  assert.match(viewer, /case 'next': dismissHeroForManualNavigation\(\); goNext\(\); break;/);
  assert.match(viewer, /case 'prev': dismissHeroForManualNavigation\(\); goPrev\(\); break;/);
  // It discards the debt: the person has just chosen where to be, and settling
  // an old one on top of that would skip the item they asked for.
  const helper = viewer.slice(
    viewer.indexOf('const dismissHeroForManualNavigation'),
    viewer.indexOf('const onTVEvent'),
  );
  assert.ok(helper.length > 0);
  assert.match(helper, /discardBoundary\(\)/);
  assert.doesNotMatch(helper, /goNext\(\)/);
});

test('the message surfaces add no focusable controls and no new remote handling', () => {
  for (const [name, component] of [['ribbon', ribbon], ['hero', heroCard]] as const) {
    assert.doesNotMatch(component, /focusable|hasTVPreferredFocus|onPress|Pressable|TouchableOpacity/, name);
    assert.doesNotMatch(component, /useTVEventHandler|BackHandler/, name);
  }
  // The remote mapping itself knows nothing about messages...
  assert.doesNotMatch(source('../video/remoteMap.ts'), /message|ribbon|hero/i);
  // ...and no key press reaches the ribbon or the Hero: the only thing that
  // shows or hides either is the automatic policy.
  const remoteHandling = viewer.slice(
    viewer.indexOf('useTVEventHandler('),
    viewer.indexOf("BackHandler.addEventListener('hardwareBackPress'"),
  );
  assert.ok(remoteHandling.length > 0);
  assert.doesNotMatch(remoteHandling, /setHero\(|setRibbonIndex\(|setMessages\(/);
});

test('message text is rendered as text, and the band never scrolls', () => {
  for (const [name, component] of [['ribbon', ribbon], ['hero', heroCard]] as const) {
    assert.match(component, /<Text/, name);
    // No marquee, no ticker, no auto-scrolling of any kind.
    assert.doesNotMatch(component, /ScrollView|Marquee|marquee|scrollTo|translateX/, name);
    // Never handed to anything that could interpret it.
    assert.doesNotMatch(component, /dangerouslySetInnerHTML|WebView|renderHtml|Markdown/, name);
  }
  // Two lines at most, then an ellipsis — and the font does not shrink, because
  // text too small to read from a sofa is worse than text that is cut.
  assert.match(ribbon, /numberOfLines=\{2\}/);
  assert.match(ribbon, /ellipsizeMode="tail"/);
  assert.doesNotMatch(ribbon, /adjustsFontSizeToFit|minimumFontScale/);
});

test('both surfaces sit inside the shared overscan safe area', () => {
  // The 10-foot layout constants already exist; this feature reuses them rather
  // than introducing a second system.
  for (const [name, component] of [['ribbon', ribbon], ['hero', heroCard]] as const) {
    assert.match(component, /from '\.\.\/theme'/, name);
    assert.match(component, /overscan\(width, height\)/, name);
  }
  // The band is anchored to the bottom inset, on both axes.
  assert.match(ribbon, /left: inset\.x, right: inset\.x, bottom: inset\.y/);
});

test('an unsigned message is labelled, not left blank', () => {
  for (const [name, component] of [['ribbon', ribbon], ['hero', heroCard]] as const) {
    assert.match(component, /displayName \?\? t\('partyMessages\.anonymous'\)/, name);
  }
});
