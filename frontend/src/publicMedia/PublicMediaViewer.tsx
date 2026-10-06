import {
  useCallback, useEffect, useRef, useState,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react';
import { useI18n } from '../i18n';
import { ZoomableImage } from '../mediaView/ZoomableImage';
import { useIdleChrome, useSwipe, useViewerKeys } from '../mediaView/viewerControls';
import { HlsVideoPlayer } from '../video/HlsVideoPlayer';
import { canShareFiles, fetchShareFile, shareFile } from './shareMedia';
import './publicMedia.css';

// THE PUBLIC VIEWER: one picture, whole, for somebody who is not the owner — a
// party guest, a visitor holding an album's link. ONE component for both, built
// from the same engine as the owner's MediaViewer (src/mediaView): the zoom, the
// controls that step aside, the keys and the swipe.
//
// What it deliberately does NOT share with the owner's viewer is authority. It
// shows no file name and no metadata, has no drawer, no Cast, no Play — it does
// not even import them. Everything it may do arrives as an address the SERVER
// built and handed over: a download where one was offered, a playback ladder
// for a video, nothing else.
//
// "Chiudi", "Scarica" and "Condividi" float over the picture and step aside
// after a moment, so the photograph has the whole screen; a tap brings them
// back.

export interface PublicMediaItem {
  id: string;
  kind: 'image' | 'video';
  /** The medium rendition of a photograph, or a video's poster. Never an original. */
  previewUrl: string;
  /** A video's adaptive ladder. Without one the poster is the honest fallback. */
  playbackUrl?: string | null;
  /** What the surface lets this visitor take; absent means nothing. */
  downloadUrl?: string | null;
}

export interface PublicMediaViewerProps {
  item: PublicMediaItem;
  /** Names the dialog for a screen reader. Already localized. */
  label: string;
  /** "Scarica", or the album's "Scarica l'originale". */
  downloadLabel?: string;
  /** A line said rather than a button that would fail — e.g. a video that can only be watched. */
  note?: string | null;
  onClose(): void;
  onPrevious?: () => void;
  onNext?: () => void;
}

type ShareState =
  | { kind: 'idle' }
  | { kind: 'preparing' }
  | { kind: 'ready'; url: string; file: File }
  | { kind: 'failed' };

export function PublicMediaViewer({
  item, label, downloadLabel, note, onClose, onPrevious, onNext,
}: PublicMediaViewerProps) {
  const { t } = useI18n();
  const rootRef = useRef<HTMLDivElement>(null);
  const [zoomed, setZoomed] = useState(false);
  const chrome = useIdleChrome(item.id);
  const swipe = useSwipe({ onPrevious, onNext, disabled: zoomed });
  useViewerKeys(rootRef, { onClose, onPrevious, onNext });

  const playable = item.kind === 'video' && !!item.playbackUrl;

  // The page behind does not scroll while this is up.
  useEffect(() => {
    const body = document.body;
    const previous = body.style.overflow;
    body.style.overflow = 'hidden';
    return () => { body.style.overflow = previous; };
  }, []);

  // `aria-modal` claims the rest of the page is inert, so Tab must not walk out
  // of the dialog into it. A key is also a hand: the controls come back.
  const onKeyDown = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    chrome.show();
    if (e.key !== 'Tab') return;
    const focusable = Array.from(
      rootRef.current?.querySelectorAll<HTMLElement>('button:not(:disabled), a[href]') ?? [],
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;
    if (e.shiftKey && (active === first || !rootRef.current?.contains(active))) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && active === last) {
      e.preventDefault();
      first.focus();
    }
  }, [chrome]);

  // ── Condividi ───────────────────────────────────────────────────────────
  const [share, setShare] = useState<ShareState>({ kind: 'idle' });
  const [shareable] = useState(canShareFiles);
  useEffect(() => { setShare({ kind: 'idle' }); }, [item.id]);
  const downloadUrl = item.downloadUrl ?? null;

  const onShare = useCallback(async () => {
    if (!downloadUrl) return;
    chrome.show();
    if (share.kind === 'ready' && share.url === downloadUrl) {
      const outcome = await shareFile(share.file);
      if (outcome !== 'blocked') setShare({ kind: 'idle' });
      return;
    }
    setShare({ kind: 'preparing' });
    let file: File;
    try {
      file = await fetchShareFile(downloadUrl, item.id);
    } catch {
      setShare({ kind: 'failed' });
      return;
    }
    const outcome = await shareFile(file);
    // Too long since the tap for this browser: the file is here now, and the
    // next tap shares it at once.
    setShare(outcome === 'blocked'
      ? { kind: 'ready', url: downloadUrl, file }
      : outcome === 'failed' ? { kind: 'failed' } : { kind: 'idle' });
  }, [chrome, downloadUrl, item.id, share]);

  return (
    <div
      className="public-viewer"
      role="dialog"
      aria-modal="true"
      aria-label={label}
      ref={rootRef}
      data-testid="public-viewer"
      data-controls={chrome.visible ? 'shown' : 'hidden'}
      onMouseMove={chrome.show}
      onKeyDown={onKeyDown}
      onFocusCapture={chrome.show}
      // A touch on a playing video belongs to its own controls; it also brings
      // these back, rather than toggling them away.
      onPointerDownCapture={playable ? chrome.show : undefined}
      onTouchStart={swipe.onTouchStart}
      onTouchEnd={swipe.onTouchEnd}
    >
      {playable ? (
        <div className="public-viewer-video">
          <HlsVideoPlayer
            key={item.id}
            fileId={item.id}
            videoUrl={item.playbackUrl!}
            posterUrl={item.previewUrl}
            className="media-viewer-media"
          />
        </div>
      ) : (
        // The medium derivative — or a video's poster where there is nothing to
        // play — whole and uncropped. Never an original.
        <ZoomableImage
          src={item.previewUrl}
          alt=""
          stageTestId="public-viewer-stage"
          onZoomChange={setZoomed}
          onTap={chrome.toggle}
        />
      )}

      <div className={`public-viewer-controls${chrome.visible ? '' : ' is-hidden'}`}>
        {(onPrevious || onNext) && (
          <>
            <button type="button" className="media-viewer-nav media-viewer-prev"
              aria-label={t('mediaViewer.previous')} disabled={!onPrevious}
              onClick={() => { onPrevious?.(); chrome.show(); }}>‹</button>
            <button type="button" className="media-viewer-nav media-viewer-next"
              aria-label={t('mediaViewer.next')} disabled={!onNext}
              onClick={() => { onNext?.(); chrome.show(); }}>›</button>
          </>
        )}
        <div className="public-viewer-bar" data-kind={playable ? 'video' : 'image'}>
          <button
            type="button"
            className="public-viewer-action"
            data-testid="public-viewer-close"
            autoFocus
            onClick={onClose}
          >
            {t('common.close')}
          </button>
          {downloadUrl && (
            <a
              className="public-viewer-action public-viewer-action--primary"
              data-testid="public-viewer-download"
              href={downloadUrl}
              download
            >
              {downloadLabel ?? t('common.download')}
            </a>
          )}
          {downloadUrl && shareable && (
            <button
              type="button"
              className="public-viewer-action"
              data-testid="public-viewer-share"
              disabled={share.kind === 'preparing'}
              onClick={() => void onShare()}
            >
              {share.kind === 'preparing'
                ? t('publicViewer.sharePreparing')
                : share.kind === 'ready' ? t('publicViewer.shareReady') : t('publicViewer.share')}
            </button>
          )}
          {share.kind === 'failed' && (
            <p className="public-viewer-note" role="status">{t('publicViewer.shareFailed')}</p>
          )}
          {note && <p className="public-viewer-note">{note}</p>}
        </div>
      </div>
    </div>
  );
}
