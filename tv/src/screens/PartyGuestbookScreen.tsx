import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  AccessibilityInfo,
  Animated,
  Image,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type LayoutChangeEvent,
  type TextStyle,
  type ViewStyle,
} from 'react-native';
import { getTvGuestbook, type TvGuestbookMemory } from '../api/tv';
import { ApiError } from '../api/client';
import { useI18n } from '../i18n';
import { useHostActive } from '../lib/useHostActive';
import { useScreenAwake } from '../lib/useScreenAwake';
import { backoffMs } from '../lib/assignmentView';
import {
  GUESTBOOK_TRANSITION_MS,
  GUESTBOOK_TV_POLL_MS,
  guestbookDensity,
  guestbookDwellMs,
  guestbookFrameAspect,
  guestbookPhotoPlacement,
  guestbookTemplateKey,
  guestbookTvFontPx,
  guestbookTvPhotoBox,
  guestbookTvShrink,
  guestbookTvStageSize,
  guestbookTvWordsHeight,
  guestbookTvWordsWidth,
  nextGuestbookMemory,
  reconcileGuestbookDeck,
} from '../lib/partyGuestbook';
import { shouldKeepPartyGuestbookAwake } from '../video/wakePolicy';
import { useTvMedia } from '../media/useTvMedia';
import { PartyNativeSurface, PARTY_SURFACE_BACKGROUND } from '../components/PartyNativeSurface';
import { tvDebug } from '../debug';

// THE PARTY'S GUEST BOOK, on this television — while the regia has put it on
// the screen. The app's counterpart of the browser's TvAssignedGuestbook +
// PartyGuestbookTvStage, drawn natively from the SAME rules
// (lib/partyGuestbook, held to the browser's by the parity test): one memory at
// a time, the photograph framed as its author framed it, the dedication whole,
// the signature, in the design the memory was published with.
//
// AUTHORISED BY THIS TELEVISION'S SESSION, AND NOTHING ELSE. No game grant is
// minted, no party or guest token exists here: the deck and every photograph
// come from /api/tv/party/guestbook, which answers only while the server's
// presentation for this television IS the guest book. A 404 means the party
// moved on, and the control plane is asked at once.
//
// THE DECK FOLLOWS THE BOOK. One reading at a time, never overlapping; a
// failed one keeps the last frame and retries with backoff; a fresh one is the
// new truth — new memories join, hidden ones leave, and the one being read is
// never pulled away. Behind HOME nothing is read and nothing rotates; the
// first read on return is immediate. An empty book hands the screen back.
//
// NOTHING IS CUT. The words start at their density tier and are measured: while
// they do not fit their column they are drawn smaller, until they do.

interface Props {
  readonly albumName: string | null;
  readonly onSessionInvalid: () => void;
  /** Ask the control plane for an immediate re-read: the book says the party moved. */
  readonly onRequestAssignment: () => void;
}


export function PartyGuestbookScreen({ albumName, onSessionInvalid, onRequestAssignment }: Props) {
  const { t } = useI18n();
  const hostActive = useHostActive();
  const [entries, setEntries] = useState<TvGuestbookMemory[] | null>(null);
  const callbacks = useRef({ onSessionInvalid, onRequestAssignment });
  callbacks.current = { onSessionInvalid, onRequestAssignment };

  // ONE reading of the book at a time. Restarted by a return to the
  // foreground, which reads at once; aborted by unmount.
  useEffect(() => {
    if (!hostActive) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | null = null;
    let failures = 0;
    const read = () => {
      controller = new AbortController();
      const current = controller;
      getTvGuestbook(current.signal)
        .then((book) => {
          if (cancelled) return;
          failures = 0;
          setEntries(book.entries);
          if (book.entries.length === 0) callbacks.current.onRequestAssignment();
          timer = setTimeout(read, GUESTBOOK_TV_POLL_MS);
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          const status = error instanceof ApiError ? error.status : null;
          if (status === 401) {
            callbacks.current.onSessionInvalid();
            return;
          }
          if (status === 404) {
            tvDebug('guestbook', 'moved');
            callbacks.current.onRequestAssignment();
          }
          // The last frame stays; try again.
          timer = setTimeout(read, backoffMs(failures++));
        });
    };
    read();
    return () => {
      cancelled = true;
      controller?.abort();
      if (timer) clearTimeout(timer);
    };
  }, [hostActive]);

  const current = useGuestbookDeck(entries ?? [], hostActive);
  useScreenAwake(shouldKeepPartyGuestbookAwake({ hostActive, showing: current !== null }));

  if (current === null) {
    return (
      <PartyNativeSurface
        albumName={albumName}
        message={t('partyGuestbook.loading')}
        busy={entries === null}
        testID="party-guestbook-waiting"
      />
    );
  }

  return (
    <View style={styles.stage} accessibilityLabel={t('partyGuestbook.title')} testID="party-guestbook">
      <MemoryOnStage key={current.id} memory={current} />
    </View>
  );
}

/** Which memory is on screen, following the book as it changes. */
function useGuestbookDeck(entries: readonly TvGuestbookMemory[], rotating: boolean): TvGuestbookMemory | null {
  const ids = useMemo(() => entries.map((e) => e.id), [entries]);
  const [currentId, setCurrentId] = useState<string | null>(null);
  const previousIds = useRef<readonly string[]>([]);
  const idsRef = useRef(ids);
  idsRef.current = ids;

  useEffect(() => {
    // Captured NOW: the updater runs later, after the ref holds the new order,
    // and "the next one after the memory that left" needs the old one.
    const previous = previousIds.current;
    previousIds.current = ids;
    setCurrentId((current) => reconcileGuestbookDeck(previous, current, ids));
  }, [ids]);

  const current = entries.find((e) => e.id === currentId) ?? null;
  const body = current?.body ?? '';
  const many = ids.length > 1;
  useEffect(() => {
    if (!rotating || !many || currentId === null) return;
    const timer = setTimeout(
      () => setCurrentId((id) => nextGuestbookMemory(idsRef.current, id)),
      guestbookDwellMs(body),
    );
    return () => clearTimeout(timer);
  }, [currentId, many, body, rotating]);

  return current;
}

function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);
  useEffect(() => {
    let alive = true;
    AccessibilityInfo.isReduceMotionEnabled()
      .then((value) => { if (alive) setReduced(value); })
      .catch(() => { /* the default motion is sober anyway */ });
    const sub = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduced);
    return () => {
      alive = false;
      sub.remove();
    };
  }, []);
  return reduced;
}

/** One memory, composed for this screen, fading in once. */
function MemoryOnStage({ memory }: { memory: TvGuestbookMemory }) {
  const { t } = useI18n();
  const window = useWindowDimensions();
  const reduced = useReducedMotion();
  const { width: stageW, height: stageH } = guestbookTvStageSize(window.width, window.height);

  const density = guestbookDensity(memory.body);
  const photoAspect = memory.media.width > 0 && memory.media.height > 0
    ? memory.media.width / memory.media.height
    : 1;
  const design = guestbookTemplateKey(memory.template.key, memory.template.version);
  const frameAspect = guestbookFrameAspect(memory.template.key, memory.template.version, photoAspect);
  const box = guestbookTvPhotoBox(stageW, stageH, frameAspect, density);
  const placement = guestbookPhotoPlacement(photoAspect, frameAspect, memory.media.crop);

  // The measured fit.
  const baseFont = guestbookTvFontPx(stageH, density);
  const [fontPx, setFontPx] = useState(baseFont);
  const [column, setColumn] = useState(0);
  useEffect(() => { setFontPx(baseFont); }, [baseFont, stageW, stageH]);
  const onColumn = useCallback((e: LayoutChangeEvent) => setColumn(e.nativeEvent.layout.height), []);
  const padV = Math.round(fontPx * 1.1);
  const padH = Math.round(fontPx * 1.3);
  const onWords = useCallback((e: LayoutChangeEvent) => {
    const height = e.nativeEvent.layout.height;
    const room = column - 2 * padV;
    if (room > 0 && height > room + 1) {
      setFontPx((px) => guestbookTvShrink(px) ?? px);
    }
  }, [column, padV]);

  const opacity = useRef(new Animated.Value(reduced ? 1 : 0)).current;
  useEffect(() => {
    if (reduced) { opacity.setValue(1); return; }
    Animated.timing(opacity, { toValue: 1, duration: GUESTBOOK_TRANSITION_MS, useNativeDriver: true }).start();
  }, [opacity, reduced]);

  const look = TEMPLATE_LOOK[design.split('@')[0]] ?? TEMPLATE_LOOK.nubarca;

  return (
    <Animated.View style={[styles.memory, look.card, { opacity }]} testID="party-guestbook-memory">
      <View style={[styles.photo, look.photo, { width: box.width, height: box.height }]}>
        <MemoryPhoto
          path={memory.media.url}
          label={t('partyGuestbook.photo', { name: memory.authorDisplayName })}
          style={{
            position: 'absolute',
            width: placement.width * box.width,
            height: placement.height * box.height,
            left: placement.left * box.width,
            top: placement.top * box.height,
          }}
        />
      </View>
      {look.ornament}
      <View
        style={[styles.column, look.column, {
          width: guestbookTvWordsWidth(stageW, box.width, density),
          height: guestbookTvWordsHeight(stageH, box.height, density),
          paddingVertical: padV,
          paddingHorizontal: padH,
        }]}
        onLayout={onColumn}
      >
        <View onLayout={onWords} style={[styles.words, look.words]}>
          {look.quote ? (
            <Text style={[look.quote, { fontSize: fontPx * 2.4, lineHeight: fontPx * 2.2 }]}>{'“'}</Text>
          ) : null}
          {/* TEXT, always: the dedication keeps every line break it was
              written with, and is never interpreted as anything but text. */}
          <Text style={[styles.body, look.body, { fontSize: fontPx, lineHeight: Math.round(fontPx * 1.4) }]}>
            {memory.body}
          </Text>
          <View style={[styles.signature, look.signature]}>
            {look.dash ? <View style={[look.dash, { width: fontPx * 1.4 }]} /> : null}
            <Text style={[look.author, { fontSize: Math.round(fontPx * look.authorScale) }]}>
              {look.uppercaseAuthor ? memory.authorDisplayName.toUpperCase() : `— ${memory.authorDisplayName}`}
            </Text>
          </View>
        </View>
      </View>
    </Animated.View>
  );
}

function MemoryPhoto({ path, label, style }: { path: string; label: string; style: ViewStyle }) {
  const { uri, state, markFailed } = useTvMedia(path);
  if (!uri || state !== 'ready') return null;
  return (
    <Image
      source={{ uri }}
      style={style as never}
      resizeMode="cover"
      onError={markFailed}
      accessibilityLabel={label}
    />
  );
}

// ── The four designs, natively ─────────────────────────────────────────────
//
// The web's stylesheet is the reference (PartyGuestbook.css): the same paper,
// ink, frame and signature treatment per design, in a row instead of a column.
// What a design MEANS — its frame, its crop — comes from lib/partyGuestbook.

const BRAND = {
  midnight: '#0a0f1a',
  deepBlue: '#0f1e3a',
  electricBlue: '#1565ff',
  cyan: '#00d4ff',
  violet: '#9a6cff',
  cloud: '#f5f7fb',
};

interface Look {
  card: ViewStyle;
  /** The photo well: what shows beside a photograph its author zoomed out (the web card's .guestbook-memory-photo). */
  photo: ViewStyle;
  column: ViewStyle;
  words: ViewStyle;
  body: TextStyle;
  signature: ViewStyle;
  author: TextStyle;
  authorScale: number;
  dash?: ViewStyle;
  quote?: TextStyle;
  uppercaseAuthor?: boolean;
  ornament?: ReactNode;
}

const TEMPLATE_LOOK: Record<string, Look> = {
  nubarca: {
    card: { backgroundColor: BRAND.deepBlue, borderRadius: 20, borderWidth: 1, borderColor: 'rgba(245,247,251,0.12)' },
    photo: { backgroundColor: 'rgba(10,15,26,0.7)' },
    column: {},
    words: {},
    body: { color: BRAND.cloud },
    signature: { flexDirection: 'row', alignItems: 'center', gap: 10 },
    author: { color: 'rgba(245,247,251,0.72)' },
    authorScale: 0.75,
    dash: { height: 2, borderRadius: 1, backgroundColor: BRAND.cyan },
  },
  polaroid: {
    card: { backgroundColor: '#f6f4ef', borderRadius: 6, paddingTop: 14, paddingBottom: 14, paddingLeft: 14 },
    photo: { backgroundColor: '#d9d6cf', borderRadius: 2 },
    column: {},
    words: {},
    body: { color: '#1d2433' },
    signature: { alignSelf: 'flex-end' },
    author: { color: '#5a6273', fontWeight: '600' },
    authorScale: 0.75,
  },
  editorial: {
    card: { backgroundColor: BRAND.cloud, borderRadius: 8 },
    photo: { backgroundColor: 'rgba(10,15,26,0.7)' },
    column: {},
    words: {},
    body: { color: BRAND.midnight, fontWeight: '500' },
    signature: { borderTopWidth: 1, borderTopColor: 'rgba(10,15,26,0.16)', paddingTop: 10 },
    author: { color: '#4c5568', fontWeight: '600', letterSpacing: 3 },
    authorScale: 0.6,
    quote: { color: BRAND.electricBlue, fontWeight: '700' },
    uppercaseAuthor: true,
  },
  celebration: {
    card: { backgroundColor: BRAND.deepBlue, borderRadius: 22, borderWidth: 3, borderColor: BRAND.violet },
    photo: { backgroundColor: 'rgba(10,15,26,0.7)', borderTopLeftRadius: 19, borderBottomLeftRadius: 19 },
    column: { alignItems: 'center' },
    words: { alignItems: 'center' },
    body: { color: BRAND.cloud, textAlign: 'center' },
    signature: {
      borderWidth: 1, borderColor: 'rgba(154,108,255,0.55)', borderRadius: 999,
      paddingHorizontal: 16, paddingVertical: 6,
    },
    author: { color: BRAND.cloud, fontWeight: '600' },
    authorScale: 0.75,
    ornament: (
      <View pointerEvents="none" style={{ position: 'absolute', top: 12, right: 12, width: 56, height: 56 }}>
        {[[0, 0, BRAND.cyan, 6], [20, 8, '#ffd166', 5], [8, 26, BRAND.violet, 4], [34, 30, BRAND.cyan, 4],
          [42, 4, BRAND.violet, 5]].map(([x, y, color, size], i) => (
          <View
            key={i}
            style={{
              position: 'absolute', left: x as number, top: y as number,
              width: size as number, height: size as number, borderRadius: 99,
              backgroundColor: color as string, opacity: 0.9,
            }}
          />
        ))}
      </View>
    ),
  },
};

const styles = StyleSheet.create({
  stage: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: PARTY_SURFACE_BACKGROUND,
  },
  memory: {
    flexDirection: 'row',
    alignItems: 'center',
    overflow: 'hidden',
  },
  photo: {
    overflow: 'hidden',
  },
  column: {
    justifyContent: 'center',
  },
  words: {
    gap: 14,
  },
  body: {},
  signature: {},
});
