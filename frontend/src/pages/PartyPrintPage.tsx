import {
  useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode,
} from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getPartyPrintManifest,
  getPartyPrintStatus,
  submitPartyPrint,
  type PartyPrintAccepted,
  type PartyPrintFormat,
  type PartyPrintManifest,
  type PartyPrintOrientation,
  type PartyPrintPhoto,
  type PartyPrintProduct,
  type PartyPrintSlot,
  type PartyPrintState,
  type PartyPrintTheme,
  type PartyPrintOverlayText,
  type PartyPrintOverlayLogo,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';
import { recallFaceFilter, recallPartyHome } from './partyGuestMemo';
import { effectiveZoom, placePhoto } from '@nubarca/contracts';
import { PhotoCropFrame, photoPlacementStyle } from '../party/PhotoCropFrame';
import { PhotoFramingControls } from '../party/PhotoFramingControls';
import {
  DEFAULT_CROP_VIEW, SLOTS_PER_STRIP, STRIPS_PER_SHEET,
  PORTRAIT_HEIGHT, PORTRAIT_WIDTH,
  type CropView, photoLayout, photoSlotAspect, stripFooter, stripSlot,
  STRIP_WORDMARK_WIDTH_FRACTION, gridLayout, gridPortrait, gridSlotAspect, sheet, type PaperSize,
  OVERLAY_LINE_FRACTION, OVERLAY_MARGIN_FRACTION, OVERLAY_SYMBOL_FRACTION, OVERLAY_TITLE_FRACTION,
  OVERLAY_NUMBER_FRACTION, OVERLAY_TEXT_SUPPORT_PADDING_FRACTION, OVERLAY_HALO_BLUR_FRACTION,
  OVERLAY_HALO_OPACITY, OVERLAY_NUMBER_ROOM, PARTY_NAME_MAX_LENGTH, FOOTER_MAX_LENGTH,
  overlaySlotAspect, overlayTextSupport, printedLine,
  stripSlotAspect,
} from './partyPrintGeometry';
import './PartyGuestHub.css';
import './PartyPrintPage.css';

/* PUBLIC, unauthenticated party PRINT STUDIO. Reached from the party hub's
   print card, on its own capability token — a print token, never the view one.

   Printing is the one party capability with a PHYSICAL result: a sheet comes
   out of a machine and a guest walks away holding it. That shapes the whole
   page. The budget is the server's to spend, so nothing here decides whether a
   print may happen; the studio composes, shows honestly what will be printed,
   and asks. Every submission carries an idempotency key minted for THAT
   composition, so a double tap, a flaky network or a reloaded page can never
   turn into a second sheet.

   What the guest chooses from is derived media served through the print token:
   the same metadata-stripped previews the album shows. The original is never
   sent to the browser — the server composes at 300dpi from its own copy. */

const PARTY_WORDMARK_DARK = '/brand/nubarca-wordmark-on-dark-480w.png';
// The COMPACT light rendition, not the master. The brand manifest builds this
// one so that "both themes share one visible geometry" — which is precisely
// what a footer band needs, and what the renderer draws. The padded master
// fitted into a tight band comes out visibly smaller than the on-dark artwork.
const PARTY_WORDMARK_LIGHT = '/brand/nubarca-wordmark-on-light-480w.png';
const PARTY_EYEBROW = `${PRODUCT_NAME} Party`;

/**
 * Looks, in the order they are offered. The first three frame any product; the
 * title on the photograph is a single-photograph look, whose words and symbol
 * take their colours independently.
 */
type Look = 'pure' | 'midnight' | 'event' | 'overlay';
const FRAMED_LOOKS: readonly Look[] = ['pure', 'midnight', 'event'] as const;
const PHOTO_LOOKS: readonly Look[] = [...FRAMED_LOOKS, 'overlay'] as const;
const LOOK_LABEL: Record<Look, MessageKey> = {
  pure: 'partyPrint.theme.pure',
  midnight: 'partyPrint.theme.midnight',
  event: 'partyPrint.theme.event',
  overlay: 'partyPrint.theme.overlay',
};
/**
 * The words' colour, and the support and halo under them ("r g b"): a whisper of
 * black under light or red words, of white under dark ones. The red is a print
 * colour for photographs (`overlay-text-red`), not a brand colour. Mirrors the
 * renderer value for value.
 */
const OVERLAY_TEXTS: readonly PartyPrintOverlayText[] = ['white', 'black', 'red'] as const;
const OVERLAY_TEXT: Record<PartyPrintOverlayText, { ink: string; support: string }> = {
  white: { ink: '#f5f7fb', support: '10 15 26' },
  black: { ink: '#0a0f1a', support: '245 247 251' },
  red: { ink: '#d11f2e', support: '10 15 26' },
};
/**
 * The symbol's two treatments: the brand's own flat marks, in their approved
 * colours — Cloud White, Cyan and Electric Blue for the light one, Midnight
 * Navy and Electric Blue for the dark — the very files the renderer draws.
 */
const OVERLAY_LOGOS: readonly PartyPrintOverlayLogo[] = ['light', 'dark'] as const;
const OVERLAY_LOGO: Record<PartyPrintOverlayLogo, { mark: string; swatch: string }> = {
  light: {
    mark: '/brand/nubarca-mark-flat-on-dark-256.png',
    swatch: '/brand/nubarca-mark-flat-on-dark-64.png',
  },
  dark: {
    mark: '/brand/nubarca-mark-flat-on-light-256.png',
    swatch: '/brand/nubarca-mark-flat-on-light-64.png',
  },
};

function isOverlay(theme: PartyPrintTheme): theme is 'overlay' {
  return theme === 'overlay';
}
/** Which wordmark an artwork this dark takes. Mirrors the renderer's palette. */
const DARK_THEMES: readonly PartyPrintTheme[] = ['midnight', 'event'] as const;

const STATE_LABEL: Record<PartyPrintState, MessageKey> = {
  preparing: 'partyPrint.state.preparing',
  queued: 'partyPrint.state.queued',
  waiting_paper: 'partyPrint.state.waitingPaper',
  printing: 'partyPrint.state.printing',
  completed: 'partyPrint.state.completed',
  failed: 'partyPrint.state.failed',
  unknown: 'partyPrint.state.unknown',
};

/** Once a print is out of the pipeline there is nothing left to ask about. */
const SETTLED: readonly PartyPrintState[] = ['completed', 'failed', 'unknown'] as const;
const STATUS_POLL_MS = 4_000;

type Step = 'format' | 'select' | 'arrange' | 'crop' | 'preview';

type Phase =
  | { kind: 'loading' }
  | { kind: 'ready'; manifest: PartyPrintManifest }
  | { kind: 'unavailable' }
  | { kind: 'error' };

/** A composition that has been accepted, and what the queue has done with it. */
interface Sent {
  accepted: PartyPrintAccepted;
  state: PartyPrintState;
}

// The server's refusal codes, said in the guest's language. Anything else is
// the generic line: a guest is told their print did not go, never why the
// server is unhappy.
function refusalKey(err: unknown): MessageKey {
  if (!(err instanceof ApiError)) return 'partyPrint.error.generic';
  const code = typeof err.body === 'object' && err.body !== null
    ? (err.body as { error?: unknown }).error
    : undefined;
  switch (code) {
    case 'budget_exhausted': return 'partyPrint.error.budget';
    // Distinct from the party running out: telling a guest the party is out
    // when it is their own share that is spent is a lie they can see through
    // the moment somebody else collects a print.
    case 'guest_budget_exhausted': return 'partyPrint.error.guestBudget';
    // A lent printer whose owner's ceiling is used up: not this guest's
    // share, not the party's — the printer has no more sheets for it.
    case 'share_exhausted': return 'partyPrint.error.shareExhausted';
    case 'printer_unavailable': return 'partyPrint.error.printer';
    case 'render_failed': return 'partyPrint.error.render';
    case 'invalid_source': return 'partyPrint.error.source';
    case 'paper_changed': return 'partyPrint.error.paperChanged';
    default: return 'partyPrint.error.generic';
  }
}

function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return globalThis.crypto.randomUUID();
  // Older WebViews: still unique enough to distinguish one guest's compositions.
  return `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function PrinterIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M7 8.5V4.5h10v4" />
      <path d="M7 17.5H5.5A1.5 1.5 0 0 1 4 16v-4.5a2 2 0 0 1 2-2h12a2 2 0 0 1 2 2V16a1.5 1.5 0 0 1-1.5 1.5H17" />
      <rect x="7" y="14" width="10" height="6" rx="1.2" />
    </svg>
  );
}

function GridIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect x="4" y="3.5" width="7" height="8" rx="1.2" />
      <rect x="13" y="3.5" width="7" height="8" rx="1.2" />
      <rect x="4" y="12.5" width="7" height="8" rx="1.2" />
      <rect x="13" y="12.5" width="7" height="8" rx="1.2" />
    </svg>
  );
}

/** A paper as the photo trade writes it. */
const PAPER_LABEL: Record<PaperSize, string> = {
  '10x15': '10\u00d715',
  '13x18': '13\u00d718',
  '20x15': '20\u00d715',
};

/**
 * What a slot shows beside a photograph zoomed out — the renderer's band:
 * each palette's own paper, and white on the photograph that has none.
 */
const PRINT_BAND: Readonly<Record<PartyPrintTheme, string>> = {
  pure: '#f5f7fb',
  midnight: '#0a0f1a',
  event: '#0f1e3a',
  overlay: '#ffffff',
};

/** Where each of the four sits on its sheet, in reading order. */
const GRID_POSITION: readonly MessageKey[] = [
  'partyPrint.gridPosition.topLeft', 'partyPrint.gridPosition.topRight',
  'partyPrint.gridPosition.bottomLeft', 'partyPrint.gridPosition.bottomRight',
] as const;

function StripIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <rect x="8.5" y="2.5" width="7" height="19" rx="1.4" />
      <path d="M8.5 7.25h7M8.5 12h7M8.5 16.75h7" />
    </svg>
  );
}

// --- The sheet, as it will come out ----------------------------------------

/**
 * A photograph inside a rectangle, framed exactly as the server will frame it.
 * The same placement maths drives this and the print, so what a guest arranges
 * here is what the paper gets — zoomed out, the slot's own paper beside it.
 */
function FramedPhoto({
  photo, aspect, slotAspect, view, onAspect,
}: {
  photo: PartyPrintPhoto | undefined;
  aspect: number;
  slotAspect: number;
  view: CropView;
  onAspect?: (width: number, height: number) => void;
}) {
  if (!photo) return null;
  return (
    <img
      className="party-print-framed"
      src={photo.previewUrl}
      alt=""
      // A single photograph's sheet is turned to match it, so the preview keeps
      // learning shapes here too rather than only from the chooser's thumbnails.
      onLoad={(event) => onAspect?.(
        event.currentTarget.naturalWidth, event.currentTarget.naturalHeight)}
      style={photoPlacementStyle(placePhoto(aspect, slotAspect, view))}
    />
  );
}

/** The party line and the wordmark, in the band the renderer reserves. */
function SheetFooter({
  partyName, footerText, theme, strip = false,
}: {
  partyName: string;
  footerText: string | null;
  theme: PartyPrintTheme;
  /** A strip's wordmark stands as wide as its narrow row allows, as on paper. */
  strip?: boolean;
}) {
  const dark = DARK_THEMES.includes(theme);
  return (
    <div className="party-print-sheet-footer">
      <span className="party-print-sheet-text">
        <span className="party-print-sheet-name">{partyName}</span>
        {footerText && <span className="party-print-sheet-line">{footerText}</span>}
      </span>
      {/* Bottom-left, where the renderer puts it. The queue number that shares
          this row on paper is deliberately absent: it does not exist until the
          print is accepted, and a preview does not invent one. */}
      <span className="party-print-sheet-sign">
        <img
          className={strip ? 'party-print-sheet-mark party-print-sheet-mark-strip' : 'party-print-sheet-mark'}
          src={dark ? PARTY_WORDMARK_DARK : PARTY_WORDMARK_LIGHT}
          alt={PRODUCT_NAME}
          style={strip ? { width: pct(STRIP_WORDMARK_WIDTH_FRACTION) } : undefined}
        />
      </span>
    </div>
  );
}

interface SheetProps {
  product: PartyPrintProduct;
  theme: PartyPrintTheme;
  partyName: string;
  footerText: string | null;
  chosen: string[];
  photoById: Map<string, PartyPrintPhoto>;
  aspectOf: (id: string) => number;
  views: Record<string, CropView>;
  onAspect: (id: string, width: number, height: number) => void;
  /** Null follows the photograph, which is the default. */
  orientation: PartyPrintOrientation | null;
  /** The paper in the printer: every sheet is one of it. */
  paper: PaperSize;
  /** Only read for the 'overlay' look. */
  overlayText: PartyPrintOverlayText;
  overlayLogo: PartyPrintOverlayLogo;
}

function pct(value: number): string {
  return `${value * 100}%`;
}

/**
 * The renderer's FitLine for the preview: a line wider than its box shrinks —
 * never below `floor` of its size — before the ellipsis takes the rest. The
 * box is what the layout left it, the number's room already taken out. The
 * sizes are container units, so the ratio found once holds at any width.
 */
function useFitLine(size: string, floor: number, deps: readonly unknown[]) {
  const ref = useRef<HTMLSpanElement>(null);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return undefined;
    const fit = () => {
      el.style.fontSize = size;
      const room = el.clientWidth;
      const needed = el.scrollWidth;
      if (needed > room && room > 0) {
        el.style.fontSize = `calc(${size} * ${Math.max(floor, room / needed)})`;
      }
    };
    fit();
    let live = true;
    // Measured again once the web fonts arrive: they set the real width.
    void document.fonts?.ready.then(() => { if (live) fit(); });
    return () => { live = false; };
  }, [size, floor, ...deps]);
  return ref;
}

/**
 * The words of the title-on-the-photo preview, laid out as the renderer lays
 * them out: the name and the host's line cut to what the paper prints, the
 * bottom line sharing its width with the room kept for the number, and each
 * line fitted into what is left.
 */
function OverlayWords({ partyName, footerText, orientation }: {
  partyName: string; footerText: string | null; orientation: string;
}) {
  const name = printedLine(partyName, PARTY_NAME_MAX_LENGTH);
  const footer = printedLine(footerText ?? '', FOOTER_MAX_LENGTH);
  // The renderer's floors: the name as small as it must be, the line to 3/4.
  const nameRef = useFitLine('var(--title-size)', 0, [name, footer, orientation]);
  const lineRef = useFitLine('var(--line-size)', 0.75, [footer, orientation]);
  const nameLine = <span ref={nameRef} className="party-print-overlay-name">{name}</span>;
  // The number's room is the bottom line's ::after — never text on the page.
  const bottom = (content: ReactNode) => (
    <div
      className="party-print-overlay-bottom"
      data-testid="party-print-overlay-bottom"
      data-number-room={OVERLAY_NUMBER_ROOM}
    >
      {content}
    </div>
  );
  // With a host's line the name stands above the bottom line; without one it
  // shares the bottom line with the number, as on paper.
  return footer ? (
    <>
      {nameLine}
      {bottom(<span ref={lineRef} className="party-print-overlay-line">{footer}</span>)}
    </>
  ) : bottom(nameLine);
}

function SheetPreview(props: SheetProps) {
  const {
    product, theme, chosen, photoById, aspectOf, views, onAspect, orientation, paper,
  } = props;
  const viewOf = (id: string) => views[id] ?? DEFAULT_CROP_VIEW;

  if (product === 'photo' && isOverlay(theme)) {
    const id = chosen[0];
    const aspect = aspectOf(id);
    const portrait = orientation === null ? aspect <= 1 : orientation === 'portrait';
    const [w, h] = sheet(paper, portrait);
    const short = Math.min(w, h);
    // Everything is a fraction of the short edge, turned into a fraction of the
    // sheet's width or height, exactly as the renderer measures it.
    const ofWidth = (fraction: number) => pct((fraction * short) / w);
    const ofHeight = (fraction: number) => pct((fraction * short) / h);
    const text = OVERLAY_TEXT[props.overlayText];
    return (
      <div
        className="party-print-sheet party-print-sheet-overlay"
        data-theme={theme}
        data-testid="party-print-sheet"
        data-orientation={portrait ? 'portrait' : 'landscape'}
        data-overlay-text={props.overlayText}
        data-overlay-logo={props.overlayLogo}
        style={{ aspectRatio: `${w} / ${h}` }}
      >
        <div className="party-print-slot" style={{ left: 0, top: 0, width: '100%', height: '100%' }}>
          <FramedPhoto
            photo={photoById.get(id)} aspect={aspect}
            slotAspect={overlaySlotAspect(portrait, paper)} view={viewOf(id)}
            onAspect={(width, height) => onAspect(id, width, height)}
          />
        </div>
        <img
          className="party-print-overlay-symbol"
          data-testid="party-print-overlay-symbol"
          src={OVERLAY_LOGO[props.overlayLogo].mark}
          alt=""
          aria-hidden="true"
          style={{
            left: ofWidth(OVERLAY_MARGIN_FRACTION), top: ofHeight(OVERLAY_MARGIN_FRACTION),
            width: ofWidth(OVERLAY_SYMBOL_FRACTION), height: ofHeight(OVERLAY_SYMBOL_FRACTION),
          }}
        />
        {/* The support is the words' own box: anchored to the foot, it begins
            one padding above the real block of text, whatever the words turn
            out to be, and the photograph above it keeps every pixel. Padding
            percentages are of the sheet's width, as `ofWidth` gives them. */}
        <div
          className="party-print-overlay-support"
          data-testid="party-print-overlay-support"
          style={{
            background: overlayTextSupport(text.support),
            paddingTop: ofWidth(OVERLAY_TEXT_SUPPORT_PADDING_FRACTION),
            paddingInline: ofWidth(OVERLAY_MARGIN_FRACTION),
            paddingBottom: ofWidth(OVERLAY_MARGIN_FRACTION),
          }}
        >
          <div
            className="party-print-overlay-text"
            style={{
              color: text.ink,
              ['--halo' as string]: `rgb(${text.support} / ${OVERLAY_HALO_OPACITY * 100}%)`,
              // cqw of the sheet: the same fraction of the short edge as on paper.
              ['--halo-blur' as string]: `${((2 * OVERLAY_HALO_BLUR_FRACTION * short) / w) * 100}cqw`,
              ['--title-size' as string]: `${((OVERLAY_TITLE_FRACTION * short) / w) * 100}cqw`,
              ['--line-size' as string]: `${((OVERLAY_LINE_FRACTION * short) / w) * 100}cqw`,
              ['--number-size' as string]: `${((OVERLAY_NUMBER_FRACTION * short) / w) * 100}cqw`,
            }}
          >
            <OverlayWords
              partyName={props.partyName}
              footerText={props.footerText}
              orientation={portrait ? 'portrait' : 'landscape'}
            />
          </div>
        </div>
      </div>
    );
  }

  if (product === 'photo') {
    const id = chosen[0];
    const aspect = aspectOf(id);
    // The sheet follows the photograph unless the guest turned it — exactly the
    // decision the renderer makes, made here from the same inputs.
    const portrait = orientation === null ? aspect <= 1 : orientation === 'portrait';
    const { sheetWidth, sheetHeight, slot, footer } = photoLayout(portrait, paper);
    const slotAspect = photoSlotAspect(portrait, paper);
    return (
      <div
        className="party-print-sheet"
        data-theme={theme}
        data-testid="party-print-sheet"
        data-orientation={portrait ? 'portrait' : 'landscape'}
        style={{ aspectRatio: `${sheetWidth} / ${sheetHeight}` }}
      >
        <div
          className="party-print-slot"
          style={{
            left: pct(slot.x), top: pct(slot.y),
            width: pct(slot.width), height: pct(slot.height),
          }}
        >
          <FramedPhoto
            photo={photoById.get(id)} aspect={aspect}
            slotAspect={slotAspect} view={viewOf(id)}
            onAspect={(w, h) => onAspect(id, w, h)}
          />
        </div>
        <div
          className="party-print-footer-band"
          style={{
            left: pct(footer.x), top: pct(footer.y),
            width: pct(footer.width), height: pct(footer.height),
          }}
        >
          <SheetFooter {...props} />
        </div>
      </div>
    );
  }

  if (product === 'grid4') {
    // Four frames, two by two, on the paper as it is named — the renderer's
    // own layout (gridLayout), in the order the guest arranged them.
    const { sheetWidth, sheetHeight, slots, footer } = gridLayout(paper);
    const slotAspect = gridSlotAspect(paper);
    return (
      <div
        className="party-print-sheet"
        data-theme={theme}
        data-testid="party-print-sheet"
        data-layout="grid4"
        data-orientation={gridPortrait(paper) ? 'portrait' : 'landscape'}
        style={{ aspectRatio: `${sheetWidth} / ${sheetHeight}` }}
      >
        {slots.map((rect, index) => {
          const id = chosen[index];
          return (
            <div
              key={index}
              className="party-print-slot"
              data-testid={`party-print-grid-${index}`}
              style={{
                left: pct(rect.x), top: pct(rect.y),
                width: pct(rect.width), height: pct(rect.height),
              }}
            >
              <FramedPhoto
                photo={photoById.get(id)} aspect={aspectOf(id)}
                slotAspect={slotAspect} view={viewOf(id)}
                onAspect={(w, h) => onAspect(id, w, h)}
              />
            </div>
          );
        })}
        <div
          className="party-print-footer-band"
          style={{
            left: pct(footer.x), top: pct(footer.y),
            width: pct(footer.width), height: pct(footer.height),
          }}
        >
          <SheetFooter {...props} />
        </div>
      </div>
    );
  }

  const slotAspect = stripSlotAspect();
  return (
    <div
      className="party-print-sheet"
      data-theme={theme}
      data-testid="party-print-sheet"
      data-layout="twinStrip4"
      data-orientation="portrait"
      style={{ aspectRatio: `${PORTRAIT_WIDTH} / ${PORTRAIT_HEIGHT}` }}
    >
      {/* TWO STRIPS of four on one sheet, which the printer cuts apart. */}
      {Array.from({ length: STRIPS_PER_SHEET }, (_, strip) => (
        <div
          key={strip}
          data-testid={`party-print-strip-${strip}`}
        >
          {Array.from({ length: SLOTS_PER_STRIP }, (_, index) => {
            const rect = stripSlot(strip, index);
            // Photographs 1–4 on the first strip, 5–8 on the second.
            const id = chosen[strip * SLOTS_PER_STRIP + index] ?? chosen[index];
            return (
              <div
                key={index}
                className="party-print-slot"
                style={{
                  left: pct(rect.x), top: pct(rect.y),
                  width: pct(rect.width), height: pct(rect.height),
                }}
              >
                <FramedPhoto
                  photo={photoById.get(id)} aspect={aspectOf(id)}
                  slotAspect={slotAspect} view={viewOf(id)}
                  onAspect={(w, h) => onAspect(id, w, h)}
                />
              </div>
            );
          })}
          <div
            className="party-print-footer-band"
            style={{
              left: pct(stripFooter(strip).x), top: pct(stripFooter(strip).y),
              width: pct(stripFooter(strip).width), height: pct(stripFooter(strip).height),
            }}
          >
            <SheetFooter {...props} strip />
          </div>
        </div>
      ))}
      {/* No cut marks: the printer cuts the sheet, and the print has none. */}
    </div>
  );
}

// --- Framing one photograph -------------------------------------------------

export function PartyPrintPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });

  const [step, setStep] = useState<Step>('format');
  const [product, setProduct] = useState<PartyPrintProduct | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [views, setViews] = useState<Record<string, CropView>>({});
  const [aspects, setAspects] = useState<Record<string, number>>({});
  const [look, setLook] = useState<Look>('pure');
  const [overlayText, setOverlayText] = useState<PartyPrintOverlayText>('white');
  const [overlayLogo, setOverlayLogo] = useState<PartyPrintOverlayLogo>('light');
  // The theme the server is sent: the overlay exists only for a single photograph.
  const theme: PartyPrintTheme = look === 'overlay'
    ? (product === 'photo' ? 'overlay' : 'pure')
    : look;
  const [cropIndex, setCropIndex] = useState(0);
  const [onlyMine, setOnlyMine] = useState(false);
  // Null is not "unset waiting for a value" — it IS the default: follow the
  // photograph. Only a guest who deliberately turns the sheet leaves it.
  const [orientation, setOrientation] = useState<PartyPrintOrientation | null>(null);
  // On a phone a whole 10x15 frame can reach below the fold, and a finger on
  // it moves the photograph rather than the page. When a photograph opens for
  // framing and its frame does not end on screen, bring the frame into view.
  const cropStageRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (step !== 'crop') return;
    const stage = cropStageRef.current;
    if (stage && stage.getBoundingClientRect().bottom > window.innerHeight) {
      stage.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
    }
  }, [step, cropIndex]);

  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<MessageKey | null>(null);
  const [sent, setSent] = useState<Sent | null>(null);

  // The sheet as it was first sent: its idempotency key AND the exact slots
  // that key stands for, frozen together at the first attempt.
  //
  // Minting the key alone would leave "the same key" and "the same sheet" as
  // two separate facts that merely tend to agree — a photograph whose real
  // shape arrived between two attempts would change the crop under a key the
  // server has already decided about. Freezing both makes it one fact. Changing
  // the composition discards the pair and earns a new key.
  const pendingRef = useRef<{ key: string; slots: PartyPrintSlot[] } | null>(null);
  useEffect(() => {
    pendingRef.current = null;
  }, [product, chosen, views, theme, orientation, overlayText, overlayLogo]);

  useEffect(() => {
    if (!token) {
      setPhase({ kind: 'unavailable' });
      return;
    }
    const controller = new AbortController();
    setPhase({ kind: 'loading' });
    getPartyPrintManifest(token, controller.signal)
      .then((manifest) => setPhase({ kind: 'ready', manifest }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 404) {
          setPhase({ kind: 'unavailable' });
          return;
        }
        setPhase({ kind: 'error' });
      });
    return () => controller.abort();
  }, [token]);

  // Follow an accepted print until it is out of the pipeline. The guest is
  // waiting at a printer, so this says what is happening rather than going
  // quiet after "sent".
  useEffect(() => {
    if (!token || !sent || SETTLED.includes(sent.state)) return;
    const controller = new AbortController();
    const timer = window.setInterval(() => {
      getPartyPrintStatus(token, sent.accepted.jobId, controller.signal)
        .then((status) => setSent((prev) => (
          prev && prev.accepted.jobId === status.jobId
            ? { ...prev, state: status.state }
            : prev
        )))
        .catch(() => { /* A missed poll is not news; the next one asks again. */ });
    }, STATUS_POLL_MS);
    return () => {
      window.clearInterval(timer);
      controller.abort();
    };
  }, [token, sent]);

  const manifest = phase.kind === 'ready' ? phase.manifest : null;

  const photoById = useMemo(() => new Map(
    (manifest?.photos ?? []).map((photo) => [photo.id, photo] as const),
  ), [manifest]);

  // What the guest's own face search found, if they ran one on the hub. The
  // memo is intersected with THIS token's photographs, so a stale one from
  // another party simply matches nothing and is never offered.
  const mine = useMemo(() => {
    const remembered = new Set(recallFaceFilter());
    return (manifest?.photos ?? []).filter((photo) => remembered.has(photo.id));
  }, [manifest]);

  /**
   * What actually bounds this guest for a product.
   *
   * Two ceilings apply and the smaller one is the truth. Showing the party's
   * forty to somebody allowed two was hiding the rule from the only person it
   * applies to — and letting them find it out by being refused.
   */
  const leftFor = useCallback((f: PartyPrintFormat) => (
    f.remainingForYou === null ? f.remaining : Math.min(f.remaining, f.remainingForYou)
  ), []);
  /** True when it is the guest's own allowance that has run out, not the party's. */
  const yoursIsBinding = useCallback((f: PartyPrintFormat) => (
    f.remainingForYou !== null && f.remainingForYou <= f.remaining
  ), []);

  const format = manifest?.formats.find((f) => f.type === product) ?? null;
  // The paper is a fact about the printer: every product on offer is a sheet of it.
  const paper: PaperSize = manifest?.paperSize ?? '10x15';
  const required = format?.requiredPhotos ?? 1;
  const printable = manifest?.formats.filter((f) => f.enabled) ?? [];
  const anyLeft = printable.some((f) => leftFor(f) > 0);

  const gallery = onlyMine && mine.length > 0 ? mine : (manifest?.photos ?? []);

  // The natural shape of a photograph, learned when its preview loads. Until
  // then it is treated as filling its slot exactly, which crops nothing.
  const aspectOf = useCallback((id: string) => aspects[id] ?? 0, [aspects]);
  const noteAspect = useCallback((id: string, width: number, height: number) => {
    if (width <= 0 || height <= 0) return;
    setAspects((prev) => (prev[id] ? prev : { ...prev, [id]: width / height }));
  }, []);

  /** Portrait unless the guest turned the sheet, or the photograph is wide. */
  const portraitFor = useCallback((id: string) => (
    orientation === null ? aspectOf(id) <= 1 : orientation === 'portrait'
  ), [orientation, aspectOf]);

  const slotAspectFor = useCallback((id: string) => (
    product === 'twinStrip4'
      ? stripSlotAspect()
      : product === 'grid4'
        ? gridSlotAspect(paper)
        : isOverlay(theme)
          ? overlaySlotAspect(portraitFor(id), paper)
          : photoSlotAspect(portraitFor(id), paper)
  ), [product, portraitFor, theme]);

  const toggle = (id: string) => {
    setChosen((prev) => {
      if (prev.includes(id)) return prev.filter((other) => other !== id);
      if (prev.length >= required) return prev;
      return [...prev, id];
    });
  };

  const move = (index: number, delta: number) => {
    setChosen((prev) => {
      const next = [...prev];
      const target = index + delta;
      if (target < 0 || target >= next.length) return prev;
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  };

  const setView = (id: string, view: CropView) => {
    setViews((prev) => ({ ...prev, [id]: view }));
  };

  const startOver = () => {
    setSent(null);
    setSubmitError(null);
    setProduct(null);
    setChosen([]);
    setViews({});
    setCropIndex(0);
    setOrientation(null);
    setStep('format');
    // The budgets moved while this guest was composing, so re-read them rather
    // than offering a count that is already out of date.
    if (token) {
      getPartyPrintManifest(token)
        .then((fresh) => setPhase({ kind: 'ready', manifest: fresh }))
        .catch(() => { /* Keep what we have; the next submit is authoritative. */ });
    }
  };

  const submit = async () => {
    if (!token || !product) return;
    pendingRef.current ??= {
      key: newIdempotencyKey(),
      slots: chosen.map((id) => {
        const view = views[id] ?? DEFAULT_CROP_VIEW;
        // The zoom as drawn: a framing held below a shape learned later is
        // sent as the contain it now shows.
        const zoom = effectiveZoom(aspectOf(id), slotAspectFor(id), view.zoom);
        return { itemId: id, placement: { centerX: view.centerX, centerY: view.centerY, zoom } };
      }),
    };
    const pending = pendingRef.current;
    setSubmitting(true);
    setSubmitError(null);
    try {
      const accepted = await submitPartyPrint(
        token,
        {
          product,
          theme,
          slots: pending.slots,
          // Omitted when the guest left the default: the server follows the
          // photograph, which is what it did before this choice existed.
          ...(product === 'photo' && orientation ? { orientation } : {}),
          ...(theme === 'overlay' ? { overlayText, overlayLogo } : {}),
          // The paper this sheet was composed for: if the printer's is no
          // longer it, nothing is printed and the studio starts again.
          paperSize: paper,
        },
        pending.key);
      setSent({ accepted, state: 'preparing' });
    } catch (err: unknown) {
      const refusal = refusalKey(err);
      if (refusal === 'partyPrint.error.paperChanged') {
        // Another paper is in: what can be made has changed, so begin again
        // from the products the new paper offers, saying why.
        startOver();
        setSubmitError(refusal);
        return;
      }
      setSubmitError(refusal);
    } finally {
      setSubmitting(false);
    }
  };

  if (phase.kind === 'loading') {
    return (
      <PrintShell>
        <p className="party-print-note">{t('partyPrint.loading')}</p>
      </PrintShell>
    );
  }

  if (phase.kind !== 'ready') {
    const message = phase.kind === 'unavailable' ? 'partyPrint.unavailable' : 'partyPrint.error';
    return (
      <PrintShell>
        <p className="party-print-note">{t(message)}</p>
        <p className="party-print-hint">{t('partyPrint.unavailableHelp')}</p>
      </PrintShell>
    );
  }

  if (sent) {
    const left = sent.accepted.remainingForProduct;
    return (
      <PrintShell title={phase.manifest.partyName}>
        <div className="party-print-sent" role="status">
          <p className="party-print-sent-title">{t('partyPrint.sent')}</p>
          <p className="party-print-ticket-label">{t('partyPrint.ticket')}</p>
          <p className="party-print-ticket">{sent.accepted.publicSequence}</p>
          <p className="party-print-sent-state">{t(STATE_LABEL[sent.state])}</p>
          {/* How long the wait is. "In the queue" without a number answers
              nothing to somebody standing at the printer. */}
          <p className="party-print-hint">
            {sent.accepted.queueAhead > 0
              ? tn(sent.accepted.queueAhead, 'partyPrint.queueAhead')
              : t('partyPrint.queueNext')}
          </p>
          <p className="party-print-hint">{t('partyPrint.collect')}</p>
          <p className="party-print-hint">
            {left > 0 ? tn(left, 'partyPrint.leftAfter') : t('partyPrint.noneLeftAfter')}
          </p>
        </div>
        {left > 0 && (
          <button type="button" className="party-print-primary" onClick={startOver}>
            {t('partyPrint.printAnother')}
          </button>
        )}
        <BackLink />
      </PrintShell>
    );
  }

  // Nothing left to print. Said plainly rather than shown as a dead button:
  // budgets move while a guest is deciding, and this is a real state.
  if (printable.length === 0 || !anyLeft) {
    // WHOSE paper ran out matters. Telling a guest the party is finished while
    // it still has forty sheets is a lie they see through the moment somebody
    // else collects a print — and it sends them to complain to the host about
    // a limit the host set on purpose.
    const mine = printable.length > 0 && printable.every(yoursIsBinding);
    return (
      <PrintShell title={phase.manifest.partyName}>
        <p className="party-print-note">
          {t(mine ? 'partyPrint.yoursAllDone' : 'partyPrint.allExhausted')}
        </p>
        <p className="party-print-hint">
          {t(mine ? 'partyPrint.yoursAllDoneHelp' : 'partyPrint.allExhaustedHelp')}
        </p>
        <BackLink />
      </PrintShell>
    );
  }

  return (
    <PrintShell title={phase.manifest.partyName}>
      {step === 'format' && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.format')}
          </h2>
          {/* Why the guest is back here: another paper was put in while they
              composed, and this is what it can make. */}
          {submitError && (
            <p className="party-print-error" role="alert">{t(submitError)}</p>
          )}
          <ul className="party-print-formats">
            {printable.map((option) => {
              const left = leftFor(option);
              const out = left <= 0;
              return (
                <li key={option.type}>
                  <button
                    type="button"
                    className="party-print-format"
                    data-testid={`party-print-format-${option.type}`}
                    data-exhausted={out ? 'true' : undefined}
                    disabled={out}
                    onClick={() => {
                      setSubmitError(null);
                      setProduct(option.type);
                      setChosen([]);
                      setStep('select');
                    }}
                  >
                    <span className="party-print-format-icon" aria-hidden="true">
                      {option.type === 'twinStrip4'
                        ? <StripIcon />
                        : option.type === 'grid4' ? <GridIcon /> : <PrinterIcon />}
                    </span>
                    <span className="party-print-format-text">
                      <strong>
                        {t(`partyPrint.format.${option.type}`, { paper: PAPER_LABEL[paper] })}
                      </strong>
                      <span className="party-print-format-help">
                        {t(`partyPrint.format.${option.type}Help`)}
                      </span>
                    </span>
                    <span className="party-print-format-left">
                      {out
                        ? t(yoursIsBinding(option)
                          ? 'partyPrint.yoursDone'
                          : 'partyPrint.exhausted')
                        : yoursIsBinding(option)
                          ? tn(left, 'partyPrint.yoursLeft')
                          : tn(left, 'partyPrint.remaining')}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
          <BackLink />
        </section>
      )}

      {step === 'select' && product && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t(product === 'twinStrip4'
              ? 'partyPrint.selectStrip'
              : product === 'grid4' ? 'partyPrint.selectGrid' : 'partyPrint.selectPhoto')}
          </h2>
          <p className="party-print-count" role="status">
            {t('partyPrint.chosen', { count: chosen.length, total: required })}
          </p>
          {/* Offered only when the guest's own search actually matched
              photographs this token serves. */}
          {mine.length > 0 && (
            <button
              type="button"
              className="party-print-filter"
              aria-pressed={onlyMine}
              onClick={() => setOnlyMine((on) => !on)}
            >
              {t(onlyMine ? 'partyPrint.allPhotos' : 'partyPrint.onlyMine')}
            </button>
          )}
          {gallery.length === 0 ? (
            <p className="party-print-note">{t('partyPrint.noPhotos')}</p>
          ) : (
            <ul className="party-print-gallery">
              {gallery.map((photo) => {
                const at = chosen.indexOf(photo.id);
                return (
                  <li key={photo.id}>
                    <button
                      type="button"
                      className="party-print-pick"
                      aria-pressed={at >= 0}
                      aria-label={at >= 0
                        ? t('partyPrint.unchoose')
                        : t('partyPrint.choose')}
                      onClick={() => toggle(photo.id)}
                    >
                      <img
                        src={photo.thumbnailUrl}
                        alt=""
                        loading="lazy"
                        onLoad={(event) => noteAspect(
                          photo.id,
                          event.currentTarget.naturalWidth,
                          event.currentTarget.naturalHeight,
                        )}
                      />
                      {at >= 0 && (
                        <span className="party-print-pick-badge" aria-hidden="true">
                          {at + 1}
                        </span>
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
          {/* Pinned where a thumb is — the invitation's own bar — so a guest
              choosing from a long album never has to scroll to its end to go on. */}
          <div className="party-invitation-cta" data-testid="party-print-select-bar">
            <div className="party-print-actions">
              <button
                type="button"
                className="party-print-secondary"
                onClick={() => setStep('format')}
              >
                {t('partyPrint.previous')}
              </button>
              <button
                type="button"
                className="party-print-primary"
                disabled={chosen.length !== required}
                onClick={() => setStep(product === 'photo' ? 'crop' : 'arrange')}
              >
                {t('partyPrint.continue')}
              </button>
            </div>
          </div>
        </section>
      )}

      {step === 'arrange' && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.arrange')}
          </h2>
          <p className="party-print-hint">
            {t(product === 'grid4' ? 'partyPrint.arrangeHelpGrid' : 'partyPrint.arrangeHelp')}
          </p>
          {/* Reordering is BUTTONS, not only dragging: the order is part of the
              composition, and it must be reachable by keyboard, by screen
              reader and by anyone who cannot hold a drag. */}
          {(() => {
            const label = (index: number) => (product === 'grid4'
              ? `${t('partyPrint.position', { n: index + 1 })} \u00b7 ${t(GRID_POSITION[index])}`
              : t('partyPrint.position', { n: index + 1 }));
            const row = (id: string, index: number) => (
              <li key={id} className="party-print-order-row">
                <span className="party-print-order-index" aria-hidden="true">{index + 1}</span>
                <img src={photoById.get(id)?.thumbnailUrl} alt="" />
                <span className="party-print-order-label">{label(index)}</span>
                <span className="party-print-order-buttons">
                  <button
                    type="button"
                    disabled={index === 0}
                    aria-label={`${t('partyPrint.moveUp')} \u2014 ${label(index)}`}
                    onClick={() => move(index, -1)}
                  >
                    {t('partyPrint.moveUp')}
                  </button>
                  <button
                    type="button"
                    disabled={index === chosen.length - 1}
                    aria-label={`${t('partyPrint.moveDown')} \u2014 ${label(index)}`}
                    onClick={() => move(index, 1)}
                  >
                    {t('partyPrint.moveDown')}
                  </button>
                </span>
              </li>
            );
            // The twin strip is two strips: the first four on one, the next
            // four on the other — shown as the two groups they will be.
            return product === 'twinStrip4'
              ? [0, 1].map((strip) => (
                <section
                  key={strip}
                  className="party-print-order-group"
                  aria-labelledby={`party-print-strip-heading-${strip}`}
                  data-testid={`party-print-order-strip-${strip}`}
                >
                  <h3 className="party-print-order-heading" id={`party-print-strip-heading-${strip}`}>
                    {t(strip === 0 ? 'partyPrint.firstStrip' : 'partyPrint.secondStrip')}
                  </h3>
                  <ol className="party-print-order" start={strip * SLOTS_PER_STRIP + 1}>
                    {chosen.slice(strip * SLOTS_PER_STRIP, (strip + 1) * SLOTS_PER_STRIP)
                      .map((id, i) => row(id, strip * SLOTS_PER_STRIP + i))}
                  </ol>
                </section>
              ))
              : <ol className="party-print-order">{chosen.map(row)}</ol>;
          })()}
          <div className="party-print-actions">
            <button
              type="button"
              className="party-print-secondary"
              onClick={() => setStep('select')}
            >
              {t('partyPrint.previous')}
            </button>
            <button
              type="button"
              className="party-print-primary"
              onClick={() => { setCropIndex(0); setStep('crop'); }}
            >
              {t('partyPrint.continue')}
            </button>
          </div>
        </section>
      )}

      {step === 'crop' && product && (() => {
        const id = chosen[cropIndex];
        const photo = photoById.get(id);
        const view = views[id] ?? DEFAULT_CROP_VIEW;
        const last = cropIndex === chosen.length - 1;
        return (
          <section className="party-print-step" aria-labelledby="party-print-heading">
            <h2 className="party-print-heading" id="party-print-heading">
              {t('partyPrint.step.crop')}
            </h2>
            {chosen.length > 1 && (
              <p className="party-print-count" role="status">
                {t('partyPrint.cropOf', { n: cropIndex + 1, total: chosen.length })}
              </p>
            )}
            {photo && (
              /* Sized to the screen as well as the column: see the CSS. */
              <div
                ref={cropStageRef}
                className="party-print-crop-stage"
                style={{ ['--crop-aspect' as string]: slotAspectFor(id) }}
              >
                <PhotoCropFrame
                  src={photo.previewUrl}
                  aspect={aspectOf(id)}
                  slotAspect={slotAspectFor(id)}
                  view={view}
                  label={t('partyPrint.cropHelp')}
                  onChange={(next) => setView(id, next)}
                  // "Adatta" needs the photograph's shape: learn it here too.
                  onAspect={(width, height) => noteAspect(id, width, height)}
                  testId="party-print-crop"
                  // What this slot shows beside a photograph zoomed out: the
                  // sheet's own paper, white on the photograph with no paper.
                  background={PRINT_BAND[theme]}
                />
              </div>
            )}
            <p className="party-print-hint">{t('partyPrint.cropHelp')}</p>
            <PhotoFramingControls
              aspect={aspectOf(id)}
              slotAspect={slotAspectFor(id)}
              view={view}
              onChange={(next) => setView(id, next)}
              zoomLabel={t('partyPrint.zoom')}
              testId="party-print-framing"
            />
            <div className="party-print-actions">
              <button
                type="button"
                className="party-print-secondary"
                onClick={() => {
                  if (cropIndex > 0) setCropIndex(cropIndex - 1);
                  else setStep(product === 'photo' ? 'select' : 'arrange');
                }}
              >
                {t('partyPrint.previous')}
              </button>
              <button
                type="button"
                className="party-print-primary"
                onClick={() => {
                  if (last) setStep('preview');
                  else setCropIndex(cropIndex + 1);
                }}
              >
                {t('partyPrint.continue')}
              </button>
            </div>
          </section>
        );
      })()}

      {step === 'preview' && product && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.preview')}
          </h2>
          <SheetPreview
            product={product}
            theme={theme}
            partyName={phase.manifest.partyName}
            footerText={phase.manifest.footerText}
            chosen={chosen}
            photoById={photoById}
            aspectOf={aspectOf}
            views={views}
            onAspect={noteAspect}
            orientation={orientation}
            paper={paper}
            overlayText={overlayText}
            overlayLogo={overlayLogo}
          />
          <p className="party-print-hint">{t('partyPrint.previewHelp', { paper: PAPER_LABEL[paper] })}</p>
          {product === 'twinStrip4' && (
            <p className="party-print-hint">{t('partyPrint.twinStrips')}</p>
          )}
          {/* Only for a single photograph: the four-photo strip is two strips
              side by side on a portrait sheet, and turning that sheet would not
              turn a picture, it would destroy the product. */}
          {product === 'photo' && (
            <fieldset className="party-print-themes">
              <legend>{t('partyPrint.orientation')}</legend>
              {(['portrait', 'landscape'] as const).map((option) => (
                <label key={option} className="party-print-theme">
                  <input
                    type="radio"
                    name="party-print-orientation"
                    value={option}
                    checked={(orientation ?? (aspectOf(chosen[0]) <= 1 ? 'portrait' : 'landscape'))
                      === option}
                    onChange={() => setOrientation(option)}
                  />
                  <span>{t(`partyPrint.orientation.${option}`)}</span>
                </label>
              ))}
            </fieldset>
          )}
          <fieldset className="party-print-themes">
            <legend>{t('partyPrint.theme')}</legend>
            {(product === 'photo' ? PHOTO_LOOKS : FRAMED_LOOKS).map((option) => (
              <label key={option} className="party-print-theme">
                <input
                  type="radio"
                  name="party-print-theme"
                  value={option}
                  checked={look === option || (option === 'pure' && look === 'overlay' && product !== 'photo')}
                  onChange={() => setLook(option)}
                />
                <span>{t(LOOK_LABEL[option])}</span>
              </label>
            ))}
          </fieldset>
          {/* Only for "On the photo": two independent choices, each a group of
              option cards over real radio inputs, sized for a thumb. */}
          {product === 'photo' && look === 'overlay' && (
            <div className="party-print-overlay-options">
              <fieldset className="party-print-choice">
                <legend>{t('partyPrint.overlayText')}</legend>
                <div className="party-print-choice-grid">
                  {OVERLAY_TEXTS.map((option) => (
                    <label key={option} className="party-print-option">
                      <input
                        type="radio"
                        className="party-print-option-input"
                        name="party-print-overlay-text"
                        value={option}
                        checked={overlayText === option}
                        onChange={() => setOverlayText(option)}
                      />
                      <span
                        className="party-print-swatch"
                        aria-hidden="true"
                        style={{ backgroundColor: OVERLAY_TEXT[option].ink }}
                      />
                      <span>{t(`partyPrint.overlayText.${option}`)}</span>
                    </label>
                  ))}
                </div>
              </fieldset>
              <fieldset className="party-print-choice">
                <legend>{t('partyPrint.overlayLogo')}</legend>
                <div className="party-print-choice-grid">
                  {OVERLAY_LOGOS.map((option) => (
                    <label key={option} className="party-print-option">
                      <input
                        type="radio"
                        className="party-print-option-input"
                        name="party-print-overlay-logo"
                        value={option}
                        checked={overlayLogo === option}
                        onChange={() => setOverlayLogo(option)}
                      />
                      <span className="party-print-swatch party-print-swatch-mark" aria-hidden="true">
                        <img src={OVERLAY_LOGO[option].swatch} alt="" />
                      </span>
                      <span>{t(`partyPrint.overlayLogo.${option}`)}</span>
                    </label>
                  ))}
                </div>
              </fieldset>
            </div>
          )}
          {submitError && (
            <p className="party-print-error" role="alert">{t(submitError)}</p>
          )}
          <div className="party-print-actions">
            <button
              type="button"
              className="party-print-secondary"
              disabled={submitting}
              onClick={() => { setCropIndex(chosen.length - 1); setStep('crop'); }}
            >
              {t('partyPrint.previous')}
            </button>
            <button
              type="button"
              className="party-print-primary"
              disabled={submitting}
              onClick={() => { void submit(); }}
            >
              {t(submitting ? 'partyPrint.submitting' : 'partyPrint.submit')}
            </button>
          </div>
        </section>
      )}
    </PrintShell>
  );
}

function BackLink() {
  const { t } = useI18n();
  // Only when the hub actually left its path behind in this tab. A studio
  // opened cold has no album it is allowed to address, and an exit that goes
  // nowhere is worse than no exit at all.
  const home = recallPartyHome();
  if (!home) return null;
  return (
    <p className="party-print-back">
      <Link to={home}>{t('partyPrint.back')}</Link>
    </p>
  );
}

function PrintShell({ title, children }: { title?: string; children: ReactNode }) {
  const { t } = useI18n();
  return (
    <main className="party-guest-hub party-print">
      <div className="party-guest-hub-topbar">
        <img
          className="party-guest-hub-logo"
          src={PARTY_WORDMARK_DARK}
          alt={PRODUCT_NAME}
          width={480}
          height={135}
        />
        <LanguageSwitcher className="language-switcher language-switcher-public" compact />
      </div>
      <header className="party-print-head">
        <p className="party-guest-hub-eyebrow">{PARTY_EYEBROW}</p>
        <h1 className="party-print-title">{title ?? t('partyPrint.title')}</h1>
      </header>
      {children}
    </main>
  );
}
