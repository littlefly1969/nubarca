import {
  useCallback, useEffect, useRef, useState,
  type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent,
} from 'react';
import { useI18n } from '../i18n';
import {
  IDENTITY, type Transform, clampScale, isZoomed, panBy, pinchCentre, pinchDistance,
  toCssTransform, toggleZoom, zoomAt,
} from './imageTransform';

// THE Party full-screen image viewer. One component, two callers.
//
// The gallery photo and a content poster are the same act — a guest looking at
// one picture, whole — and they were one screen's worth of identical concerns:
// a focus trap, Escape, a body scroll lock, a close button, an uncropped fit.
// Two copies of that is two places for the trap to drift out of agreement with
// the dialog role, so there is one, and what differs between the callers is
// passed in rather than forked.
//
// What is deliberately NOT shared is authority. The gallery may offer a
// download when the server sent one; a Party content poster never can, and says
// so by having no `downloadUrl` to pass. A Party reference authorizes LOOKING.

export interface PartyImageViewerProps {
  src: string;
  /** Describes the picture to a screen reader. Already localized by the caller. */
  label: string;
  /**
   * The gallery's download, when the server offered one. A content poster
   * passes nothing: a reference authorizes viewing, never bytes.
   */
  downloadUrl?: string | null;
  onClose(): void;
}

export function PartyImageViewer({ src, label, downloadUrl, onClose }: PartyImageViewerProps) {
  const { t } = useI18n();
  const rootRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const [transform, setTransform] = useState<Transform>(IDENTITY);

  // A new picture is a new look at something: never inherit the previous
  // photograph's zoom. This is also why the viewer keeps no zoom across
  // openings — there is nothing to restore that a guest asked for.
  useEffect(() => { setTransform(IDENTITY); }, [src]);

  const viewport = useCallback(() => {
    const box = stageRef.current?.getBoundingClientRect();
    return { width: box?.width ?? 0, height: box?.height ?? 0 };
  }, []);

  // Where a client point sits relative to the stage's CENTRE, which is the
  // frame every transform function is written in.
  const focalOf = useCallback((clientX: number, clientY: number) => {
    const box = stageRef.current?.getBoundingClientRect();
    if (!box) return { x: 0, y: 0 };
    return { x: clientX - (box.left + box.width / 2), y: clientY - (box.top + box.height / 2) };
  }, []);

  // Escape closes, and the page behind does not scroll while this is up.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    const body = document.body;
    const previousOverflow = body.style.overflow;
    body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      body.style.overflow = previousOverflow;
    };
  }, [onClose]);

  // `aria-modal` claims the rest of the page is inert, so Tab must not walk out
  // of the dialog into it. The surface holds at most two controls — close, and
  // a download the gallery may offer — so the trap is this rather than a reason
  // to move a full-bleed viewer onto a primitive built around a titled header.
  const trapFocus = useCallback((e: ReactKeyboardEvent<HTMLDivElement>) => {
    if (e.key !== 'Tab') return;
    const focusable = Array.from(
      rootRef.current?.querySelectorAll<HTMLElement>('button, a[href]') ?? [],
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
  }, []);

  // Active pointers, so one finger drags and two pinch. A Map rather than a
  // count because a pinch needs both positions, and because a pointer that
  // leaves without a matching up would otherwise leave the gesture stuck.
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const pinchStart = useRef<{ distance: number; scale: number } | null>(null);
  const lastTap = useRef(0);

  const onPointerDown = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    if (pointers.current.size === 2) {
      const [a, b] = [...pointers.current.values()];
      pinchStart.current = { distance: pinchDistance(a, b), scale: transform.scale };
    }
  }, [transform.scale]);

  const onPointerMove = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    const previous = pointers.current.get(e.pointerId);
    if (!previous) return;
    const next = { x: e.clientX, y: e.clientY };
    pointers.current.set(e.pointerId, next);

    if (pointers.current.size >= 2 && pinchStart.current) {
      const [a, b] = [...pointers.current.values()];
      const distance = pinchDistance(a, b);
      if (pinchStart.current.distance > 0) {
        const factor = distance / pinchStart.current.distance;
        const centre = pinchCentre(a, b);
        setTransform((cur) => zoomAt(
          cur,
          pinchStart.current!.scale * factor,
          focalOf(centre.x, centre.y),
          viewport(),
        ));
      }
      return;
    }

    // One finger pans, and only while zoomed — `panBy` is a no-op at fit, so a
    // fitted photograph cannot be dragged off its own screen.
    if (pointers.current.size === 1 && isZoomed(transform)) {
      setTransform((cur) => panBy(cur, next.x - previous.x, next.y - previous.y, viewport()));
    }
  }, [focalOf, transform, viewport]);

  const endPointer = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    pointers.current.delete(e.pointerId);
    if (pointers.current.size < 2) pinchStart.current = null;
  }, []);

  // Double tap / double click: zoom to where it happened, or go back to fit.
  const onDoubleActivate = useCallback((clientX: number, clientY: number) => {
    setTransform((cur) => toggleZoom(cur, focalOf(clientX, clientY), viewport()));
  }, [focalOf, viewport]);

  const onPointerUp = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    endPointer(e);
    // Touch has no dblclick worth relying on, so the tap cadence is measured
    // here. Mouse keeps its own `onDoubleClick`.
    if (e.pointerType === 'touch') {
      const now = Date.now();
      if (now - lastTap.current < 300) {
        onDoubleActivate(e.clientX, e.clientY);
        lastTap.current = 0;
      } else {
        lastTap.current = now;
      }
    }
  }, [endPointer, onDoubleActivate]);

  const onWheel = useCallback((e: React.WheelEvent<HTMLDivElement>) => {
    // Desktop convenience only, and deliberately not a preventDefault on the
    // page: NubArca's viewport allows the browser's own zoom and this must not
    // take it away.
    if (!e.ctrlKey && !isZoomed(transform)) return;
    setTransform((cur) => zoomAt(
      cur,
      clampScale(cur.scale * (e.deltaY < 0 ? 1.15 : 1 / 1.15)),
      focalOf(e.clientX, e.clientY),
      viewport(),
    ));
  }, [focalOf, transform, viewport]);

  const zoomed = isZoomed(transform);

  return (
    <div
      className="party-guest-hub-viewer"
      role="dialog"
      aria-modal="true"
      aria-label={label}
      ref={rootRef}
      data-testid="party-image-viewer"
      // A click on the backdrop closes — but never one that ended a drag, which
      // is what `zoomed` guards: panning a photograph must not dismiss it.
      onClick={() => { if (!zoomed) onClose(); }}
      onKeyDown={trapFocus}
    >
      <div className="party-guest-hub-viewer-inner" onClick={(e) => e.stopPropagation()}>
        <div
          className="party-guest-hub-viewer-stage"
          ref={stageRef}
          data-testid="party-viewer-stage"
          data-zoomed={zoomed ? 'true' : 'false'}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={endPointer}
          onDoubleClick={(e) => onDoubleActivate(e.clientX, e.clientY)}
          onWheel={onWheel}
        >
          {/* The medium derivative, whole and uncropped. Never an original. */}
          <img
            className="party-guest-hub-viewer-img"
            src={src}
            alt=""
            draggable={false}
            style={{ transform: toCssTransform(transform) }}
          />
        </div>
        <div className="party-guest-hub-viewer-bar">
          <button
            type="button"
            className="party-guest-hub-viewer-close"
            data-testid="party-viewer-close"
            autoFocus
            onClick={onClose}
          >
            {t('common.close')}
          </button>
          {downloadUrl && (
            <a className="party-guest-hub-viewer-download" href={downloadUrl} download>
              {t('common.download')}
            </a>
          )}
        </div>
      </div>
    </div>
  );
}
