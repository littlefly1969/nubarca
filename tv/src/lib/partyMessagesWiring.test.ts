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
  // configured cap. There is no message-driven timer anywhere near the player.
  const player = viewer.slice(viewer.indexOf('<TvVideoPlayer'), viewer.indexOf('controlsRef='));
  assert.ok(player.length > 0);
  assert.doesNotMatch(player, /HERO_DURATION_MS|setHero|hero/);
  // And the Hero timer only ever hands the wall back; it never stops a player.
  assert.match(viewer, /setTimeout\(\(\) => \{\s*setHero\(null\);\s*goNext\(\);\s*\},\s*HERO_DURATION_MS\)/);
});

test('a Hero holds the current media rather than moving the index', () => {
  // The boundary handler returns WITHOUT advancing when it shows a card...
  assert.match(viewer, /setHero\(pick\.message\);\s*return;/);
  // ...and the photo dwell timer is suspended underneath it, so the media does
  // not advance out from behind the card.
  assert.match(viewer, /hero === null;/);
});

test('the Hero comes down when the conditions that allowed it stop holding', () => {
  assert.match(viewer, /faceFilter !== null \|\| !playing \|\| !slideshowMode\) setHero\(null\)/);
  // A Hero the server stops sending leaves on the next poll rather than
  // serving out its six seconds.
  assert.match(viewer, /!feed\.messages\.some\(\(m\) => m\.id === current\.id && m\.isHero\)/);
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
