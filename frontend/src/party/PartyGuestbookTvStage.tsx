import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { PartyGuestbookEntry } from '@nubarca/api-client';
import { PartyGuestbookMemoryCard } from '../components/PartyGuestbookMemoryCard';
import {
  guestbookDensity,
  guestbookDwellMs,
  guestbookFrameAspect,
  guestbookTvFontPx,
  guestbookTvPhotoBox,
  guestbookTvShrink,
  guestbookTvStageSize,
  guestbookTvWordsHeight,
  guestbookTvWordsWidth,
  nextGuestbookMemory,
  reconcileGuestbookDeck,
} from '../tv/semantics/partyGuestbook';
import { useI18n } from '../i18n';
import './PartyGuestbookTvStage.css';

// THE GUEST BOOK ON THE TELEVISION — one memory at a time, whole.
//
// It is handed the book (visible memories, in the book's order) and nothing
// else: it does not fetch, poll or know which television it is on. The memory
// is drawn by the SAME card every guest-book surface uses, in its television
// composition, so the template, the crop, the line breaks and the signature
// are the book's own and not a second interpretation of them.
//
// Three promises, each kept here rather than hoped for:
//
//   NOTHING IS CUT. The dedication's size starts from its density tier and is
//   then measured: while the words do not fit their column they are drawn
//   smaller, until they do. No ellipsis, no clamp, no hidden overflow.
//
//   A POLL NEVER PULLS A MEMORY AWAY. A new reading of the book keeps the one
//   on screen if it is still in the book; one that left is replaced by the
//   next in the room's order; new memories simply join the round.
//
//   ONE MEMORY STAYS STILL. With a single memory there is no timer, no fade
//   and nothing re-drawn: the picture on the wall does not blink.

function useStageSize(): { width: number; height: number } {
  const read = () => guestbookTvStageSize(window.innerWidth, window.innerHeight);
  const [size, setSize] = useState(read);
  useEffect(() => {
    const onResize = () => setSize(read());
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);
  return size;
}

/** Which memory is on screen, following the book as it changes. */
function useGuestbookDeck(entries: readonly PartyGuestbookEntry[]): PartyGuestbookEntry | null {
  const ids = useMemo(() => entries.map((e) => e.id), [entries]);
  const [currentId, setCurrentId] = useState<string | null>(() => ids[0] ?? null);
  const previousIds = useRef<readonly string[]>(ids);

  // A fresh reading of the book: keep, move on, or start — never restart.
  useEffect(() => {
    // Captured NOW: the updater runs later, after the ref holds the new order,
    // and "the next one after the memory that left" needs the old one.
    const previous = previousIds.current;
    previousIds.current = ids;
    setCurrentId((current) => reconcileGuestbookDeck(previous, current, ids));
  }, [ids]);

  const current = entries.find((e) => e.id === currentId) ?? null;

  // The dwell. Only a round of two or more moves at all.
  const body = current?.body ?? '';
  const many = ids.length > 1;
  const idsRef = useRef(ids);
  idsRef.current = ids;
  useEffect(() => {
    if (!many || currentId === null) return;
    const timer = setTimeout(
      () => setCurrentId((id) => nextGuestbookMemory(idsRef.current, id)),
      guestbookDwellMs(body),
    );
    return () => clearTimeout(timer);
  }, [currentId, many, body]);

  // The next photograph is fetched while this one is read, so a change of
  // memory never shows an empty frame.
  const nextId = many && currentId ? nextGuestbookMemory(ids, currentId) : null;
  const nextUrl = entries.find((e) => e.id === nextId)?.media.url ?? null;
  useEffect(() => {
    if (!nextUrl || typeof Image === 'undefined') return;
    const image = new Image();
    image.decoding = 'async';
    image.src = nextUrl;
  }, [nextUrl]);

  return current;
}

export function PartyGuestbookTvStage({
  entries,
  testId = 'guestbook-tv-stage',
}: {
  entries: readonly PartyGuestbookEntry[];
  testId?: string;
}) {
  const { t } = useI18n();
  const stage = useStageSize();
  const current = useGuestbookDeck(entries);

  const density = current ? guestbookDensity(current.body) : 'regular';
  const photoAspect = current && current.media.width > 0 && current.media.height > 0
    ? current.media.width / current.media.height
    : 1;
  const frameAspect = current
    ? guestbookFrameAspect(current.template.key, current.template.version, photoAspect)
    : 1;
  const box = guestbookTvPhotoBox(stage.width, stage.height, frameAspect, density);
  const baseFont = guestbookTvFontPx(stage.height, density);

  // The measured fit: shrink until the words fit their column. Starts again
  // from the tier whenever the memory or the screen changes.
  const [fontPx, setFontPx] = useState(baseFont);
  const words = useRef<HTMLElement | null>(null);
  const fitKey = `${current?.id ?? ''}:${stage.width}x${stage.height}:${baseFont}`;
  const lastFitKey = useRef(fitKey);
  useLayoutEffect(() => {
    if (lastFitKey.current !== fitKey) {
      lastFitKey.current = fitKey;
      setFontPx(baseFont);
      return;
    }
    const el = words.current;
    if (!el || el.clientHeight === 0) return;
    if (el.scrollHeight > el.clientHeight + 1) {
      const smaller = guestbookTvShrink(fontPx);
      if (smaller !== null) setFontPx(smaller);
    }
  }, [fitKey, baseFont, fontPx]);

  if (!current) {
    return <div className="guestbook-tv-stage" data-testid={testId} aria-busy="true" />;
  }

  return (
    <div
      className="guestbook-tv-stage"
      data-testid={testId}
      data-density={density}
      role="region"
      aria-label={t('tv.guestbookTitle')}
    >
      {/* Keyed by the memory: a new one fades in; the same one is left alone. */}
      <div className="guestbook-tv-memory" key={current.id} data-memory-id={current.id}>
        <PartyGuestbookMemoryCard
          memory={current}
          loading="eager"
          testId="guestbook-tv-memory"
          tv={{
            photoWidth: box.width,
            photoHeight: box.height,
            fontPx,
            wordsWidth: guestbookTvWordsWidth(stage.width, box.width, density),
            wordsHeight: guestbookTvWordsHeight(stage.height, box.height, density),
          }}
          wordsRef={words}
        />
      </div>
    </div>
  );
}
