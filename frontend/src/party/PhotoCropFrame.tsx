import {
  useRef, type CSSProperties, type KeyboardEvent as ReactKeyboardEvent,
  type PointerEvent as ReactPointerEvent, type ReactNode,
} from 'react';
import {
  panPlacement, placePhoto, type PhotoPlacement, type PlacedPhoto,
} from '@nubarca/contracts';
import './PhotoCropFrame.css';

/**
 * Where a photograph is drawn inside its frame, as CSS — the one placement
 * shared by this editor and by anything that draws the result (a guest book
 * memory, a party print preview), so what was framed is what is shown.
 */
export function photoPlacementStyle(placed: PlacedPhoto): CSSProperties {
  return {
    width: `${placed.width * 100}%`,
    height: `${placed.height * 100}%`,
    left: `${placed.left * 100}%`,
    top: `${placed.top * 100}%`,
  };
}

/**
 * What a frame shows beside a photograph zoomed out, when whoever draws the
 * result has no paper of its own: white, never the editor's dark surround.
 */
export const DEFAULT_BAND = '#ffffff';

// A photograph in a frame, placed by hand: drag it, or move it with the arrow
// keys; the zoom is the caller's own control beside it (PhotoFramingControls).
//
// The SAME maths as the print and every renderer (`placePhoto`), which is the
// point: a host framing a photograph on the invitation, a guest framing one for
// the printer and an owner framing one for their own printer move it the same
// way and get the same result.

/** How far one arrow key moves the photograph, as a fraction of it. */
const NUDGE = 0.02;

export function PhotoCropFrame({
  src, aspect, slotAspect, view, onChange, label, onAspect, testId = 'photo-crop',
  background = DEFAULT_BAND, children,
}: {
  src: string;
  /** The photograph's own width / height. */
  aspect: number;
  /** The frame's width / height. */
  slotAspect: number;
  view: PhotoPlacement;
  onChange: (view: PhotoPlacement) => void;
  label: string;
  /** Learns the photograph's shape from the picture itself, once it loads. */
  onAspect?: (width: number, height: number) => void;
  testId?: string;
  /** What the result shows beside a photograph zoomed out: its paper, its well. */
  background?: string;
  /** Drawn over the photograph, inside the frame (a print's date). */
  children?: ReactNode;
}) {
  const frameRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ x: number; y: number } | null>(null);
  const placed = placePhoto(aspect, slotAspect, view);

  const onKeyDown = (event: ReactKeyboardEvent<HTMLDivElement>) => {
    // Precision without a pointer: the same movement a drag makes, in steps of
    // the photograph. An axis the photograph does not overflow does not move.
    const step = event.shiftKey ? NUDGE * 4 : NUDGE;
    const x = step * placed.width;
    const y = step * placed.height;
    switch (event.key) {
      case 'ArrowLeft': onChange(panPlacement(aspect, slotAspect, view, x, 0)); break;
      case 'ArrowRight': onChange(panPlacement(aspect, slotAspect, view, -x, 0)); break;
      case 'ArrowUp': onChange(panPlacement(aspect, slotAspect, view, 0, y)); break;
      case 'ArrowDown': onChange(panPlacement(aspect, slotAspect, view, 0, -y)); break;
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
    // The photograph follows the finger, however far it is zoomed in.
    onChange(panPlacement(aspect, slotAspect, view,
      (event.clientX - start.x) / box.width,
      (event.clientY - start.y) / box.height));
    dragRef.current = { x: event.clientX, y: event.clientY };
  };

  const endDrag = () => { dragRef.current = null; };

  return (
    <div
      ref={frameRef}
      className="photo-crop"
      style={{ aspectRatio: `${slotAspect}`, background }}
      tabIndex={0}
      role="group"
      aria-label={label}
      onKeyDown={onKeyDown}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={endDrag}
      onPointerCancel={endDrag}
      data-testid={testId}
    >
      <img
        className="photo-crop-img"
        src={src}
        alt=""
        draggable={false}
        onLoad={(event) => onAspect?.(
          event.currentTarget.naturalWidth, event.currentTarget.naturalHeight)}
        style={photoPlacementStyle(placed)}
      />
      {children}
    </div>
  );
}
