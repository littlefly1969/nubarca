import {
  useCallback, useEffect, useMemo, useRef, useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getPartyPrintManifest,
  getPartyPrintStatus,
  submitPartyPrint,
  type PartyPrintAccepted,
  type PartyPrintManifest,
  type PartyPrintPhoto,
  type PartyPrintProduct,
  type PartyPrintSlot,
  type PartyPrintState,
  type PartyPrintTheme,
} from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';
import { recallFaceFilter, recallPartyHome } from './partyGuestMemo';
import {
  DEFAULT_CROP_VIEW, MAX_ZOOM, SLOTS_PER_STRIP, STRIPS_PER_SHEET,
  CUT_MARK_LENGTH_FRACTION, PORTRAIT_HEIGHT, PORTRAIT_WIDTH,
  type CropView, cropFor, photoLayout, photoSlotAspect, stripFooter, stripSlot,
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

/** Themes, in the order they are offered. Same three the renderer knows. */
const THEMES: readonly PartyPrintTheme[] = ['pure', 'midnight', 'event'] as const;
const THEME_LABEL: Record<PartyPrintTheme, MessageKey> = {
  pure: 'partyPrint.theme.pure',
  midnight: 'partyPrint.theme.midnight',
  event: 'partyPrint.theme.event',
};
/** Which wordmark an artwork this dark takes. Mirrors the renderer's palette. */
const DARK_THEMES: readonly PartyPrintTheme[] = ['midnight', 'event'] as const;

const STATE_LABEL: Record<PartyPrintState, MessageKey> = {
  preparing: 'partyPrint.state.preparing',
  queued: 'partyPrint.state.queued',
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
    case 'printer_unavailable': return 'partyPrint.error.printer';
    case 'render_failed': return 'partyPrint.error.render';
    case 'invalid_source': return 'partyPrint.error.source';
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
 * The same crop maths drives this and the print, so what a guest arranges here
 * is what the paper gets.
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
  const crop = cropFor(aspect, slotAspect, view);
  return (
    <img
      className="party-print-framed"
      src={photo.previewUrl}
      alt=""
      // A single photograph's sheet is turned to match it, so the preview keeps
      // learning shapes here too rather than only from the chooser's thumbnails.
      onLoad={(event) => onAspect?.(
        event.currentTarget.naturalWidth, event.currentTarget.naturalHeight)}
      style={{
        width: `${100 / crop.cropWidth}%`,
        height: `${100 / crop.cropHeight}%`,
        left: `${(-crop.cropX * 100) / crop.cropWidth}%`,
        top: `${(-crop.cropY * 100) / crop.cropHeight}%`,
      }}
    />
  );
}

/** The party line and the wordmark, in the band the renderer reserves. */
function SheetFooter({
  partyName, footerText, theme,
}: {
  partyName: string;
  footerText: string | null;
  theme: PartyPrintTheme;
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
          className="party-print-sheet-mark"
          src={dark ? PARTY_WORDMARK_DARK : PARTY_WORDMARK_LIGHT}
          alt={PRODUCT_NAME}
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
}

function pct(value: number): string {
  return `${value * 100}%`;
}

function SheetPreview(props: SheetProps) {
  const { product, theme, chosen, photoById, aspectOf, views, onAspect } = props;
  const viewOf = (id: string) => views[id] ?? DEFAULT_CROP_VIEW;

  if (product === 'photo') {
    const id = chosen[0];
    const aspect = aspectOf(id);
    // The sheet follows the photograph, exactly as the renderer decides it.
    const portrait = aspect <= 1;
    const { sheetWidth, sheetHeight, slot, footer } = photoLayout(portrait);
    const slotAspect = photoSlotAspect(portrait);
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

  const slotAspect = stripSlotAspect();
  return (
    <div
      className="party-print-sheet"
      data-theme={theme}
      data-testid="party-print-sheet"
      data-orientation="portrait"
      style={{ aspectRatio: `${PORTRAIT_WIDTH} / ${PORTRAIT_HEIGHT}` }}
    >
      {/* TWO IDENTICAL STRIPS on one sheet: one to keep, one to give away. */}
      {Array.from({ length: STRIPS_PER_SHEET }, (_, strip) => (
        <div
          key={strip}
          data-testid={`party-print-strip-${strip}`}
          // The second strip is a copy of the first, so it is drawn but not
          // announced: a screen reader would otherwise read the whole
          // composition twice. The caption under the sheet is what says there
          // are two of them.
          aria-hidden={strip > 0 ? true : undefined}
        >
          {Array.from({ length: SLOTS_PER_STRIP }, (_, index) => {
            const rect = stripSlot(strip, index);
            const id = chosen[index];
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
            <SheetFooter {...props} />
          </div>
        </div>
      ))}
      {/* Ticks at the ends of the gutter only — where the sheet is cut. */}
      <span
        className="party-print-cut party-print-cut-top"
        style={{ height: pct(CUT_MARK_LENGTH_FRACTION) }}
        aria-hidden="true"
      />
      <span
        className="party-print-cut party-print-cut-bottom"
        style={{ height: pct(CUT_MARK_LENGTH_FRACTION) }}
        aria-hidden="true"
      />
    </div>
  );
}

// --- Framing one photograph -------------------------------------------------

/** How far one arrow key moves the photograph, as a fraction of the source. */
const NUDGE = 0.02;

function CropFrame({
  photo, aspect, slotAspect, view, onChange, label,
}: {
  photo: PartyPrintPhoto;
  aspect: number;
  slotAspect: number;
  view: CropView;
  onChange: (view: CropView) => void;
  label: string;
}) {
  const frameRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ x: number; y: number } | null>(null);
  const crop = cropFor(aspect, slotAspect, view);

  const nudge = (dx: number, dy: number) => onChange({
    ...view,
    centerX: Math.min(1, Math.max(0, view.centerX + dx)),
    centerY: Math.min(1, Math.max(0, view.centerY + dy)),
  });

  const onKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    // Precision without a pointer: the same movement a drag makes, in steps.
    const step = event.shiftKey ? NUDGE * 4 : NUDGE;
    switch (event.key) {
      case 'ArrowLeft': nudge(-step, 0); break;
      case 'ArrowRight': nudge(step, 0); break;
      case 'ArrowUp': nudge(0, -step); break;
      case 'ArrowDown': nudge(0, step); break;
      default: return;
    }
    event.preventDefault();
  };

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    dragRef.current = { x: event.clientX, y: event.clientY };
    event.currentTarget.setPointerCapture?.(event.pointerId);
  };

  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const start = dragRef.current;
    const frame = frameRef.current;
    if (!start || !frame) return;
    const box = frame.getBoundingClientRect();
    if (box.width === 0 || box.height === 0) return;
    // A pixel of drag moves the photograph by that fraction of what is visible,
    // so the picture tracks the finger however far it is zoomed in.
    nudge(
      (-(event.clientX - start.x) * crop.cropWidth) / box.width,
      (-(event.clientY - start.y) * crop.cropHeight) / box.height,
    );
    dragRef.current = { x: event.clientX, y: event.clientY };
  };

  const endDrag = () => { dragRef.current = null; };

  return (
    <div
      ref={frameRef}
      className="party-print-crop"
      style={{ aspectRatio: `${slotAspect}` }}
      tabIndex={0}
      role="group"
      aria-label={label}
      onKeyDown={onKeyDown}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
      data-testid="party-print-crop"
    >
      <img
        className="party-print-framed"
        src={photo.previewUrl}
        alt=""
        draggable={false}
        style={{
          width: `${100 / crop.cropWidth}%`,
          height: `${100 / crop.cropHeight}%`,
          left: `${(-crop.cropX * 100) / crop.cropWidth}%`,
          top: `${(-crop.cropY * 100) / crop.cropHeight}%`,
        }}
      />
    </div>
  );
}

export function PartyPrintPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });

  const [step, setStep] = useState<Step>('format');
  const [product, setProduct] = useState<PartyPrintProduct | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [views, setViews] = useState<Record<string, CropView>>({});
  const [aspects, setAspects] = useState<Record<string, number>>({});
  const [theme, setTheme] = useState<PartyPrintTheme>('pure');
  const [cropIndex, setCropIndex] = useState(0);
  const [onlyMine, setOnlyMine] = useState(false);

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
  useEffect(() => { pendingRef.current = null; }, [product, chosen, views, theme]);

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

  const format = manifest?.formats.find((f) => f.type === product) ?? null;
  const required = format?.requiredPhotos ?? 1;
  const printable = manifest?.formats.filter((f) => f.enabled) ?? [];
  const anyLeft = printable.some((f) => f.remaining > 0);

  const gallery = onlyMine && mine.length > 0 ? mine : (manifest?.photos ?? []);

  // The natural shape of a photograph, learned when its preview loads. Until
  // then it is treated as filling its slot exactly, which crops nothing.
  const aspectOf = useCallback((id: string) => aspects[id] ?? 0, [aspects]);
  const noteAspect = useCallback((id: string, width: number, height: number) => {
    if (width <= 0 || height <= 0) return;
    setAspects((prev) => (prev[id] ? prev : { ...prev, [id]: width / height }));
  }, []);

  const slotAspectFor = useCallback((id: string) => (
    product === 'strip4' ? stripSlotAspect() : photoSlotAspect(aspectOf(id) <= 1)
  ), [product, aspectOf]);

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
        const crop = cropFor(aspectOf(id), slotAspectFor(id), views[id] ?? DEFAULT_CROP_VIEW);
        return { itemId: id, ...crop };
      }),
    };
    const pending = pendingRef.current;
    setSubmitting(true);
    setSubmitError(null);
    try {
      const accepted = await submitPartyPrint(
        token, { product, theme, slots: pending.slots }, pending.key);
      setSent({ accepted, state: 'preparing' });
    } catch (err: unknown) {
      setSubmitError(refusalKey(err));
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
    return (
      <PrintShell title={phase.manifest.partyName}>
        <p className="party-print-note">{t('partyPrint.allExhausted')}</p>
        <p className="party-print-hint">{t('partyPrint.allExhaustedHelp')}</p>
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
          <ul className="party-print-formats">
            {printable.map((option) => {
              const out = option.remaining <= 0;
              return (
                <li key={option.type}>
                  <button
                    type="button"
                    className="party-print-format"
                    data-testid={`party-print-format-${option.type}`}
                    data-exhausted={out ? 'true' : undefined}
                    disabled={out}
                    onClick={() => {
                      setProduct(option.type);
                      setChosen([]);
                      setStep('select');
                    }}
                  >
                    <span className="party-print-format-icon" aria-hidden="true">
                      {option.type === 'strip4' ? <StripIcon /> : <PrinterIcon />}
                    </span>
                    <span className="party-print-format-text">
                      <strong>
                        {t(option.type === 'strip4'
                          ? 'partyPrint.format.strip4'
                          : 'partyPrint.format.photo')}
                      </strong>
                      <span className="party-print-format-help">
                        {t(option.type === 'strip4'
                          ? 'partyPrint.format.strip4Help'
                          : 'partyPrint.format.photoHelp')}
                      </span>
                    </span>
                    <span className="party-print-format-left">
                      {out ? t('partyPrint.exhausted') : tn(option.remaining, 'partyPrint.remaining')}
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
            {t(product === 'strip4' ? 'partyPrint.selectStrip' : 'partyPrint.selectPhoto')}
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
              onClick={() => setStep(product === 'strip4' ? 'arrange' : 'crop')}
            >
              {t('partyPrint.continue')}
            </button>
          </div>
        </section>
      )}

      {step === 'arrange' && (
        <section className="party-print-step" aria-labelledby="party-print-heading">
          <h2 className="party-print-heading" id="party-print-heading">
            {t('partyPrint.step.arrange')}
          </h2>
          <p className="party-print-hint">{t('partyPrint.arrangeHelp')}</p>
          {/* Reordering is BUTTONS, not only dragging: a strip's order is part
              of the composition, and it must be reachable by keyboard, by
              screen reader and by anyone who cannot hold a drag. */}
          <ol className="party-print-order">
            {chosen.map((id, index) => (
              <li key={id} className="party-print-order-row">
                <span className="party-print-order-index" aria-hidden="true">{index + 1}</span>
                <img src={photoById.get(id)?.thumbnailUrl} alt="" />
                <span className="party-print-order-label">
                  {t('partyPrint.position', { n: index + 1 })}
                </span>
                <span className="party-print-order-buttons">
                  <button
                    type="button"
                    disabled={index === 0}
                    aria-label={`${t('partyPrint.moveUp')} — ${t('partyPrint.position', { n: index + 1 })}`}
                    onClick={() => move(index, -1)}
                  >
                    {t('partyPrint.moveUp')}
                  </button>
                  <button
                    type="button"
                    disabled={index === chosen.length - 1}
                    aria-label={`${t('partyPrint.moveDown')} — ${t('partyPrint.position', { n: index + 1 })}`}
                    onClick={() => move(index, 1)}
                  >
                    {t('partyPrint.moveDown')}
                  </button>
                </span>
              </li>
            ))}
          </ol>
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
              <CropFrame
                photo={photo}
                aspect={aspectOf(id)}
                slotAspect={slotAspectFor(id)}
                view={view}
                label={t('partyPrint.cropHelp')}
                onChange={(next) => setView(id, next)}
              />
            )}
            <p className="party-print-hint">{t('partyPrint.cropHelp')}</p>
            <label className="party-print-zoom">
              <span>{t('partyPrint.zoom')}</span>
              <input
                type="range"
                min={1}
                max={MAX_ZOOM}
                step={0.05}
                value={view.zoom}
                onChange={(event) => setView(id, { ...view, zoom: Number(event.target.value) })}
              />
            </label>
            <button
              type="button"
              className="party-print-secondary"
              onClick={() => setView(id, DEFAULT_CROP_VIEW)}
            >
              {t('partyPrint.resetCrop')}
            </button>
            <div className="party-print-actions">
              <button
                type="button"
                className="party-print-secondary"
                onClick={() => {
                  if (cropIndex > 0) setCropIndex(cropIndex - 1);
                  else setStep(product === 'strip4' ? 'arrange' : 'select');
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
          />
          <p className="party-print-hint">{t('partyPrint.previewHelp')}</p>
          {product === 'strip4' && (
            <p className="party-print-hint">{t('partyPrint.twinStrips')}</p>
          )}
          <fieldset className="party-print-themes">
            <legend>{t('partyPrint.theme')}</legend>
            {THEMES.map((option) => (
              <label key={option} className="party-print-theme">
                <input
                  type="radio"
                  name="party-print-theme"
                  value={option}
                  checked={theme === option}
                  onChange={() => setTheme(option)}
                />
                <span>{t(THEME_LABEL[option])}</span>
              </label>
            ))}
          </fieldset>
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
