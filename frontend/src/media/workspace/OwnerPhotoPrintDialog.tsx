import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  PERMISSIONS,
  getOwnerPhotoPrintDate,
  listPrintStations,
  listSharedPrinters,
  submitOwnerPhotoPrint,
  type OwnerPhotoPrintAccepted,
  type OwnerPhotoPrintDate,
  type OwnerPhotoPrintRequest,
  type ImageMediaItem,
} from '@nubarca/api-client';
import {
  DEFAULT_PHOTO_PLACEMENT, effectiveZoom, formatOwnerPrintDate, placePhoto, visiblePhotoRect,
  type PhotoPlacement,
} from '@nubarca/contracts';
import { usePermissions } from '../../auth/usePermissions';
import { Modal } from '../../components/Overlay';
import { mediumPreviewUrl } from '../../components/files/types';
import { useI18n, type MessageKey } from '../../i18n';
import { PrinterMediaRemaining, mediaRemainingLine } from '../../cloud/PrinterMediaRemaining';
import { sheet } from '../../pages/partyPrintGeometry';
import { DEFAULT_BAND, PhotoCropFrame } from '../../party/PhotoCropFrame';
import { PhotoFramingControls } from '../../party/PhotoFramingControls';
import { initialTarget, printTargets, type PrintTarget } from './ownerPhotoPrintTargets';
import './OwnerPhotoPrintDialog.css';

// ONE OF YOUR OWN PHOTOGRAPHS, ON PAPER: the direct print from the library or
// an own album. Not a party: no budget, no footer, no brand — the photograph,
// placed as the owner places it on the loaded paper, and a date only if asked.
//
// The preview is the print: the sheet's proportions, the shared placement,
// white beside a photograph zoomed out, and the date in the very characters
// the server will print (one format table, never Intl), on the photograph's
// own bottom-right corner — not on a band.
//
// A key makes the print one sheet however often "Invia" is pressed: the same
// composition retried keeps its key; any change to it is a new print, and a
// new key. The original never comes to the browser — the server renders from
// its own copy.

/** The renderer's date: type a fraction of the short edge, inset from the photograph's corner. */
const DATE_TYPE_FRACTION = 0.03;
const DATE_INSET_FRACTION = 0.035;

type Orientation = 'portrait' | 'landscape';

function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return globalThis.crypto.randomUUID();
  return `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
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
  render_failed: 'ownerPrint.error.render',
};

/** Refusals after which what the printer list says is no longer true. */
const REREAD_PRINTERS = new Set([
  'paper_changed', 'printer_offline', 'printer_unavailable', 'printer_not_found', 'share_revoked',
  'share_exhausted', 'format_unsupported',
]);

export function OwnerPhotoPrintDialog({ item, onClose }: {
  item: ImageMediaItem;
  onClose: () => void;
}) {
  const i18n = useI18n();
  const { t, tn, lang } = i18n;
  const perms = usePermissions();

  const [targets, setTargets] = useState<PrintTarget[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [chosen, setChosen] = useState<string | null>(null);
  // The photograph's shape: the listing's until the preview (auto-oriented,
  // as the print is) says otherwise.
  const [aspect, setAspect] = useState(() =>
    item.width && item.height ? item.width / item.height : 1);
  const [orientation, setOrientation] = useState<Orientation>(aspect <= 1 ? 'portrait' : 'landscape');
  const orientationTouched = useRef(false);
  const [view, setView] = useState<PhotoPlacement>(DEFAULT_PHOTO_PLACEMENT);
  const [includeDate, setIncludeDate] = useState(false);
  const [date, setDate] = useState<OwnerPhotoPrintDate | 'loading' | 'error' | null>(null);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<MessageKey | null>(null);
  const [sent, setSent] = useState<OwnerPhotoPrintAccepted | null>(null);
  // The composition a key stands for: a retry of the same one keeps it.
  const pendingRef = useRef<{ key: string; fingerprint: string } | null>(null);

  const loadPrinters = useCallback(async (signal?: AbortSignal) => {
    try {
      const [own, lent] = await Promise.all([listPrintStations(signal), listSharedPrinters(signal)]);
      const next = printTargets(own, lent);
      setTargets(next);
      setChosen((previous) => initialTarget(next, previous));
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

  // The date is read only once someone asks for it.
  useEffect(() => {
    if (!includeDate || date !== null) return;
    const controller = new AbortController();
    setDate('loading');
    getOwnerPhotoPrintDate(item.id, browserTimeZone(), controller.signal)
      .then(setDate)
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setDate('error');
      });
    return () => controller.abort();
  }, [includeDate, date, item.id]);

  const target = targets?.find((candidate) => candidate.key === chosen) ?? null;
  const portrait = orientation === 'portrait';
  const [sheetW, sheetH] = sheet(target?.paper ?? '10x15', portrait);
  const sheetAspect = sheetW / sheetH;
  const zoom = effectiveZoom(aspect, sheetAspect, view.zoom);
  const dateText = includeDate && date && typeof date === 'object'
    ? formatOwnerPrintDate(date.date, lang)
    : null;

  const learnAspect = (width: number, height: number) => {
    if (width <= 0 || height <= 0) return;
    const real = width / height;
    setAspect(real);
    // The sheet follows the photograph until the owner turns it.
    if (!orientationTouched.current) setOrientation(real <= 1 ? 'portrait' : 'landscape');
  };

  const request = useMemo<OwnerPhotoPrintRequest | null>(() => (target ? {
    fileItemId: item.id,
    printStationId: target.stationId,
    printerDeviceId: target.deviceId,
    expectedPaperSize: target.paper,
    orientation,
    placement: { centerX: view.centerX, centerY: view.centerY, zoom },
    includeDate,
    ...(includeDate ? { dateLocale: lang, timeZone: browserTimeZone() } : {}),
  } : null), [target, item.id, orientation, view.centerX, view.centerY, zoom, includeDate, lang]);

  const canSend = request !== null && target?.unavailable === null && !sending
    && (!includeDate || (date !== null && typeof date === 'object'));

  async function send() {
    if (!request || !canSend) return;
    const fingerprint = JSON.stringify(request);
    if (pendingRef.current?.fingerprint !== fingerprint) {
      pendingRef.current = { key: newIdempotencyKey(), fingerprint };
    }
    setSending(true);
    setError(null);
    try {
      const accepted = await submitOwnerPhotoPrint(request, pendingRef.current.key);
      pendingRef.current = null;
      setSent(accepted);
    } catch (err) {
      const code = refusalOf(err);
      // A refusal is an answer about THIS key: the next attempt is a new print.
      if (code) pendingRef.current = null;
      setError((code ? ERROR_KEYS[code] : undefined) ?? 'ownerPrint.error.generic');
      // Another paper, a printer gone or offline, a loan used up: read the
      // printers again. The photograph, the framing and the date stay.
      if (code && REREAD_PRINTERS.has(code)) await loadPrinters();
    } finally {
      setSending(false);
    }
  }

  const footer = sent ? (
    <button type="button" className="row-action-primary" onClick={onClose} data-testid="owner-print-done">
      {t('common.close')}
    </button>
  ) : (
    <>
      <button type="button" className="row-action" onClick={onClose} disabled={sending}>
        {t('common.cancel')}
      </button>
      {targets !== null && targets.length > 0 && (
        <button
          type="button"
          className="row-action-primary"
          onClick={() => void send()}
          disabled={!canSend}
          data-testid="owner-print-send"
        >
          {t(sending ? 'ownerPrint.sending' : 'ownerPrint.send')}
        </button>
      )}
    </>
  );

  return (
    <Modal
      title={t('ownerPrint.title')}
      subtitle={item.displayName}
      onClose={onClose}
      dismissable={!sending}
      ownsKeyboard
      layer="workspace"
      footer={footer}
      testId="owner-print"
    >
      {sent ? (
        <section className="owner-print-sent" role="status" data-testid="owner-print-sent">
          <h3>{t('ownerPrint.sent')}</h3>
          <p>{sent.queueAhead === 0 ? t('ownerPrint.queueNext') : tn(sent.queueAhead, 'ownerPrint.queueAhead')}</p>
          <p className="muted">{t('ownerPrint.code', { code: sent.shortCode })}</p>
        </section>
      ) : targets === null ? (
        <p className="muted" role="status">{t('ownerPrint.loading')}</p>
      ) : targets.length === 0 ? (
        <section className="owner-print-empty" data-testid="owner-print-empty">
          <h3>{t(loadFailed ? 'ownerPrint.loadError' : 'ownerPrint.noPrinters')}</h3>
          {!loadFailed && <p className="muted">{t('ownerPrint.noPrintersHelp')}</p>}
          {!loadFailed && perms.has(PERMISSIONS.cloudFunctionsAccess) && (
            <Link to="/cloud-functions?tool=print-stations" onClick={onClose}>
              {t('ownerPrint.openPrintStations')}
            </Link>
          )}
        </section>
      ) : (
        <div className="owner-print">
          <fieldset className="owner-print-printers">
            <legend>{t('ownerPrint.printer')}</legend>
            {targets.map((candidate) => (
              <label
                key={candidate.key}
                className={`owner-print-printer${candidate.unavailable ? ' is-unavailable' : ''}`}
                data-testid={`owner-print-printer-${candidate.deviceId}`}
              >
                <input
                  type="radio"
                  name="owner-print-printer"
                  value={candidate.key}
                  checked={chosen === candidate.key}
                  disabled={candidate.unavailable !== null || sending}
                  onChange={() => { setChosen(candidate.key); setError(null); }}
                />
                <span className="owner-print-printer-text">
                  <strong>{candidate.name}</strong>
                  <span className="muted">
                    {[
                      candidate.stationName,
                      ...(candidate.ownerName ? [t('ownerPrint.sharedBy', { name: candidate.ownerName })] : []),
                      mediaRemainingLine(i18n, candidate.mediaRemaining, candidate.mediaObservedAt, candidate.offline),
                    ].join(' · ')}
                  </span>
                  {candidate.unavailable && <span className="owner-print-why">{t(candidate.unavailable)}</span>}
                </span>
              </label>
            ))}
          </fieldset>

          {target && (
            <dl className="owner-print-facts" data-testid="owner-print-facts">
              <div>
                <dt>{t('ownerPrint.paper')}</dt>
                <dd data-testid="owner-print-paper">{target.paper.replace('x', '×')}</dd>
              </div>
              <PrinterMediaRemaining
                remaining={target.mediaRemaining}
                observedAt={target.mediaObservedAt}
                offline={target.offline}
                testId="owner-print-media"
              />
              {target.sheetsLeft !== undefined && (
                // The loan's ceiling: what the owner of the printer allows,
                // never what is physically in it.
                <div>
                  <dt>{t('ownerPrint.quota')}</dt>
                  <dd data-testid="owner-print-quota">
                    {target.sheetsLeft === null
                      ? t('ownerPrint.quotaNone')
                      : tn(target.sheetsLeft, 'ownerPrint.quotaLeft')}
                  </dd>
                </div>
              )}
            </dl>
          )}

          <fieldset className="owner-print-orientation">
            <legend>{t('ownerPrint.orientation')}</legend>
            {(['portrait', 'landscape'] as const).map((option) => (
              <label key={option}>
                <input
                  type="radio"
                  name="owner-print-orientation"
                  value={option}
                  checked={orientation === option}
                  disabled={sending}
                  onChange={() => { orientationTouched.current = true; setOrientation(option); }}
                  data-testid={`owner-print-orientation-${option}`}
                />
                <span>{t(option === 'portrait' ? 'ownerPrint.portrait' : 'ownerPrint.landscape')}</span>
              </label>
            ))}
          </fieldset>

          <div
            className="owner-print-preview"
            data-testid="owner-print-preview"
            data-orientation={orientation}
          >
            <PhotoCropFrame
              src={mediumPreviewUrl(item.id)}
              aspect={aspect}
              slotAspect={sheetAspect}
              view={view}
              onChange={setView}
              onAspect={learnAspect}
              label={t('ownerPrint.previewLabel')}
              testId="owner-print-frame"
              background={DEFAULT_BAND}
            >
              {dateText && (
                <PrintedDate
                  text={dateText}
                  sheetAspect={sheetAspect}
                  visible={visiblePhotoRect(placePhoto(aspect, sheetAspect, view))}
                />
              )}
            </PhotoCropFrame>
          </div>
          <PhotoFramingControls
            aspect={aspect}
            slotAspect={sheetAspect}
            view={view}
            onChange={setView}
            testId="owner-print-framing"
          />

          <label className="owner-print-date">
            <input
              type="checkbox"
              checked={includeDate}
              disabled={sending}
              onChange={(event) => setIncludeDate(event.target.checked)}
              data-testid="owner-print-date"
            />
            <span>{t('ownerPrint.showDate')}</span>
          </label>
          {includeDate && (
            <p className="muted" data-testid="owner-print-date-note" role="status">
              {date === 'loading' || date === null ? t('ownerPrint.dateLoading')
                : date === 'error' ? t('ownerPrint.dateError')
                  : date.source === 'today'
                    ? t('ownerPrint.dateToday', { date: dateText ?? '' })
                    : t('ownerPrint.datePhoto', { date: dateText ?? '' })}
            </p>
          )}

          {error && <p className="folder-error" role="alert" data-testid="owner-print-error">{t(error)}</p>}
        </div>
      )}
    </Modal>
  );
}

/**
 * The date as the renderer draws it: white type with a soft dark halo, right-
 * and bottom-aligned at an inset from the VISIBLE photograph's corner. Sizes
 * are the renderer's fractions of the sheet's short edge, in container units.
 */
function PrintedDate({ text, sheetAspect, visible }: {
  text: string;
  sheetAspect: number;
  visible: { left: number; top: number; width: number; height: number };
}) {
  // The short edge as a fraction of the sheet's width.
  const short = Math.min(1, 1 / sheetAspect);
  const unit = (fraction: number) => `${fraction * short * 100}cqw`;
  return (
    <span
      className="owner-print-date-text"
      data-testid="owner-print-date-text"
      style={{
        right: `calc(${(1 - (visible.left + visible.width)) * 100}% + ${unit(DATE_INSET_FRACTION)})`,
        bottom: `calc(${(1 - (visible.top + visible.height)) * 100}% + ${unit(DATE_INSET_FRACTION)})`,
        fontSize: unit(DATE_TYPE_FRACTION),
        textShadow: `0 0 ${unit(0.008)} rgb(0 0 0 / 55%)`,
      }}
    >
      {text}
    </span>
  );
}
