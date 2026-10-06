import {
  useCallback, useEffect, useRef, useState,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import {
  IDENTITY, NO_TAP, TAP_SLOP_PX, type TapState, type Transform,
  clampScale, isZoomed, panBy, pinchCentre, pinchDistance, pointerDown, pointerMoved,
  pointerUp, toCssTransform, toggleZoom, zoomAt,
} from './imageTransform';
import './mediaView.css';

// ONE PHOTOGRAPH, EXAMINABLE: pinch, double tap, drag and ctrl-wheel zoom, for
// every full-screen viewer — the public one a guest opens and the owner's own.
//
// The stage FILLS the viewer (it is absolutely positioned against it), so the
// picture fits a box of definite size and stays whole in portrait and in
// landscape alike — a picture sized against a box whose height is not fixed is
// sized by its width alone, and on a phone turned sideways that is taller than
// the screen. Zooming happens inside the same full-screen box, so an enlarged
// photograph uses the whole screen rather than its own fitted frame.
//
// The arithmetic is pure and tested (imageTransform). What lives here is the
// plumbing between pointer events and that state.

export interface ZoomableImageProps {
  src: string;
  alt: string;
  stageClassName?: string;
  imgClassName?: string;
  stageTestId?: string;
  imgTestId?: string;
  /** Told whenever the picture leaves or returns to fit: a shell must not swipe a zoomed photograph away. */
  onZoomChange?(zoomed: boolean): void;
  /** A tap or click that was not part of a gesture — a viewer shows or hides its controls. */
  onTap?(): void;
  onError?(): void;
}

export function ZoomableImage({
  src, alt, stageClassName, imgClassName, stageTestId, imgTestId, onZoomChange, onTap, onError,
}: ZoomableImageProps) {
  const stageRef = useRef<HTMLDivElement>(null);
  const imgRef = useRef<HTMLImageElement>(null);
  const [transform, setTransform] = useState<Transform>(IDENTITY);

  // A new picture is a new look at something: never inherit the previous
  // photograph's zoom.
  useEffect(() => { setTransform(IDENTITY); }, [src]);

  const zoomed = isZoomed(transform);
  useEffect(() => { onZoomChange?.(zoomed); }, [onZoomChange, zoomed]);

  // The box the picture FILLS at fit — its own untransformed layout size,
  // which is what the pan bounds are written against.
  const viewport = useCallback(() => ({
    width: imgRef.current?.offsetWidth ?? 0,
    height: imgRef.current?.offsetHeight ?? 0,
  }), []);

  // Where a client point sits relative to the stage's CENTRE — which is also the
  // picture's, since the stage centres it — the frame every transform is in.
  const focalOf = useCallback((clientX: number, clientY: number) => {
    const box = stageRef.current?.getBoundingClientRect();
    if (!box) return { x: 0, y: 0 };
    return { x: clientX - (box.left + box.width / 2), y: clientY - (box.top + box.height / 2) };
  }, []);

  // Active pointers, so one finger drags and two pinch. Each remembers where it
  // STARTED, which is what the tap slop measures from.
  const pointers = useRef(new Map<number, {
    x: number; y: number; originX: number; originY: number;
  }>());
  const pinchStart = useRef<{ distance: number; scale: number } | null>(null);
  const tap = useRef<TapState>(NO_TAP);

  const onPointerDown = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    pointers.current.set(e.pointerId, {
      x: e.clientX, y: e.clientY, originX: e.clientX, originY: e.clientY,
    });
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    tap.current = pointerDown(tap.current);
    if (pointers.current.size === 2) {
      const [a, b] = [...pointers.current.values()];
      pinchStart.current = { distance: pinchDistance(a, b), scale: transform.scale };
    }
  }, [transform.scale]);

  const onPointerMove = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    const previous = pointers.current.get(e.pointerId);
    if (!previous) return;
    const next = {
      x: e.clientX, y: e.clientY,
      originX: previous.originX, originY: previous.originY,
    };
    pointers.current.set(e.pointerId, next);
    // Measured from where this pointer STARTED, not from the last frame: a slow
    // drag moves a pixel at a time and would never trip a per-frame threshold.
    if (Math.hypot(next.x - next.originX, next.y - next.originY) > TAP_SLOP_PX) {
      tap.current = pointerMoved(tap.current);
    }

    if (pointers.current.size >= 2 && pinchStart.current) {
      const [a, b] = [...pointers.current.values()];
      const distance = pinchDistance(a, b);
      if (pinchStart.current.distance > 0) {
        // Everything the update needs is read NOW, while this event still owns
        // the refs. React runs the updater at its next render, which can come
        // after a finger has lifted and cleared `pinchStart`: reading the ref
        // inside the updater threw there, and the whole page went blank the
        // moment a guest let go of a photograph they had just enlarged.
        const nextScale = pinchStart.current.scale * (distance / pinchStart.current.distance);
        const centre = pinchCentre(a, b);
        const focal = focalOf(centre.x, centre.y);
        const box = viewport();
        setTransform((cur) => zoomAt(cur, nextScale, focal, box));
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

  // A cancelled pointer ends its gesture without ever being a tap.
  const onPointerCancel = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    tap.current = pointerUp(pointerMoved(tap.current), Date.now()).state;
    endPointer(e);
  }, [endPointer]);

  // Double tap / double click: zoom to where it happened, or go back to fit.
  const onDoubleActivate = useCallback((clientX: number, clientY: number) => {
    setTransform((cur) => toggleZoom(cur, focalOf(clientX, clientY), viewport()));
  }, [focalOf, viewport]);

  const onPointerUp = useCallback((e: ReactPointerEvent<HTMLDivElement>) => {
    // The machine decides, and it only says "tap" for a gesture that had ONE
    // pointer and stayed inside the slop: lifting the second finger of a pinch,
    // or letting go after a pan, is not a tap. Touch has no dblclick worth
    // relying on, so its double tap is measured here; a mouse keeps its own
    // `onDoubleClick`.
    const result = pointerUp(tap.current, Date.now());
    tap.current = result.state;
    if (result.doubleTap || result.state.lastTapAt !== 0) onTap?.();
    if (e.pointerType === 'touch' && result.doubleTap) onDoubleActivate(e.clientX, e.clientY);
    endPointer(e);
  }, [endPointer, onDoubleActivate, onTap]);

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

  return (
    <div
      className={`zoomable-stage${stageClassName ? ` ${stageClassName}` : ''}`}
      ref={stageRef}
      data-testid={stageTestId}
      data-zoomed={zoomed ? 'true' : 'false'}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
      onDoubleClick={(e) => onDoubleActivate(e.clientX, e.clientY)}
      onWheel={onWheel}
    >
      <img
        ref={imgRef}
        className={`zoomable-img${imgClassName ? ` ${imgClassName}` : ''}`}
        data-testid={imgTestId}
        src={src}
        alt={alt}
        draggable={false}
        onError={onError}
        style={{ transform: toCssTransform(transform) }}
      />
    </div>
  );
}
