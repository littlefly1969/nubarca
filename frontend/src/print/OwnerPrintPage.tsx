import { useCallback, useEffect, useMemo, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  PERMISSIONS,
  getAlbum,
  getAlbumShare,
  getAlbumShareItems,
  getAlbumSharePrintDate,
  getAlbumSharePrinter,
  getOwnerPhotoPrintDate,
  listAlbumItems,
  listPrintStations,
  listSharedPrinters,
  submitAlbumSharePrint,
  submitOwnerPhotoPrint,
  createSubjectCutout,
  subjectPointOnPhoto,
  SubjectCutoutError,
  type SubjectPoint,
  type AlbumSharePrintRequest,
  type OwnerPhotoPrintDate,
} from '@nubarca/api-client';
import { formatOwnerPrintDate, layoutAllowed, layoutCut, type PrintLayout } from '@nubarca/contracts';
import { useI18n, type MessageKey } from '../i18n';
import { usePermissions } from '../auth/usePermissions';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PRODUCT_NAME } from '../brand/brand';
import { mediumPreviewUrl, smallThumbnailUrl } from '../components/files/types';
import { mediaRemainingLine } from '../cloud/PrinterMediaRemaining';
import { initialTarget, printTargets, type PrintTarget } from '../media/workspace/ownerPhotoPrintTargets';
import { useSubjectCutoutSupport } from '../subjectCutout/useSubjectCutoutAvailable';
import {
  PAPER_LABEL, PrintStudio,
  type FullBleedSheet, type StudioFormat, type StudioPhoto, type StudioPopOut, type StudioSubmission,
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
//
// PRINTING FROM A SHARE is this same page (SharePrintPage): a verified visitor
// of an email + second-factor link whose owner allowed printing gets the same
// studio and the same options, on the OWNER's printer. What differs is only
// where things come from — the share's photographs and the share's print
// routes — and that there is no printer to choose: the server takes the
// owner's, and the browser never names one. Whether the visitor may print at
// all is the server's answer on every request; this page only offers.

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
  no_subject: 'ownerPrint.popOut.noSubject',
  subject_unavailable: 'ownerPrint.popOut.unavailable',
  subject_busy: 'ownerPrint.popOut.busy',
  print_not_allowed: 'sharePrint.error.notAllowed',
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

type PrintStyleChoice = 'fullBleed' | 'framed' | 'popOut';

/** Where the subject of "Esce dalla foto" stands: being lifted, lifted, or why not. */
type PopOutState =
  | { kind: 'working'; photoId: string; point: SubjectPoint | null }
  | { kind: 'ready'; layer: StudioPopOut; point: SubjectPoint | null }
  | { kind: 'failed'; photoId: string; point: SubjectPoint | null; message: MessageKey };

const samePoint = (a: SubjectPoint | null, b: SubjectPoint | null) =>
  a === b || (a !== null && b !== null && a.x === b.x && a.y === b.y);

const POP_OUT_FAILURES: Record<string, MessageKey> = {
  noSubject: 'ownerPrint.popOut.noSubject',
  busy: 'ownerPrint.popOut.busy',
  unavailable: 'ownerPrint.popOut.unavailable',
  notFound: 'ownerPrint.error.photo',
};

type Photos =
  | { kind: 'loading' }
  | { kind: 'ready'; title: string | null; photos: StudioPhoto[] }
  | { kind: 'error' };

/** A print, without the printer: the page adds it when it is the owner's to choose. */
type PrintRequest = AlbumSharePrintRequest;

/** What one kind of print page reads and sends; the studio and its options are the same for all. */
interface PrintSource {
  /** Whether the person chooses the printer (the owner) or the server does (a share). */
  choosesPrinter: boolean;
  loadPrinters(signal?: AbortSignal): Promise<PrintTarget[]>;
  loadPhotos(signal: AbortSignal): Promise<{ title: string | null; photos: StudioPhoto[] }>;
  resolveDate(fileItemId: string, timeZone: string, signal: AbortSignal): Promise<OwnerPhotoPrintDate>;
  submit(target: PrintTarget, request: PrintRequest, key: string): Promise<{ shortCode: string; queueAhead: number }>;
  /** Photographs to start from, by id. */
  preselected: string[];
}

export function OwnerPrintPage() {
  const { t, tn } = useI18n();
  const perms = usePermissions();
  const [params] = useSearchParams();
  const albumId = params.get('album');
  const files = params.get('files') ?? '';
  const videos = Number(params.get('videos') ?? 0);
  const source = useMemo<PrintSource>(() => {
    const preselected = files.split(',').map((id) => id.trim()).filter(Boolean);
    const photo = (id: string): StudioPhoto => ({
      id, thumbnailUrl: smallThumbnailUrl(id), previewUrl: mediumPreviewUrl(id),
    });
    return {
      choosesPrinter: true,
      preselected,
      loadPrinters: async (signal) => {
        const [own, lent] = await Promise.all([listPrintStations(signal), listSharedPrinters(signal)]);
        return printTargets(own, lent);
      },
      loadPhotos: async (signal) => {
        // From the library: the photographs that were selected, and only those.
        if (!albumId) return { title: null, photos: preselected.map(photo) };
        const [album, items] = await Promise.all([getAlbum(albumId, signal), listAlbumItems(albumId, signal)]);
        const images = new Set(items.filter((item) => item.mimeType.toLowerCase().startsWith('image/'))
          .map((item) => item.fileItemId));
        return {
          title: album.name,
          // From an album's selection: the selected photographs still in it,
          // in the order selected. With none selected (the album's own
          // "Stampa"), every photograph of the album, as a party offers all of its own.
          photos: preselected.length > 0
            ? preselected.filter((id) => images.has(id)).map(photo)
            : items.filter((item) => images.has(item.fileItemId)).map((item) => photo(item.fileItemId)),
        };
      },
      resolveDate: (fileItemId, timeZone, signal) => getOwnerPhotoPrintDate(fileItemId, timeZone, signal),
      submit: (target, request, key) => submitOwnerPhotoPrint({
        ...request, printStationId: target.stationId, printerDeviceId: target.deviceId,
      }, key),
    };
  }, [albumId, files]);
  // "Esce dalla foto" uses the owner's own subject cutout: the owner's page only.
  const { available: popOut, pointSelection } = useSubjectCutoutSupport(true);
  return (
    <PrintPage
      source={source}
      popOut={popOut}
      popOutPick={pointSelection}
      note={Number.isInteger(videos) && videos > 0 ? (
        <p className="party-print-hint" data-testid="owner-print-videos-skipped">
          {tn(videos, 'ownerPrint.videosSkipped')}
        </p>
      ) : null}
      back={albumId
        ? <Link to={`/albums/${albumId}`}>{t('ownerPrint.backToAlbum')}</Link>
        : <Link to="/media">{t('ownerPrint.backToLibrary')}</Link>}
      noPrinters={(
        <>
          <p className="party-print-hint">{t('ownerPrint.noPrintersHelp')}</p>
          {perms.has(PERMISSIONS.cloudFunctionsAccess) && (
            <p className="party-print-back"><Link to="/cloud-functions?tool=print-stations">{t('ownerPrint.openPrintStations')}</Link></p>
          )}
        </>
      )}
    />
  );
}

/** The single target a share prints on: the owner's printer, as the server describes it, with no id of its own. */
const SHARE_TARGET = 'share';

/**
 * A verified visitor of a print-enabled share prints the album, with the
 * owner's studio, on the owner's printer. Reached from the share page's
 * "Stampa", which shows only when the server says the visitor may — and the
 * server asks again on every request.
 */
export function SharePrintPage() {
  const { t } = useI18n();
  const { token = '' } = useParams<{ token: string }>();
  const source = useMemo<PrintSource>(() => ({
    choosesPrinter: false,
    preselected: [],
    loadPrinters: async (signal) => {
      const printer = await getAlbumSharePrinter(token, signal);
      return [{
        key: SHARE_TARGET, stationId: '', deviceId: '', name: '', stationName: '', ownerName: null,
        paper: printer.paper,
        unavailable: printer.available ? null : 'ownerPrint.unavailable.offline',
        mediaRemaining: null, mediaObservedAt: null, offline: !printer.available,
        cutsStrips: printer.cutsStrips,
        cutFormats: printer.cutFormats ?? [],
        // The visitor chooses no printer: the owner's own one, as it is.
        simulated: false,
      }];
    },
    loadPhotos: async (signal) => {
      const [album, items] = await Promise.all([getAlbumShare(token, signal), getAlbumShareItems(token, signal)]);
      return {
        title: album.albumName,
        photos: items.items.filter((item) => !item.isVideo)
          .map((item) => ({ id: item.id, thumbnailUrl: item.thumbnailUrl, previewUrl: item.previewUrl })),
      };
    },
    resolveDate: (fileItemId, timeZone, signal) => getAlbumSharePrintDate(token, fileItemId, timeZone, signal),
    submit: (_target, request, key) => submitAlbumSharePrint(token, request, key),
  }), [token]);
  return <PrintPage source={source} back={<Link to=".." relative="path">{t('sharePrint.backToAlbum')}</Link>} />;
}

function PrintPage({ source: from, back, noPrinters, note, popOut = false, popOutPick = false }: {
  source: PrintSource;
  /** Whether "Esce dalla foto" is offered: the owner's own print, with subject cutout on. */
  popOut?: boolean;
  /** Whether, in "Esce dalla foto", the owner may tap the one person who breaks out. */
  popOutPick?: boolean;
  back: ReactNode;
  /** Said at the top of the format step (videos left out of the selection). */
  note?: ReactNode;
  /** Shown under "no printers" when there is none at all. */
  noPrinters?: ReactNode;
}) {
  const i18n = useI18n();
  const { t, tn, lang } = i18n;
  const preselected = from.preselected;

  // --- the printers ---------------------------------------------------------
  const [targets, setTargets] = useState<PrintTarget[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [chosenTarget, setChosenTarget] = useState<string | null>(null);
  const loadPrinters = useCallback(async (signal?: AbortSignal) => {
    try {
      const next = await from.loadPrinters(signal);
      setTargets(next);
      setChosenTarget((previous) => initialTarget(next, previous) ?? next.find((x) => x.unavailable === null)?.key ?? null);
      setLoadFailed(false);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setLoadFailed(true);
      setTargets((previous) => previous ?? []);
    }
  }, [from]);
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
    from.loadPhotos(controller.signal)
      .then(({ title, photos }) => setSource({ kind: 'ready', title, photos }))
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setSource({ kind: 'error' });
      });
    return () => controller.abort();
  }, [from]);
  const photos = source.kind === 'ready' ? source.photos : [];
  const photoById = useMemo(() => new Map(photos.map((p) => [p.id, p] as const)), [photos]);

  // --- the owner's choices ------------------------------------------------------
  const [style, setStyle] = useState<PrintStyleChoice>('fullBleed');
  const [caption, setCaption] = useState('');
  const [brand, setBrand] = useState(false);
  const [includeDate, setIncludeDate] = useState(false);
  const [first, setFirst] = useState<string | null>(null);
  const [date, setDate] = useState<{ id: string; value: OwnerPhotoPrintDate } | 'loading' | 'error' | null>(null);
  const onChosenChange = useCallback((chosen: string[]) => setFirst(chosen[0] ?? null), []);
  // Another format, or a print sent: the next sheet starts from the defaults.
  // Going back without changing the format keeps every choice.
  const resetChoices = useCallback(() => {
    setStyle('fullBleed');
    setCaption('');
    setBrand(false);
    setIncludeDate(false);
  }, []);

  // The date is the first photograph's, read only once someone asks for it.
  useEffect(() => {
    if (!includeDate || !first) return undefined;
    if (date && typeof date === 'object' && date.id === first) return undefined;
    const controller = new AbortController();
    setDate('loading');
    from.resolveDate(first, browserTimeZone(), controller.signal)
      .then((value) => setDate({ id: first, value }))
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setDate('error');
      });
    return () => controller.abort();
  // `date` is what this effect writes: reading it here must not re-run it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [includeDate, first, from]);
  // "Esce dalla foto": the first photograph's subject, lifted once it is
  // asked for, for the preview and the framing NubArca proposes — every
  // subject, or the one person the owner touched. The print itself lifts it
  // again on the server, from the same photograph and the same touch.
  const [touched, setTouched] = useState<{ photoId: string; point: SubjectPoint } | null>(null);
  const subjectPoint = popOutPick && touched?.photoId === first ? touched.point : null;
  const [lifted, setLifted] = useState<PopOutState | null>(null);
  useEffect(() => {
    if (style !== 'popOut' || !first) return undefined;
    if (lifted && lifted.kind !== 'failed' && samePoint(lifted.point, subjectPoint)
      && (lifted.kind === 'ready' ? lifted.layer.photoId : lifted.photoId) === first) {
      return undefined;
    }
    const controller = new AbortController();
    const point = subjectPoint;
    setLifted({ kind: 'working', photoId: first, point });
    createSubjectCutout(first, { point, signal: controller.signal })
      .then(({ png, region }) => {
        if (!region) {
          setLifted({ kind: 'failed', photoId: first, point, message: 'ownerPrint.popOut.unavailable' });
          return;
        }
        setLifted({ kind: 'ready', layer: { photoId: first, url: URL.createObjectURL(png), region }, point });
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        const reason = err instanceof SubjectCutoutError ? err.reason : 'failed';
        const message = point !== null && reason === 'noSubject'
          ? 'ownerPrint.popOut.noSubjectHere'
          : POP_OUT_FAILURES[reason] ?? 'ownerPrint.popOut.failed';
        setLifted({ kind: 'failed', photoId: first, point, message });
      });
    return () => controller.abort();
  // `lifted` is what this effect writes: reading it here must not re-run it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [style, first, subjectPoint]);
  const liftedUrl = lifted?.kind === 'ready' ? lifted.layer.url : null;
  useEffect(() => () => { if (liftedUrl) URL.revokeObjectURL(liftedUrl); }, [liftedUrl]);
  const popOutLayer = style === 'popOut' && lifted?.kind === 'ready' && lifted.layer.photoId === first
    && samePoint(lifted.point, subjectPoint)
    ? lifted.layer : null;
  // A touch on the cutout of every subject, mapped back onto the photograph.
  const pickSubject = (event: ReactMouseEvent<HTMLImageElement>) => {
    if (!popOutLayer || !first || subjectPoint !== null) return;
    const box = event.currentTarget.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return;
    setTouched({
      photoId: first,
      point: subjectPointOnPhoto(
        popOutLayer.region, (event.clientX - box.left) / box.width, (event.clientY - box.top) / box.height),
    });
  };

  const dateText = includeDate && date && typeof date === 'object' && date.id === first
    ? formatOwnerPrintDate(date.value.date, lang)
    : null;
  const captionInvalid = captionError(caption);

  // --- sending --------------------------------------------------------------------
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [notice, setNotice] = useState<MessageKey | null>(null);
  const [resetSignal, setResetSignal] = useState(0);
  const [sent, setSent] = useState<{ shortCode: string; queueAhead: number } | null>(null);

  const submit = async (composition: StudioSubmission) => {
    if (!target) return;
    const framed = composition.layout !== 'photo' || style !== 'fullBleed';
    const line = caption.replace(/\s+/g, ' ').trim();
    setSubmitting(true);
    setError(null);
    setNotice(null);
    try {
      const accepted = await from.submit(target, {
        expectedPaperSize: target.paper,
        layout: composition.layout,
        style: composition.layout === 'photo' ? style : 'framed',
        photos: composition.slots.map((slot) => ({ fileItemId: slot.itemId, placement: slot.placement })),
        ...(composition.layout === 'photo' && style === 'popOut' && subjectPoint ? { subject: subjectPoint } : {}),
        ...(composition.orientation ? { orientation: composition.orientation } : {}),
        ...(framed && line ? { caption: line } : {}),
        ...(framed && brand ? { brand: true } : {}),
        includeDate,
        ...(includeDate ? { dateLocale: lang, timeZone: browserTimeZone() } : {}),
      }, composition.key);
      setSent(accepted);
      resetChoices();
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

  // The formats this printer and its paper can make: the twin strip, two 10x15
  // and four strips only from a printer that makes that cut.
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
    const cuts = (layout: PrintLayout) => layoutAllowed(paper, layout)
      && target?.cutFormats.includes(layoutCut(layout)!) === true;
    if (cuts('photo2')) {
      offered.push({
        layout: 'photo2', count: 2, testId: 'party-print-format-photo2',
        label: t('ownerPrint.format.photo2'), help: t('ownerPrint.format.photo2Help'),
        left: null, exhausted: false,
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
    if (cuts('quadStrip4')) {
      offered.push({
        layout: 'quadStrip4', count: 4, testId: 'party-print-format-quadStrip4-4',
        label: t('ownerPrint.format.quadStrip4Same'), help: t('ownerPrint.format.quadStrip4SameHelp'),
        left: null, exhausted: false,
      }, {
        layout: 'quadStrip4', count: 16, testId: 'party-print-format-quadStrip4',
        label: t('ownerPrint.format.quadStrip4'), help: t('ownerPrint.format.quadStrip4Help'),
        left: null, exhausted: false,
      });
    }
    return offered;
  }, [paper, target, t]);

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
          {!loadFailed && noPrinters}
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

  const framedFor = (layout: PrintLayout) => layout !== 'photo' || style !== 'fullBleed';
  const styles: PrintStyleChoice[] = popOut ? ['fullBleed', 'framed', 'popOut'] : ['fullBleed', 'framed'];

  return (
    <OwnerPrintShell title={title} back={back}>
      <PrintStudio
        paper={paper}
        formats={formats}
        gallery={photos}
        photoById={photoById}
        preselected={preselected}
        formatHeader={<>{note}{!from.choosesPrinter ? (
          // A share prints on the owner's printer, which the server chooses:
          // nothing to pick, only whether it can print now.
          targets[0]?.unavailable ? (
            <p className="party-print-note" data-testid="share-print-unavailable">{t('sharePrint.unavailable')}</p>
          ) : null
        ) : (
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
                    ...(candidate.simulated ? [t('print.sim.badge')] : []),
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
        )}</>}
        formatFooter={<p className="party-print-back">{back}</p>}
        formatNotice={notice}
        photoStyle={style}
        sheetTheme={(layout) => (framedFor(layout) ? 'pure' : 'overlay')}
        bandDark={() => false}
        paperColor={(layout) => (framedFor(layout) ? FRAMED_PAPER : FULL_BLEED_PAPER)}
        band={{ title: caption.replace(/\s+/g, ' ').trim() || null, line: null, mark: dateText, brand }}
        fullBleedWords={dateText ? (sheet) => <PrintedDate text={dateText} sheet={sheet} /> : undefined}
        compositionKey={JSON.stringify([target?.key, style, caption, brand, includeDate, lang, subjectPoint])}
        submitError={error ? t(error) : null}
        submitting={submitting}
        canSubmit={target !== null && target.unavailable === null && !captionInvalid
          && (!includeDate || dateText !== null)
          && (style !== 'popOut' || popOutLayer !== null)}
        popOut={popOutLayer}
        onChosenChange={onChosenChange}
        onFormatChange={resetChoices}
        resetSignal={resetSignal}
        onSubmit={(composition) => { void submit(composition); }}
        options={(layout) => (
          <>
            {layout === 'photo' && (
              <fieldset className="party-print-themes" data-testid="owner-print-style">
                <legend>{t('ownerPrint.style')}</legend>
                {styles.map((option) => (
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
                {style === 'popOut' && (
                  <p className="party-print-hint" data-testid="owner-print-popout-note" role="status">
                    {lifted === null || lifted.kind === 'working'
                      ? t('ownerPrint.popOut.working')
                      : lifted.kind === 'failed' ? t(lifted.message) : t('ownerPrint.popOut.help')}
                  </p>
                )}
                {style === 'popOut' && popOutPick && (popOutLayer || subjectPoint !== null) && (
                  <div className="owner-print-subject-pick" data-testid="owner-print-subject-pick">
                    {popOutLayer && (
                      <img
                        src={popOutLayer.url}
                        alt={t('ownerPrint.popOut.pickAlt')}
                        data-testid="owner-print-subject-pick-image"
                        className={subjectPoint === null ? 'owner-print-subject-pick-tappable' : undefined}
                        onClick={subjectPoint === null ? pickSubject : undefined}
                      />
                    )}
                    <p className="party-print-hint">
                      {t(subjectPoint === null ? 'ownerPrint.popOut.pick' : 'ownerPrint.popOut.picked')}
                    </p>
                    {subjectPoint !== null && (
                      <button type="button" className="party-print-secondary" onClick={() => setTouched(null)}>
                        {t('ownerPrint.popOut.all')}
                      </button>
                    )}
                  </div>
                )}
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
