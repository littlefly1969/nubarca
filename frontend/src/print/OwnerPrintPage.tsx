import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import {
  ApiError,
  PERMISSIONS,
  getAlbum,
  getOwnerPhotoPrintDate,
  listAlbumItems,
  listPrintStations,
  listSharedPrinters,
  submitOwnerPhotoPrint,
  type OwnerPhotoPrintAccepted,
  type OwnerPhotoPrintDate,
} from '@nubarca/api-client';
import { formatOwnerPrintDate, layoutAllowed, type PrintLayout } from '@nubarca/contracts';
import { useI18n, type MessageKey } from '../i18n';
import { usePermissions } from '../auth/usePermissions';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';
import { mediumPreviewUrl, smallThumbnailUrl } from '../components/files/types';
import { mediaRemainingLine } from '../cloud/PrinterMediaRemaining';
import { initialTarget, printTargets, type PrintTarget } from '../media/workspace/ownerPhotoPrintTargets';
import {
  PAPER_LABEL, PrintStudio,
  type FullBleedSheet, type StudioFormat, type StudioPhoto, type StudioSubmission,
} from './studio/PrintStudio';
import './OwnerPrintPage.css';

// PRINTING FROM AN ALBUM — or from the library's selection — on the owner's
// printer or one lent to them, through the SAME studio a party's guest uses:
// the format, the photographs, their order, their framing, the sheet.
//
// What is the owner's here: the printer is chosen, not configured by a host;
// a single photograph is Piena (to the edges, the date on it) or Cornice (the
// party's own frame, in white); a framed sheet's band carries one line of the
// owner's own, the date at its right, and the NubArca mark only if asked. No
// budget, no number, no guest.

/** The renderer's date on a full-bleed photograph: type a fraction of the short edge, inset from its corner. */
const DATE_TYPE_FRACTION = 0.03;
const DATE_INSET_FRACTION = 0.035;
/** The band's paper beside a zoomed-out photograph: the party's white for Cornice, the sheet's own for Piena. */
const FRAMED_PAPER = '#f5f7fb';
const FULL_BLEED_PAPER = '#ffffff';
const CAPTION_MAX_LENGTH = 40;

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** The server's rule for the owner's line, said before it is sent. */
function captionError(value: string): boolean {
  const flat = value.replace(/\s+/g, ' ').trim();
  return flat.length > CAPTION_MAX_LENGTH || /[\uD800-\uDFFF]/.test(flat);
}

function refusalOf(err: unknown): string | null {
  return err instanceof ApiError ? (err.body as { error?: string } | null)?.error ?? null : null;
}

const ERROR_KEYS: Record<string, MessageKey> = {
  paper_changed: 'ownerPrint.error.paperChanged',
  printer_offline: 'ownerPrint.error.offline',
  printer_unavailable: 'ownerPrint.error.unavailable',
  printer_not_found: 'ownerPrint.error.printerGone',
  share_revoked: 'ownerPrint.error.printerGone',
  share_exhausted: 'ownerPrint.error.shareExhausted',
  format_unsupported: 'ownerPrint.error.format',
  not_found: 'ownerPrint.error.photo',
  not_image: 'ownerPrint.error.photo',
  invalid_source: 'ownerPrint.error.photo',
  invalid_caption: 'ownerPrint.error.caption',
  render_failed: 'ownerPrint.error.render',
};

/** Refusals after which what the printer list says is no longer true. */
const REREAD_PRINTERS = new Set([
  'paper_changed', 'printer_offline', 'printer_unavailable', 'printer_not_found', 'share_revoked',
  'share_exhausted', 'format_unsupported',
]);

/**
 * The date as the renderer draws it on a full-bleed photograph: white type
 * with a soft dark halo, right- and bottom-aligned at an inset from the VISIBLE
 * photograph's corner, in container units of the sheet.
 */
function PrintedDate({ text, sheet }: { text: string; sheet: FullBleedSheet }) {
  const short = Math.min(sheet.width, sheet.height) / sheet.width;
  const unit = (fraction: number) => `${fraction * short * 100}cqw`;
  const { visible } = sheet;
  return (
    <span
      className="owner-print-date-text"
      data-testid="owner-print-date-text"
      style={{
        position: 'absolute',
        right: `calc(${(1 - (visible.left + visible.width)) * 100}% + ${unit(DATE_INSET_FRACTION)})`,
        bottom: `calc(${(1 - (visible.top + visible.height)) * 100}% + ${unit(DATE_INSET_FRACTION)})`,
        fontSize: unit(DATE_TYPE_FRACTION),
        color: '#ffffff',
        lineHeight: 1,
        textShadow: `0 0 ${unit(0.008)} rgb(0 0 0 / 55%)`,
      }}
    >
      {text}
    </span>
  );
}

type Photos =
  | { kind: 'loading' }
  | { kind: 'ready'; title: string | null; photos: StudioPhoto[] }
  | { kind: 'error' };

export function OwnerPrintPage() {
  const i18n = useI18n();
  const { t, tn, lang } = i18n;
  const perms = usePermissions();
  const [params] = useSearchParams();
  const albumId = params.get('album');
  const preselected = useMemo(
    () => (params.get('files') ?? '').split(',').map((id) => id.trim()).filter(Boolean),
    [params],
  );

  // --- the printers ---------------------------------------------------------
  const [targets, setTargets] = useState<PrintTarget[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [chosenTarget, setChosenTarget] = useState<string | null>(null);
  const loadPrinters = useCallback(async (signal?: AbortSignal) => {
    try {
      const [own, lent] = await Promise.all([listPrintStations(signal), listSharedPrinters(signal)]);
      const next = printTargets(own, lent);
      setTargets(next);
      setChosenTarget((previous) => initialTarget(next, previous) ?? next.find((x) => x.unavailable === null)?.key ?? null);
      setLoadFailed(false);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setLoadFailed(true);
      setTargets((previous) => previous ?? []);
    }
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    void loadPrinters(controller.signal);
    return () => controller.abort();
  }, [loadPrinters]);
  const target = targets?.find((candidate) => candidate.key === chosenTarget) ?? null;
  const paper = target?.paper ?? '10x15';

  // --- the photographs ----------------------------------------------------------
  const [source, setSource] = useState<Photos>({ kind: 'loading' });
  useEffect(() => {
    const controller = new AbortController();
    const photo = (id: string): StudioPhoto => ({
      id, thumbnailUrl: smallThumbnailUrl(id), previewUrl: mediumPreviewUrl(id),
    });
    if (!albumId) {
      // From the library: the photographs that were selected, and only those.
      setSource({ kind: 'ready', title: null, photos: preselected.map(photo) });
      return () => controller.abort();
    }
    Promise.all([getAlbum(albumId, controller.signal), listAlbumItems(albumId, controller.signal)])
      .then(([album, items]) => setSource({
        kind: 'ready',
        title: album.name,
        photos: items.filter((item) => item.mimeType.toLowerCase().startsWith('image/'))
          .map((item) => photo(item.fileItemId)),
      }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setSource({ kind: 'error' });
      });
    return () => controller.abort();
  }, [albumId, preselected]);
  const photos = source.kind === 'ready' ? source.photos : [];
  const photoById = useMemo(() => new Map(photos.map((p) => [p.id, p] as const)), [photos]);

  // --- the owner's choices ------------------------------------------------------
  const [style, setStyle] = useState<'fullBleed' | 'framed'>('fullBleed');
  const [caption, setCaption] = useState('');
  const [brand, setBrand] = useState(false);
  const [includeDate, setIncludeDate] = useState(false);
  const [first, setFirst] = useState<string | null>(null);
  const [date, setDate] = useState<{ id: string; value: OwnerPhotoPrintDate } | 'loading' | 'error' | null>(null);
  const onChosenChange = useCallback((chosen: string[]) => setFirst(chosen[0] ?? null), []);

  // The date is the first photograph's, read only once someone asks for it.
  useEffect(() => {
    if (!includeDate || !first) return undefined;
    if (date && typeof date === 'object' && date.id === first) return undefined;
    const controller = new AbortController();
    setDate('loading');
    getOwnerPhotoPrintDate(first, browserTimeZone(), controller.signal)
      .then((value) => setDate({ id: first, value }))
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setDate('error');
      });
    return () => controller.abort();
  // `date` is what this effect writes: reading it here must not re-run it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [includeDate, first]);
  const dateText = includeDate && date && typeof date === 'object' && date.id === first
    ? formatOwnerPrintDate(date.value.date, lang)
    : null;
  const captionInvalid = captionError(caption);

  // --- sending --------------------------------------------------------------------
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  const [resetSignal, setResetSignal] = useState(0);
  const [sent, setSent] = useState<OwnerPhotoPrintAccepted | null>(null);

  const submit = async (composition: StudioSubmission) => {
    if (!target) return;
    const framed = composition.layout !== 'photo' || style === 'framed';
    const line = caption.replace(/\s+/g, ' ').trim();
    setSubmitting(true);
    setError(null);
    setNotice(null);
    try {
      const accepted = await submitOwnerPhotoPrint({
        printStationId: target.stationId,
        printerDeviceId: target.deviceId,
        expectedPaperSize: target.paper,
        layout: composition.layout,
        style: framed ? 'framed' : 'fullBleed',
        photos: composition.slots.map((slot) => ({ fileItemId: slot.itemId, placement: slot.placement })),
        ...(composition.layout === 'photo'
          ? { orientation: composition.orientation ?? 'portrait' }
          : {}),
        ...(framed && line ? { caption: line } : {}),
        ...(framed && brand ? { brand: true } : {}),
        includeDate,
        ...(includeDate ? { dateLocale: lang, timeZone: browserTimeZone() } : {}),
      }, composition.key);
      setSent(accepted);
    } catch (err) {
      const code = refusalOf(err);
      if (code === 'paper_changed') {
        // Another paper is in: what can be made has changed, so begin again
        // from the formats it offers, saying why.
        setResetSignal((n) => n + 1);
        setNotice('ownerPrint.error.paperChanged');
      } else {
        setError((code ? ERROR_KEYS[code] : undefined) ?? 'ownerPrint.error.generic');
      }
      // Another paper, a printer gone or offline, a loan used up: read the
      // printers again. The photographs and their framing stay.
      if (code && REREAD_PRINTERS.has(code)) await loadPrinters();
    } finally {
      setSubmitting(false);
    }
  };

  // The formats this printer and its paper can make: the twin strip only from
  // a printer that cuts it.
  const formats = useMemo<StudioFormat[]>(() => {
    const offered: StudioFormat[] = [{
      layout: 'photo', count: 1, testId: 'party-print-format-photo',
      label: t('partyPrint.format.photo', { paper: PAPER_LABEL[paper] }),
      help: t('ownerPrint.format.photoHelp'), left: null, exhausted: false,
    }];
    if (layoutAllowed(paper, 'grid4')) {
      offered.push({
        layout: 'grid4', count: 4, testId: 'party-print-format-grid4',
        label: t('partyPrint.format.grid4', { paper: PAPER_LABEL[paper] }),
        help: t('partyPrint.format.grid4Help'), left: null, exhausted: false,
      });
    }
    if (layoutAllowed(paper, 'twinStrip4') && target?.cutsStrips) {
      offered.push({
        layout: 'twinStrip4', count: 4, testId: 'party-print-format-twinStrip4-4',
        label: t('partyPrint.format.twinStrip4Same'), help: t('partyPrint.format.twinStrip4SameHelp'),
        left: null, exhausted: false,
      }, {
        layout: 'twinStrip4', count: 8, testId: 'party-print-format-twinStrip4',
        label: t('partyPrint.format.twinStrip4'), help: t('partyPrint.format.twinStrip4Help'),
        left: null, exhausted: false,
      });
    }
    return offered;
  }, [paper, target, t]);

  const back = albumId
    ? <Link to={`/albums/${albumId}`}>{t('ownerPrint.backToAlbum')}</Link>
    : <Link to="/media">{t('ownerPrint.backToLibrary')}</Link>;
  const title = source.kind === 'ready' && source.title ? source.title : t('ownerPrint.title');

  if (targets === null || source.kind === 'loading') {
    return <OwnerPrintShell title={title} back={back}><p className="party-print-note">{t('ownerPrint.loading')}</p></OwnerPrintShell>;
  }
  if (source.kind === 'error') {
    return <OwnerPrintShell title={title} back={back}><p className="party-print-note">{t('ownerPrint.loadError')}</p></OwnerPrintShell>;
  }
  if (targets.length === 0) {
    return (
      <OwnerPrintShell title={title} back={back}>
        <section className="owner-print-empty" data-testid="owner-print-empty">
          <p className="party-print-note">{t(loadFailed ? 'ownerPrint.loadError' : 'ownerPrint.noPrinters')}</p>
          {!loadFailed && <p className="party-print-hint">{t('ownerPrint.noPrintersHelp')}</p>}
          {!loadFailed && perms.has(PERMISSIONS.cloudFunctionsAccess) && (
            <p className="party-print-back"><Link to="/cloud-functions?tool=print-stations">{t('ownerPrint.openPrintStations')}</Link></p>
          )}
        </section>
      </OwnerPrintShell>
    );
  }

  if (sent) {
    return (
      <OwnerPrintShell title={title} back={back}>
        <div className="party-print-sent" role="status" data-testid="owner-print-sent">
          <p className="party-print-sent-title">{t('ownerPrint.sent')}</p>
          <p className="party-print-hint">
            {sent.queueAhead === 0 ? t('ownerPrint.queueNext') : tn(sent.queueAhead, 'ownerPrint.queueAhead')}
          </p>
          <p className="party-print-hint">{t('ownerPrint.code', { code: sent.shortCode })}</p>
        </div>
        <button type="button" className="party-print-primary" onClick={() => setSent(null)}>
          {t('ownerPrint.printAnother')}
        </button>
      </OwnerPrintShell>
    );
  }

  const framedFor = (layout: PrintLayout) => layout !== 'photo' || style === 'framed';

  return (
    <OwnerPrintShell title={title} back={back}>
      <PrintStudio
        paper={paper}
        formats={formats}
        gallery={photos}
        photoById={photoById}
        preselected={preselected}
        formatHeader={(
          <fieldset className="party-print-themes owner-print-printers" data-testid="owner-print-printers">
            <legend>{t('ownerPrint.printer')}</legend>
            {targets.map((candidate) => (
              <label
                key={candidate.key}
                className="party-print-theme"
                data-testid={`owner-print-printer-${candidate.deviceId}`}
              >
                <input
                  type="radio"
                  name="owner-print-printer"
                  value={candidate.key}
                  checked={chosenTarget === candidate.key}
                  disabled={candidate.unavailable !== null}
                  onChange={() => { setChosenTarget(candidate.key); setError(null); setNotice(null); }}
                />
                <span>
                  <strong>{candidate.name}</strong>
                  {' · '}
                  {[
                    PAPER_LABEL[candidate.paper],
                    candidate.stationName,
                    ...(candidate.ownerName ? [t('ownerPrint.sharedBy', { name: candidate.ownerName })] : []),
                    ...(candidate.sheetsLeft === undefined ? []
                      : [candidate.sheetsLeft === null ? t('ownerPrint.quotaNone') : tn(candidate.sheetsLeft, 'ownerPrint.quotaLeft')]),
                    mediaRemainingLine(i18n, candidate.mediaRemaining, candidate.mediaObservedAt, candidate.offline),
                  ].join(' · ')}
                  {candidate.unavailable && <> {' — '}{t(candidate.unavailable)}</>}
                </span>
              </label>
            ))}
          </fieldset>
        )}
        formatFooter={<p className="party-print-back">{back}</p>}
        formatNotice={notice}
        photoStyle={style}
        sheetTheme={(layout) => (framedFor(layout) ? 'pure' : 'overlay')}
        bandDark={() => false}
        paperColor={(layout) => (framedFor(layout) ? FRAMED_PAPER : FULL_BLEED_PAPER)}
        band={{ title: caption.replace(/\s+/g, ' ').trim() || null, line: null, mark: dateText, brand }}
        fullBleedWords={dateText ? (sheet) => <PrintedDate text={dateText} sheet={sheet} /> : undefined}
        compositionKey={JSON.stringify([target?.key, style, caption, brand, includeDate, lang])}
        submitError={error ? t(error) : null}
        submitting={submitting}
        canSubmit={target !== null && target.unavailable === null && !captionInvalid
          && (!includeDate || dateText !== null)}
        onChosenChange={onChosenChange}
        resetSignal={resetSignal}
        onSubmit={(composition) => { void submit(composition); }}
        options={(layout) => (
          <>
            {layout === 'photo' && (
              <fieldset className="party-print-themes" data-testid="owner-print-style">
                <legend>{t('ownerPrint.style')}</legend>
                {(['fullBleed', 'framed'] as const).map((option) => (
                  <label key={option} className="party-print-theme">
                    <input
                      type="radio"
                      name="owner-print-style"
                      value={option}
                      checked={style === option}
                      onChange={() => setStyle(option)}
                    />
                    <span>{t(`ownerPrint.style.${option}`)}</span>
                  </label>
                ))}
              </fieldset>
            )}
            {framedFor(layout) && (
              <div className="owner-print-words">
                <label className="owner-print-caption">
                  <span>{t('ownerPrint.caption')}</span>
                  <input
                    type="text"
                    value={caption}
                    maxLength={CAPTION_MAX_LENGTH * 2}
                    onChange={(event) => setCaption(event.target.value)}
                    aria-invalid={captionInvalid}
                    data-testid="owner-print-caption"
                  />
                  <span className={captionInvalid ? 'party-print-error' : 'party-print-hint'}>
                    {t(captionInvalid ? 'ownerPrint.captionInvalid' : 'ownerPrint.captionHelp', { max: CAPTION_MAX_LENGTH })}
                  </span>
                </label>
                <label className="party-print-theme">
                  <input
                    type="checkbox"
                    checked={brand}
                    onChange={(event) => setBrand(event.target.checked)}
                    data-testid="owner-print-brand"
                  />
                  <span>{t('ownerPrint.brand')}</span>
                </label>
              </div>
            )}
            <label className="party-print-theme">
              <input
                type="checkbox"
                checked={includeDate}
                onChange={(event) => setIncludeDate(event.target.checked)}
                data-testid="owner-print-date"
              />
              <span>{t('ownerPrint.showDate')}</span>
            </label>
            {includeDate && (
              <p className="party-print-hint" data-testid="owner-print-date-note" role="status">
                {date === 'loading' || date === null || (typeof date === 'object' && date.id !== first)
                  ? t('ownerPrint.dateLoading')
                  : date === 'error' ? t('ownerPrint.dateError')
                    : date.value.source === 'today'
                      ? t('ownerPrint.dateToday', { date: dateText ?? '' })
                      : t('ownerPrint.datePhoto', { date: dateText ?? '' })}
              </p>
            )}
          </>
        )}
      />
    </OwnerPrintShell>
  );
}

/** The studio's own surface, as a party's guest sees it: a print is the same act. */
function OwnerPrintShell({ title, back, children }: { title: string; back: ReactNode; children: ReactNode }) {
  return (
    <main className="party-guest-hub party-print" data-testid="owner-print-page">
      <div className="party-guest-hub-topbar">
        <img
          className="party-guest-hub-logo"
          src="/brand/nubarca-wordmark-on-dark-480w.png"
          alt={PRODUCT_NAME}
          width={480}
          height={135}
        />
        <LanguageSwitcher className="language-switcher language-switcher-public" compact />
      </div>
      <header className="party-print-head">
        <p className="party-guest-hub-eyebrow">{PRODUCT_NAME}</p>
        <h1 className="party-print-title">{title}</h1>
        <p className="party-print-back">{back}</p>
      </header>
      {children}
    </main>
  );
}
