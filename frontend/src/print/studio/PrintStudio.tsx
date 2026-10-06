import {
  useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode,
} from 'react';
import {
  effectiveZoom, placePhoto, visiblePhotoRect, type PhotoPlacement, type PrintLayout, type PrintStyle,
} from '@nubarca/contracts';
import { useI18n, type MessageKey } from '../../i18n';
import { PRODUCT_NAME } from '../../brand/brand';
import { PhotoCropFrame, photoPlacementStyle } from '../../party/PhotoCropFrame';
import { PhotoFramingControls } from '../../party/PhotoFramingControls';
import {
  DEFAULT_CROP_VIEW, SLOTS_PER_STRIP, STRIPS_PER_SHEET,
  PORTRAIT_HEIGHT, PORTRAIT_WIDTH, FOOTER_WORDMARK_WIDTH_FRACTION, STRIP_WORDMARK_WIDTH_FRACTION,
  type CropView, type PaperSize, gridLayout, gridPortrait, gridSlotAspect, overlaySlotAspect, photoLayout,
  photoSlotAspect, sheet, stripFooter, stripSlot, stripSlotAspect,
} from '../../pages/partyPrintGeometry';
import '../../pages/PartyPrintPage.css';

/* THE PRINT STUDIO — one path to a printed sheet, for a party's guest and for
   an owner printing from their album: choose the format, choose the
   photographs, put them in order, frame each one, look at the sheet, send it.

   The steps, the framing and the preview are this component's, and the sheet
   it previews is laid out by the shared catalogue (@nubarca/contracts
   printLayouts) the server draws with. What differs between a party and an
   album is handed in, never forked: which formats are on offer and what each
   has left, the photographs to choose from, the words in a framed sheet's band,
   the words over a full-bleed photograph, the choices under the preview, and
   what happens to the composition when it is sent. */

const WORDMARK_DARK = '/brand/nubarca-wordmark-on-dark-480w.png';
// The COMPACT light rendition, not the master: "both themes share one visible
// geometry" — which is precisely what a footer band needs, and what the
// renderer draws.
const WORDMARK_LIGHT = '/brand/nubarca-wordmark-on-light-480w.png';

export interface StudioPhoto { id: string; thumbnailUrl: string; previewUrl: string }

/** One way to print, as offered: a format of the catalogue and how many photographs it takes. */
export interface StudioFormat {
  layout: PrintLayout;
  count: number;
  testId: string;
  label: string;
  help: string;
  /** What is left of it, already in words — absent where nothing bounds it. */
  left: string | null;
  exhausted: boolean;
}

/** A framed sheet's band: title above, a line under it, the wordmark left and a mark right. */
export interface StudioBand {
  title: string | null;
  line: string | null;
  /** The text at the right of the signature row (an owner's date); a guest's number is never previewed. */
  mark: string | null;
  brand: boolean;
}

export interface StudioSlot { itemId: string; placement: PhotoPlacement }

export interface StudioSubmission {
  layout: PrintLayout;
  slots: StudioSlot[];
  /** A single photograph's sheet: null follows the photograph. */
  orientation: 'portrait' | 'landscape' | null;
  /** One key per composition: a retry of the same sheet keeps it, any change earns a new one. */
  key: string;
}

/** What a full-bleed sheet looks like to the words drawn over it. */
export interface FullBleedSheet {
  portrait: boolean;
  width: number;
  height: number;
  /** The visible photograph, in fractions of the sheet. */
  visible: { left: number; top: number; width: number; height: number };
}

export type StudioStep = 'format' | 'select' | 'arrange' | 'crop' | 'preview';

/** A paper as the photo trade writes it. */
export const PAPER_LABEL: Record<PaperSize, string> = {
  '10x15': '10×15',
  '13x18': '13×18',
  '20x15': '20×15',
};

/** Where each of the four sits on its sheet, in reading order. */
const GRID_POSITION: readonly MessageKey[] = [
  'partyPrint.gridPosition.topLeft', 'partyPrint.gridPosition.topRight',
  'partyPrint.gridPosition.bottomLeft', 'partyPrint.gridPosition.bottomRight',
] as const;

export function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return globalThis.crypto.randomUUID();
  // Older WebViews: still unique enough to distinguish one person's compositions.
  return `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

export function pct(value: number): string {
  return `${value * 100}%`;
}

/**
 * The renderer's FitLine for the preview: a line wider than its box shrinks —
 * never below `floor` of its size — before the ellipsis takes the rest. The
 * sizes are container units, so the ratio found once holds at any width.
 */
export function useFitLine(size: string, floor: number, deps: readonly unknown[]) {
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
 * The same placement maths drives this and the print, so what is arranged here
 * is what the paper gets — zoomed out, the slot's own paper beside it.
 */
function FramedPhoto({
  photo, aspect, slotAspect, view, onAspect,
}: {
  photo: StudioPhoto | undefined;
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

/** The band's words and the wordmark, where the renderer reserves them. */
function SheetBand({ band, dark, strip = false }: {
  band: StudioBand;
  dark: boolean;
  /** A strip's wordmark stands as wide as its narrow row allows, as on paper. */
  strip?: boolean;
}) {
  return (
    // A strip's narrow foot keeps its words' size; under a photograph or four
    // they are a touch larger, as on paper.
    <div className={strip ? 'party-print-sheet-footer is-strip' : 'party-print-sheet-footer'}>
      <span className="party-print-sheet-text">
        {band.title && <span className="party-print-sheet-name">{band.title}</span>}
        {band.line && <span className="party-print-sheet-line">{band.line}</span>}
      </span>
      {/* Bottom-left the wordmark, bottom-right the mark — where the renderer
          puts them. A guest's queue number is never previewed: it does not
          exist until the print is accepted. */}
      <span className="party-print-sheet-sign">
        {band.brand && (
          <img
            className={strip ? 'party-print-sheet-mark party-print-sheet-mark-strip' : 'party-print-sheet-mark'}
            src={dark ? WORDMARK_DARK : WORDMARK_LIGHT}
            alt={PRODUCT_NAME}
            style={{ width: pct(strip ? STRIP_WORDMARK_WIDTH_FRACTION : FOOTER_WORDMARK_WIDTH_FRACTION) }}
          />
        )}
        {band.mark && <span className="party-print-sheet-date" data-testid="print-sheet-mark">{band.mark}</span>}
      </span>
    </div>
  );
}

interface SheetProps {
  layout: PrintLayout;
  photoStyle: PrintStyle;
  sheetTheme: string;
  bandDark: boolean;
  band: StudioBand;
  fullBleedWords?: (sheet: FullBleedSheet) => ReactNode;
  sheetData?: Record<string, string>;
  chosen: string[];
  photoById: Map<string, StudioPhoto>;
  aspectOf: (id: string) => number;
  views: Record<string, CropView>;
  onAspect: (id: string, width: number, height: number) => void;
  orientation: 'portrait' | 'landscape' | null;
  paper: PaperSize;
}

function SheetPreview(props: SheetProps) {
  const {
    layout, photoStyle, sheetTheme, chosen, photoById, aspectOf, views, onAspect, orientation, paper,
  } = props;
  const viewOf = (id: string) => views[id] ?? DEFAULT_CROP_VIEW;
  const bandFor = (strip = false) => <SheetBand band={props.band} dark={props.bandDark} strip={strip} />;

  if (layout === 'photo' && photoStyle === 'fullBleed') {
    const id = chosen[0];
    const aspect = aspectOf(id);
    const portrait = orientation === null ? aspect <= 1 : orientation === 'portrait';
    const [w, h] = sheet(paper, portrait);
    const slotAspect = overlaySlotAspect(portrait, paper);
    return (
      <div
        className="party-print-sheet party-print-sheet-overlay"
        data-theme={sheetTheme}
        data-testid="party-print-sheet"
        data-orientation={portrait ? 'portrait' : 'landscape'}
        {...props.sheetData}
        style={{ aspectRatio: `${w} / ${h}` }}
      >
        <div className="party-print-slot" style={{ left: 0, top: 0, width: '100%', height: '100%' }}>
          <FramedPhoto
            photo={photoById.get(id)} aspect={aspect}
            slotAspect={slotAspect} view={viewOf(id)}
            onAspect={(width, height) => onAspect(id, width, height)}
          />
        </div>
        {props.fullBleedWords?.({
          portrait, width: w, height: h,
          visible: visiblePhotoRect(placePhoto(aspect, slotAspect, viewOf(id))),
        })}
      </div>
    );
  }

  if (layout === 'photo') {
    const id = chosen[0];
    const aspect = aspectOf(id);
    // The sheet follows the photograph unless it was turned — exactly the
    // decision the renderer makes, made here from the same inputs.
    const portrait = orientation === null ? aspect <= 1 : orientation === 'portrait';
    const { sheetWidth, sheetHeight, slot, footer } = photoLayout(portrait, paper);
    const slotAspect = photoSlotAspect(portrait, paper);
    return (
      <div
        className="party-print-sheet"
        data-theme={sheetTheme}
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
          {bandFor()}
        </div>
      </div>
    );
  }

  if (layout === 'grid4') {
    // Four frames, two by two, on the paper as it is named — the renderer's
    // own layout, in the order they were arranged.
    const { sheetWidth, sheetHeight, slots, footer } = gridLayout(paper);
    const slotAspect = gridSlotAspect(paper);
    return (
      <div
        className="party-print-sheet"
        data-theme={sheetTheme}
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
          {bandFor()}
        </div>
      </div>
    );
  }

  const slotAspect = stripSlotAspect();
  return (
    <div
      className="party-print-sheet"
      data-theme={sheetTheme}
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
          // Four photographs: the second strip is the first again, and says
          // nothing the first did not.
          aria-hidden={strip > 0 && chosen.length <= SLOTS_PER_STRIP ? 'true' : undefined}
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
            {bandFor(true)}
          </div>
        </div>
      ))}
      {/* No cut marks: the printer cuts the sheet, and the print has none. */}
    </div>
  );
}

// --- The studio -------------------------------------------------------------

export interface PrintStudioProps {
  /** The paper in the printer: every format on offer is a sheet of it. */
  paper: PaperSize;
  formats: StudioFormat[];
  /** The photographs to choose from — possibly a filtered view of `photoById`. */
  gallery: StudioPhoto[];
  /** Every photograph the studio may show, chosen or not. */
  photoById: Map<string, StudioPhoto>;
  /** Shown above the gallery (a party's "only mine"). */
  galleryTools?: ReactNode;
  /** Shown at the top of the format step (an owner's printer). */
  formatHeader?: ReactNode;
  /** Shown under the formats (a way back). */
  formatFooter?: ReactNode;
  /** Already chosen when the studio opens, in this order: taken, up to what a format takes. */
  preselected?: string[];
  /** A single photograph's style with the look chosen now; four and the strips are always framed. */
  photoStyle: PrintStyle;
  /** The preview sheet's palette (its data-theme) for a format, and whether its band is dark. */
  sheetTheme: (layout: PrintLayout) => string;
  bandDark: (layout: PrintLayout) => boolean;
  /** What a slot shows beside a photograph zoomed out. */
  paperColor: (layout: PrintLayout) => string;
  band: StudioBand;
  fullBleedWords?: (sheet: FullBleedSheet) => ReactNode;
  /** Extra attributes for a full-bleed preview sheet. */
  sheetData?: Record<string, string>;
  /** The preview step's own choices (looks, words…) for the format being composed. */
  options: (layout: PrintLayout) => ReactNode;
  /** Every choice of the page's that changes the sheet: a new one earns a new key. */
  compositionKey: string;
  /** Said on the format step: why the person is back there. */
  formatNotice?: MessageKey | null;
  submitError: string | null;
  submitting: boolean;
  /** False while something the sheet needs is not ready yet (a date being read). */
  canSubmit?: boolean;
  onSubmit: (submission: StudioSubmission) => void;
  /** Told the photographs chosen, in order, whenever they change (an owner's date is the first one's). */
  onChosenChange?: (chosen: string[]) => void;
  /** Changed by the page to start over from the format step. */
  resetSignal?: number;
}

export function PrintStudio(props: PrintStudioProps) {
  const {
    paper, formats, gallery, photoById, preselected, photoStyle, onSubmit,
  } = props;
  const { t } = useI18n();

  const [step, setStep] = useState<StudioStep>('format');
  const [chosenFormat, setChosenFormat] = useState<{ layout: PrintLayout; count: number } | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [views, setViews] = useState<Record<string, CropView>>({});
  const [aspects, setAspects] = useState<Record<string, number>>({});
  const [cropIndex, setCropIndex] = useState(0);
  // Null is not "unset waiting for a value" — it IS the default: follow the
  // photograph. Only a deliberate turn of the sheet leaves it.
  const [orientation, setOrientation] = useState<'portrait' | 'landscape' | null>(null);

  const layout = chosenFormat?.layout ?? null;
  const required = chosenFormat?.count ?? 1;
  // Four photographs on a twin strip: one strip, printed twice.
  const sameStrip = layout === 'twinStrip4' && required === SLOTS_PER_STRIP;

  // Start over when the page says so — another paper is in, a print was sent.
  const firstReset = useRef(true);
  useEffect(() => {
    if (firstReset.current) { firstReset.current = false; return; }
    setStep('format');
    setChosenFormat(null);
    setChosen([]);
    setViews({});
    setCropIndex(0);
    setOrientation(null);
  }, [props.resetSignal]);

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

  // The sheet as it was first sent: its idempotency key AND the exact slots
  // that key stands for, frozen together at the first attempt. Changing the
  // composition discards the pair and earns a new key.
  const pendingRef = useRef<{ key: string; slots: StudioSlot[] } | null>(null);
  useEffect(() => {
    pendingRef.current = null;
  }, [chosenFormat, chosen, views, orientation, props.compositionKey]);

  const { onChosenChange } = props;
  useEffect(() => { onChosenChange?.(chosen); }, [chosen, onChosenChange]);

  // The natural shape of a photograph, learned when its preview loads. Until
  // then it is treated as filling its slot exactly, which crops nothing.
  const aspectOf = useCallback((id: string) => aspects[id] ?? 0, [aspects]);
  const noteAspect = useCallback((id: string, width: number, height: number) => {
    if (width <= 0 || height <= 0) return;
    setAspects((prev) => (prev[id] ? prev : { ...prev, [id]: width / height }));
  }, []);

  /** Portrait unless the sheet was turned, or the photograph is wide. */
  const portraitFor = useCallback((id: string) => (
    orientation === null ? aspectOf(id) <= 1 : orientation === 'portrait'
  ), [orientation, aspectOf]);

  const slotAspectFor = useCallback((id: string) => (
    layout === 'twinStrip4'
      ? stripSlotAspect()
      : layout === 'grid4'
        ? gridSlotAspect(paper)
        : photoStyle === 'fullBleed'
          ? overlaySlotAspect(portraitFor(id), paper)
          : photoSlotAspect(portraitFor(id), paper)
  ), [layout, paper, photoStyle, portraitFor]);

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

  const submit = () => {
    if (!layout) return;
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
    onSubmit({
      layout,
      slots: pendingRef.current.slots,
      orientation: layout === 'photo' ? orientation : null,
      key: pendingRef.current.key,
    });
  };

  const known = useMemo(() => new Set(photoById.keys()), [photoById]);

  return (
    <>
      {step === 'format' && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.format')}
          </h2>
          {props.formatHeader}
          {/* Why the person is back here: another paper was put in while they
              composed, and this is what it can make. */}
          {props.formatNotice && (
            <p className="party-print-error" role="alert">{t(props.formatNotice)}</p>
          )}
          <ul className="party-print-formats">
            {formats.map((option) => (
              <li key={`${option.layout}-${option.count}`}>
                <button
                  type="button"
                  className="party-print-format"
                  data-testid={option.testId}
                  data-exhausted={option.exhausted ? 'true' : undefined}
                  disabled={option.exhausted}
                  onClick={() => {
                    setChosenFormat({ layout: option.layout, count: option.count });
                    // What was already chosen — from an album's selection —
                    // stays chosen, as far as the format takes.
                    setChosen((preselected ?? []).filter((id) => known.has(id)).slice(0, option.count));
                    setStep('select');
                  }}
                >
                  <span className="party-print-format-icon" aria-hidden="true">
                    {option.layout === 'twinStrip4'
                      ? <StripIcon />
                      : option.layout === 'grid4' ? <GridIcon /> : <PrinterIcon />}
                  </span>
                  <span className="party-print-format-text">
                    <strong>{option.label}</strong>
                    <span className="party-print-format-help">{option.help}</span>
                  </span>
                  {option.left !== null && <span className="party-print-format-left">{option.left}</span>}
                </button>
              </li>
            ))}
          </ul>
          {props.formatFooter}
        </section>
      )}

      {step === 'select' && layout && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t(layout === 'twinStrip4'
              ? (sameStrip ? 'partyPrint.selectStripSame' : 'partyPrint.selectStrip')
              : layout === 'grid4' ? 'partyPrint.selectGrid' : 'partyPrint.selectPhoto')}
          </h2>
          <p className="party-print-count" role="status">
            {t('partyPrint.chosen', { count: chosen.length, total: required })}
          </p>
          {props.galleryTools}
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
          {/* Pinned where a thumb is, so a person choosing from a long album
              never has to scroll to its end to go on. */}
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
                onClick={() => setStep(layout === 'photo' ? 'crop' : 'arrange')}
              >
                {t('partyPrint.continue')}
              </button>
            </div>
          </div>
        </section>
      )}

      {step === 'arrange' && layout && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.arrange')}
          </h2>
          <p className="party-print-hint">
            {t(layout === 'grid4'
              ? 'partyPrint.arrangeHelpGrid'
              : sameStrip ? 'partyPrint.arrangeHelpStripSame' : 'partyPrint.arrangeHelp')}
          </p>
          {(() => {
            const label = (index: number) => (layout === 'grid4'
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
                    aria-label={`${t('partyPrint.moveUp')} — ${label(index)}`}
                    onClick={() => move(index, -1)}
                  >
                    {t('partyPrint.moveUp')}
                  </button>
                  <button
                    type="button"
                    disabled={index === chosen.length - 1}
                    aria-label={`${t('partyPrint.moveDown')} — ${label(index)}`}
                    onClick={() => move(index, 1)}
                  >
                    {t('partyPrint.moveDown')}
                  </button>
                </span>
              </li>
            );
            if (sameStrip) {
              return (
                <section
                  className="party-print-order-group"
                  aria-labelledby="party-print-strip-heading-same"
                  data-testid="party-print-order-strip-same"
                >
                  <h3 className="party-print-order-heading" id="party-print-strip-heading-same">
                    {t('partyPrint.sameStrip')}
                  </h3>
                  <ol className="party-print-order">{chosen.map(row)}</ol>
                </section>
              );
            }
            // The twin strip is two strips: the first four on one, the next
            // four on the other — shown as the two groups they will be.
            return layout === 'twinStrip4'
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

      {step === 'crop' && layout && (() => {
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
                  // sheet's own paper.
                  background={props.paperColor(layout)}
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
                  else setStep(layout === 'photo' ? 'select' : 'arrange');
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

      {step === 'preview' && layout && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.preview')}
          </h2>
          <SheetPreview
            layout={layout}
            photoStyle={layout === 'photo' ? photoStyle : 'framed'}
            sheetTheme={props.sheetTheme(layout)}
            bandDark={props.bandDark(layout)}
            band={props.band}
            fullBleedWords={props.fullBleedWords}
            sheetData={props.sheetData}
            chosen={chosen}
            photoById={photoById}
            aspectOf={aspectOf}
            views={views}
            onAspect={noteAspect}
            orientation={orientation}
            paper={paper}
          />
          <p className="party-print-hint">{t('partyPrint.previewHelp', { paper: PAPER_LABEL[paper] })}</p>
          {layout === 'twinStrip4' && (
            <p className="party-print-hint">{t('partyPrint.twinStrips')}</p>
          )}
          {/* Only for a single photograph: the strips are two strips side by
              side on a portrait sheet, and turning that sheet would not turn a
              picture, it would destroy the format. */}
          {layout === 'photo' && (
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
          {props.options(layout)}
          {props.submitError && (
            <p className="party-print-error" role="alert">{props.submitError}</p>
          )}
          <div className="party-print-actions">
            <button
              type="button"
              className="party-print-secondary"
              disabled={props.submitting}
              onClick={() => { setCropIndex(chosen.length - 1); setStep('crop'); }}
            >
              {t('partyPrint.previous')}
            </button>
            <button
              type="button"
              className="party-print-primary"
              disabled={props.submitting || props.canSubmit === false}
              onClick={submit}
            >
              {t(props.submitting ? 'partyPrint.submitting' : 'partyPrint.submit')}
            </button>
          </div>
        </section>
      )}
    </>
  );
}
