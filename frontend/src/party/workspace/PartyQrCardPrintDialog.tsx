import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import {
  ApiError,
  listPartyQrCardPhotos,
  submitPartyQrCardPrint,
  type PartyQrCardPhoto,
  type PartyQrCardPrintRequest,
} from '@nubarca/api-client';
import { DEFAULT_PHOTO_PLACEMENT, effectiveZoom, type PhotoPlacement } from '@nubarca/contracts';
import { Modal } from '../../components/Overlay';
import { mediumPreviewUrl, smallThumbnailUrl } from '../../components/files/types';
import { useI18n, type MessageKey } from '../../i18n';
import {
  PORTRAIT_HEIGHT, PORTRAIT_WIDTH, QR_CARD_CODE_WIDTH_FRACTION, QR_CARD_LINE_FRACTION, QR_CARD_LINES,
  STRIP_MARGIN_FRACTION, qrCardCell, qrCardPhotoAspect, stripFooter, stripWidthFraction,
} from '../../pages/partyPrintGeometry';
import { DEFAULT_BAND, PhotoCropFrame } from '../PhotoCropFrame';
import { PhotoFramingControls } from '../PhotoFramingControls';
import { absoluteGuestUrl, useQrSvg } from './PartyShareCard';
import './PartyQrCardPrintDialog.css';

// THE PARTY'S QR, ON PAPER: the host's cards for the tables. One 10x15 is two
// cards the printer cuts apart — a photograph of the party over the code that
// opens it, the party's name and the wordmark at the foot.
//
// The preview is one card, laid out by the renderer's own geometry. Its code is
// drawn here from the address the host sees; the printed one carries the
// installation's public address, which is the server's to put there.
//
// Several sheets are several prints, each with its own key, sent one after the
// other: a retry after a failure re-sends the same keys, so the sheets already
// accepted are answered from their records and only the missing ones print.

export const MAX_QR_CARD_SHEETS = 10;

function newIdempotencyKey(): string {
  const uuid = globalThis.crypto?.randomUUID;
  if (typeof uuid === 'function') return globalThis.crypto.randomUUID();
  return `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

const ERROR_KEYS: Record<string, MessageKey> = {
  party_closed: 'party.qrCard.error.closed',
  no_printer: 'party.qrCard.error.noPrinter',
  origin_unavailable: 'party.qrCard.error.origin',
  format_unsupported: 'party.qrCard.error.format',
  printer_offline: 'ownerPrint.error.offline',
  printer_unavailable: 'ownerPrint.error.unavailable',
  printer_not_found: 'ownerPrint.error.printerGone',
  share_revoked: 'ownerPrint.error.printerGone',
  share_exhausted: 'ownerPrint.error.shareExhausted',
  not_found: 'ownerPrint.error.photo',
  not_image: 'ownerPrint.error.photo',
  invalid_source: 'ownerPrint.error.photo',
  render_failed: 'ownerPrint.error.render',
};

/** A fraction of the strip, as a percentage of the one-card preview (the strip, margins excluded). */
function inCard(rect: { x: number; y: number; width: number; height: number }) {
  const x0 = STRIP_MARGIN_FRACTION;
  const y0 = STRIP_MARGIN_FRACTION;
  const w = stripWidthFraction();
  const h = 1 - 2 * STRIP_MARGIN_FRACTION;
  return {
    left: `${((rect.x - x0) / w) * 100}%`,
    top: `${((rect.y - y0) / h) * 100}%`,
    width: `${(rect.width / w) * 100}%`,
    height: `${(rect.height / h) * 100}%`,
  };
}

export function PartyQrCardPrintDialog({
  albumId, partyName, partyUrl, coverFileItemId, onClose,
}: {
  albumId: string;
  partyName: string;
  /** The party's relative public address — the one its share card shows. */
  partyUrl: string;
  /** The party's cover, chosen first when it is among the album's photographs. */
  coverFileItemId: string | null;
  onClose: () => void;
}) {
  const { t, tn, lang } = useI18n();
  const [photos, setPhotos] = useState<PartyQrCardPhoto[] | 'error' | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [aspect, setAspect] = useState(1);
  const [view, setView] = useState<PhotoPlacement>(DEFAULT_PHOTO_PLACEMENT);
  const [sheets, setSheets] = useState(1);
  const [progress, setProgress] = useState<{ sent: number; total: number } | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [done, setDone] = useState<number | null>(null);
  // The keys a composition stands for, one per sheet: a retry keeps them.
  const pendingRef = useRef<{ fingerprint: string; keys: string[] } | null>(null);
  // The line is fitted to its cell, as the renderer fits it: smaller, never cut.
  const lineRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    // Every photograph the party shows — not a first page of the album — with
    // the party's cover, when it is one of them, first.
    listPartyQrCardPhotos(albumId, controller.signal)
      .then((listed) => {
        const cover = listed.find((photo) => photo.fileItemId === coverFileItemId);
        const ordered = cover ? [cover, ...listed.filter((photo) => photo !== cover)] : listed;
        setPhotos(ordered);
        const first = ordered[0];
        if (first) {
          setChosen(first.fileItemId);
          if (first.width && first.height) setAspect(first.width / first.height);
        }
      })
      .catch((err) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setPhotos('error');
      });
    return () => controller.abort();
  }, [albumId, coverFileItemId]);

  const cellAspect = qrCardPhotoAspect();
  const zoom = effectiveZoom(aspect, cellAspect, view.zoom);
  const locale = lang;
  const qr = useQrSvg(absoluteGuestUrl(partyUrl), 240);
  const sending = progress !== null && done === null && error === null;

  const choose = (item: PartyQrCardPhoto) => {
    setChosen(item.fileItemId);
    setAspect(item.width && item.height ? item.width / item.height : 1);
    setView(DEFAULT_PHOTO_PLACEMENT);
    setError(null);
  };

  const request = useMemo<PartyQrCardPrintRequest | null>(() => (chosen ? {
    fileItemId: chosen,
    placement: { centerX: view.centerX, centerY: view.centerY, zoom },
    locale,
  } : null), [chosen, view.centerX, view.centerY, zoom, locale]);

  async function send() {
    if (!request || sending) return;
    const fingerprint = JSON.stringify(request);
    if (pendingRef.current?.fingerprint !== fingerprint) pendingRef.current = { fingerprint, keys: [] };
    const keys = pendingRef.current.keys;
    while (keys.length < sheets) keys.push(newIdempotencyKey());
    setError(null);
    setDone(null);
    for (let index = 0; index < sheets; index += 1) {
      setProgress({ sent: index, total: sheets });
      try {
        await submitPartyQrCardPrint(albumId, request, keys[index]);
      } catch (err) {
        const code = err instanceof ApiError ? (err.body as { error?: string } | null)?.error ?? null : null;
        setProgress({ sent: index, total: sheets });
        setError((code ? ERROR_KEYS[code] : undefined) ?? 'ownerPrint.error.generic');
        return;
      }
    }
    pendingRef.current = null;
    setProgress({ sent: sheets, total: sheets });
    setDone(sheets);
  }

  const footer = done !== null ? (
    <button type="button" className="row-action-primary" onClick={onClose} data-testid="party-qr-card-done">
      {t('common.close')}
    </button>
  ) : (
    <>
      <button type="button" className="row-action" onClick={onClose} disabled={sending}>
        {t('common.cancel')}
      </button>
      <button
        type="button"
        className="row-action-primary"
        onClick={() => void send()}
        disabled={!request || sending}
        data-testid="party-qr-card-send"
      >
        {sending && progress
          ? t('party.qrCard.sending', { sent: progress.sent + 1, total: progress.total })
          : t('party.qrCard.send')}
      </button>
    </>
  );

  const photoCell = inCard(qrCardCell(0, 0));
  const codeCell = inCard(qrCardCell(0, 1));
  const foot = inCard(stripFooter(0));
  // The card's own width in pixels on paper: type sizes are fractions of the sheet's short edge.
  const cardPx = stripWidthFraction() * PORTRAIT_WIDTH;
  const lineSize = `${(QR_CARD_LINE_FRACTION * Math.min(PORTRAIT_WIDTH, PORTRAIT_HEIGHT) / cardPx) * 100}cqw`;

  useLayoutEffect(() => {
    const el = lineRef.current;
    if (!el) return undefined;
    const fit = () => {
      el.style.fontSize = lineSize;
      const room = el.clientWidth;
      const needed = el.scrollWidth;
      if (needed > room && room > 0) el.style.fontSize = `calc(${lineSize} * ${room / needed})`;
    };
    fit();
    let live = true;
    // Measured again once the web fonts arrive: they set the real width.
    void document.fonts?.ready.then(() => { if (live) fit(); });
    return () => { live = false; };
  }, [lineSize, locale, photos]);

  return (
    <Modal
      title={t('party.qrCard.title')}
      subtitle={partyName}
      onClose={onClose}
      dismissable={!sending}
      ownsKeyboard
      footer={footer}
      testId="party-qr-card"
    >
      {done !== null ? (
        <section role="status" data-testid="party-qr-card-sent">
          <h3>{tn(done, 'party.qrCard.sent')}</h3>
          <p className="muted">{tn(done * 2, 'party.qrCard.cards')}</p>
        </section>
      ) : photos === null ? (
        <p className="muted" role="status">{t('party.qrCard.photosLoading')}</p>
      ) : photos === 'error' ? (
        <p className="folder-error" role="alert">{t('party.qrCard.photosError')}</p>
      ) : photos.length === 0 ? (
        <p className="muted" data-testid="party-qr-card-no-photos">{t('party.qrCard.noPhotos')}</p>
      ) : (
        <div className="party-qr-card">
          <div className="party-qr-card-compose">
            <div className="party-qr-card-preview" data-testid="party-qr-card-preview">
              <div className="party-qr-card-cell" style={photoCell}>
                {chosen && (
                  <PhotoCropFrame
                    src={mediumPreviewUrl(chosen)}
                    aspect={aspect}
                    slotAspect={cellAspect}
                    view={view}
                    onChange={setView}
                    onAspect={(width, height) => { if (width > 0 && height > 0) setAspect(width / height); }}
                    label={t('party.qrCard.previewLabel')}
                    testId="party-qr-card-frame"
                    background={DEFAULT_BAND}
                  />
                )}
              </div>
              <div className="party-qr-card-cell party-qr-card-code" style={codeCell}>
                <span ref={lineRef} className="party-qr-card-line" data-testid="party-qr-card-line">
                  {QR_CARD_LINES[locale]}
                </span>
                {qr && (
                  <span
                    className="party-qr-card-qr"
                    role="img"
                    aria-label={t('party.share.qrAlt')}
                    style={{ width: `${QR_CARD_CODE_WIDTH_FRACTION * 100}%` }}
                    // Generated in the browser from the party's address, as the share card does.
                    dangerouslySetInnerHTML={{ __html: qr }}
                  />
                )}
              </div>
              <div className="party-qr-card-cell party-qr-card-foot" style={foot}>
                <strong>{partyName}</strong>
                <img src="/brand/nubarca-wordmark-on-light-480w.png" alt="" aria-hidden="true" />
              </div>
            </div>
            <PhotoFramingControls
              aspect={aspect}
              slotAspect={cellAspect}
              view={view}
              onChange={setView}
              testId="party-qr-card-framing"
            />
          </div>

          <div className="party-qr-card-side">
            <fieldset className="party-qr-card-photos">
              <legend>{t('party.qrCard.photo')}</legend>
              {/* The scrolling grid is its own box: a fieldset that scrolls lays its rows out unreliably. */}
              <div className="party-qr-card-photo-grid" data-testid="party-qr-card-photos">
                {photos.map((item, index) => (
                  <label key={item.fileItemId} className={chosen === item.fileItemId ? 'is-chosen' : undefined}>
                    <input
                      type="radio"
                      name="party-qr-card-photo"
                      value={item.fileItemId}
                      checked={chosen === item.fileItemId}
                      disabled={sending}
                      onChange={() => choose(item)}
                      aria-label={t('party.qrCard.photoNumber', { n: index + 1 })}
                    />
                    <img src={smallThumbnailUrl(item.fileItemId)} alt="" loading="lazy" />
                  </label>
                ))}
              </div>
            </fieldset>

            <label className="party-qr-card-sheets">
              <span>{t('party.qrCard.sheets')}</span>
              <select
                value={sheets}
                disabled={sending}
                onChange={(event) => setSheets(Number(event.target.value))}
                data-testid="party-qr-card-sheets"
              >
                {Array.from({ length: MAX_QR_CARD_SHEETS }, (_, index) => index + 1).map((count) => (
                  <option key={count} value={count}>{count}</option>
                ))}
              </select>
              <span className="muted">{tn(sheets * 2, 'party.qrCard.cards')}</span>
            </label>
            <p className="muted pw-small">{t('party.qrCard.relink')}</p>

            {error && (
              <p className="folder-error" role="alert" data-testid="party-qr-card-error">
                {progress && progress.sent > 0 && `${t('party.qrCard.partial', { sent: progress.sent, total: progress.total })} `}
                {t(error)}
              </p>
            )}
          </div>
        </div>
      )}
    </Modal>
  );
}
