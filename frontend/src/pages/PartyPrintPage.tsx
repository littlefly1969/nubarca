import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getPartyPrintManifest,
  getPartyPrintStatus,
  submitPartyPrint,
  type PartyPrintAccepted,
  type PartyPrintFormat,
  type PartyPrintManifest,
  type PartyPrintState,
  type PartyPrintTheme,
  type PartyPrintOverlayText,
  type PartyPrintOverlayLogo,
} from '@nubarca/api-client';
import type { PrintLayout } from '@nubarca/contracts';
import { useI18n, type MessageKey } from '../i18n';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';
import { recallFaceFilter, recallPartyHome } from './partyGuestMemo';
import {
  PAPER_LABEL, PrintStudio, useFitLine,
  type FullBleedSheet, type StudioFormat, type StudioSubmission,
} from '../print/studio/PrintStudio';
import {
  SLOTS_PER_STRIP, type PaperSize,
  OVERLAY_LINE_FRACTION, OVERLAY_MARGIN_FRACTION, OVERLAY_SYMBOL_FRACTION, OVERLAY_TITLE_FRACTION,
  OVERLAY_SYMBOL_GAP_FRACTION,
  OVERLAY_NUMBER_FRACTION, OVERLAY_TEXT_SUPPORT_PADDING_FRACTION, OVERLAY_HALO_BLUR_FRACTION,
  OVERLAY_HALO_OPACITY, OVERLAY_NUMBER_ROOM, PARTY_NAME_MAX_LENGTH, FOOTER_MAX_LENGTH,
  overlayTextSupport, printedLine,
} from './partyPrintGeometry';

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

/**
 * The words of the title-on-the-photo preview, laid out as the renderer lays
 * them out: the name and the host's line cut to what the paper prints, the
 * bottom line sharing its width with the room kept for the number, and each
 * line fitted into what is left.
 */
function OverlayWords({ partyName, footerText, orientation, symbol }: {
  partyName: string; footerText: string | null; orientation: string;
  /** The NubArca symbol, set on the name's line just before the name. */
  symbol: ReactNode;
}) {
  const name = printedLine(partyName, PARTY_NAME_MAX_LENGTH);
  const footer = printedLine(footerText ?? '', FOOTER_MAX_LENGTH);
  // The renderer's floors: the name as small as it must be, the line to 3/4.
  const nameRef = useFitLine('var(--title-size)', 0, [name, footer, orientation]);
  const lineRef = useFitLine('var(--line-size)', 0.75, [footer, orientation]);
  // The symbol first, centred on the name's capitals, then the name in what
  // it leaves — as the renderer lays the line out.
  const nameLine = (
    <span className="party-print-overlay-head">
      {symbol}
      <span ref={nameRef} className="party-print-overlay-name">{name}</span>
    </span>
  );
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

/** The party's title on a full-bleed photograph: its support, its words, its symbol. */
function OverlayBlock({ sheet, partyName, footerText, overlayText, overlayLogo }: {
  sheet: FullBleedSheet;
  partyName: string;
  footerText: string | null;
  overlayText: PartyPrintOverlayText;
  overlayLogo: PartyPrintOverlayLogo;
}) {
  const { width: w, height: h } = sheet;
  const short = Math.min(w, h);
  // Everything is a fraction of the short edge, turned into a fraction of the
  // sheet's width, exactly as the renderer measures it.
  const ofWidth = (fraction: number) => `${((fraction * short) / w) * 100}%`;
  const text = OVERLAY_TEXT[overlayText];
  return (
    // The support is the words' own box: anchored to the foot, it begins one
    // padding above the real block of text, whatever the words turn out to be,
    // and the photograph above it keeps every pixel.
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
          ['--symbol-size' as string]: `${((OVERLAY_SYMBOL_FRACTION * short) / w) * 100}cqw`,
          ['--symbol-gap' as string]: `${((OVERLAY_SYMBOL_GAP_FRACTION * short) / w) * 100}cqw`,
        }}
      >
        <OverlayWords
          partyName={partyName}
          footerText={footerText}
          orientation={sheet.portrait ? 'portrait' : 'landscape'}
          symbol={(
            <img
              className="party-print-overlay-symbol"
              data-testid="party-print-overlay-symbol"
              src={OVERLAY_LOGO[overlayLogo].mark}
              alt=""
              aria-hidden="true"
            />
          )}
        />
      </div>
    </div>
  );
}

export function PartyPrintPage() {
  const { token } = useParams<{ token: string }>();
  const { t, tn } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });

  const [look, setLook] = useState<Look>('pure');
  const [overlayText, setOverlayText] = useState<PartyPrintOverlayText>('white');
  const [overlayLogo, setOverlayLogo] = useState<PartyPrintOverlayLogo>('light');
  // The theme the server is sent: the overlay exists only for a single photograph.
  const themeFor = useCallback((layout: PrintLayout): PartyPrintTheme => (look === 'overlay'
    ? (layout === 'photo' ? 'overlay' : 'pure')
    : look), [look]);
  const [onlyMine, setOnlyMine] = useState(false);

  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<MessageKey | null>(null);
  // Why the guest is back at the formats (another paper went in).
  const [formatNotice, setFormatNotice] = useState<MessageKey | null>(null);
  const [resetSignal, setResetSignal] = useState(0);
  const [sent, setSent] = useState<Sent | null>(null);

  const refreshManifest = useCallback(() => {
    // The budgets moved while this guest was composing, so re-read them rather
    // than offering a count that is already out of date.
    if (!token) return;
    getPartyPrintManifest(token)
      .then((fresh) => setPhase({ kind: 'ready', manifest: fresh }))
      .catch(() => { /* Keep what we have; the next submit is authoritative. */ });
  }, [token]);

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

  // The paper is a fact about the printer: every product on offer is a sheet of it.
  const paper: PaperSize = manifest?.paperSize ?? '10x15';
  const printable = useMemo(() => manifest?.formats.filter((f) => f.enabled) ?? [], [manifest]);
  const anyLeft = printable.some((f) => leftFor(f) > 0);
  const gallery = onlyMine && mine.length > 0 ? mine : (manifest?.photos ?? []);

  // Each product as offered — a format with a choice of how many photographs,
  // the twin strip (the same four twice, or eight), as each of them.
  const formats = useMemo<StudioFormat[]>(() => printable.flatMap((option) => (
    [...(option.photoCounts && option.photoCounts.length > 1 ? option.photoCounts : [option.requiredPhotos])]
      .sort((a, b) => a - b)
      .map((count): StudioFormat => {
        const left = leftFor(option);
        const out = left <= 0;
        const same = option.type === 'twinStrip4' && count === SLOTS_PER_STRIP;
        return {
          layout: option.type,
          count,
          testId: count === option.requiredPhotos
            ? `party-print-format-${option.type}`
            : `party-print-format-${option.type}-${count}`,
          label: same
            ? t('partyPrint.format.twinStrip4Same')
            : t(`partyPrint.format.${option.type}`, { paper: PAPER_LABEL[paper] }),
          help: same
            ? t('partyPrint.format.twinStrip4SameHelp')
            : t(`partyPrint.format.${option.type}Help`),
          left: out
            ? t(yoursIsBinding(option) ? 'partyPrint.yoursDone' : 'partyPrint.exhausted')
            : yoursIsBinding(option) ? tn(left, 'partyPrint.yoursLeft') : tn(left, 'partyPrint.remaining'),
          exhausted: out,
        };
      })
  )), [printable, leftFor, yoursIsBinding, paper, t, tn]);

  const startOver = () => {
    setSent(null);
    setSubmitError(null);
    refreshManifest();
  };

  const submit = async (composition: StudioSubmission) => {
    if (!token) return;
    const theme = themeFor(composition.layout);
    setSubmitting(true);
    setSubmitError(null);
    try {
      const accepted = await submitPartyPrint(
        token,
        {
          product: composition.layout,
          theme,
          slots: composition.slots,
          // Omitted when the guest left the default: the server follows the
          // photograph, which is what it did before this choice existed.
          ...(composition.orientation ? { orientation: composition.orientation } : {}),
          ...(theme === 'overlay' ? { overlayText, overlayLogo } : {}),
          // The paper this sheet was composed for: if the printer's is no
          // longer it, nothing is printed and the studio starts again.
          paperSize: paper,
        },
        composition.key);
      setSent({ accepted, state: 'preparing' });
    } catch (err: unknown) {
      const refusal = refusalKey(err);
      if (refusal === 'partyPrint.error.paperChanged') {
        // Another paper is in: what can be made has changed, so begin again
        // from the products the new paper offers, saying why.
        setResetSignal((n) => n + 1);
        setFormatNotice(refusal);
        refreshManifest();
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
    const yours = printable.length > 0 && printable.every(yoursIsBinding);
    return (
      <PrintShell title={phase.manifest.partyName}>
        <p className="party-print-note">
          {t(yours ? 'partyPrint.yoursAllDone' : 'partyPrint.allExhausted')}
        </p>
        <p className="party-print-hint">
          {t(yours ? 'partyPrint.yoursAllDoneHelp' : 'partyPrint.allExhaustedHelp')}
        </p>
        <BackLink />
      </PrintShell>
    );
  }

  return (
    <PrintShell title={phase.manifest.partyName}>
      <PrintStudio
        paper={paper}
        formats={formats}
        gallery={gallery}
        photoById={photoById}
        // Offered only when the guest's own search actually matched
        // photographs this token serves.
        galleryTools={mine.length > 0 ? (
          <button
            type="button"
            className="party-print-filter"
            aria-pressed={onlyMine}
            onClick={() => setOnlyMine((on) => !on)}
          >
            {t(onlyMine ? 'partyPrint.allPhotos' : 'partyPrint.onlyMine')}
          </button>
        ) : undefined}
        formatFooter={<BackLink />}
        formatNotice={formatNotice}
        photoStyle={look === 'overlay' ? 'fullBleed' : 'framed'}
        sheetTheme={themeFor}
        bandDark={(layout) => DARK_THEMES.includes(themeFor(layout))}
        paperColor={(layout) => PRINT_BAND[themeFor(layout)]}
        band={{ title: phase.manifest.partyName, line: phase.manifest.footerText, mark: null, brand: true }}
        fullBleedWords={(sheet) => (
          <OverlayBlock
            sheet={sheet}
            partyName={phase.manifest.partyName}
            footerText={phase.manifest.footerText}
            overlayText={overlayText}
            overlayLogo={overlayLogo}
          />
        )}
        sheetData={{ 'data-overlay-text': overlayText, 'data-overlay-logo': overlayLogo }}
        compositionKey={JSON.stringify([look, overlayText, overlayLogo])}
        submitError={submitError ? t(submitError) : null}
        submitting={submitting}
        resetSignal={resetSignal}
        onSubmit={(composition) => { setFormatNotice(null); void submit(composition); }}
        options={(layout) => (
          <>
            <fieldset className="party-print-themes">
              <legend>{t('partyPrint.theme')}</legend>
              {(layout === 'photo' ? PHOTO_LOOKS : FRAMED_LOOKS).map((option) => (
                <label key={option} className="party-print-theme">
                  <input
                    type="radio"
                    name="party-print-theme"
                    value={option}
                    checked={look === option || (option === 'pure' && look === 'overlay' && layout !== 'photo')}
                    onChange={() => setLook(option)}
                  />
                  <span>{t(LOOK_LABEL[option])}</span>
                </label>
              ))}
            </fieldset>
            {/* Only for "On the photo": two independent choices, each a group
                of option cards over real radio inputs, sized for a thumb. */}
            {layout === 'photo' && look === 'overlay' && (
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
          </>
        )}
      />
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
