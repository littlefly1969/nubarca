import {
  useRef, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent,
} from 'react';
import { cropFor, type CropView } from '../pages/partyPrintGeometry';
import './PhotoCropFrame.css';

// A photograph in a frame, placed by hand: drag it, or move it with the arrow
// keys; the zoom is the caller's own control beside it.
//
// The SAME maths as the party print (`cropFor`), which is the point: a host
// framing a photograph on the invitation and a guest framing one for the
// printer move it the same way and get the same crop.

/** How far one arrow key moves the photograph, as a fraction of the source. */
const NUDGE = 0.02;

export function PhotoCropFrame({
  src, aspect, slotAspect, view, onChange, label, onAspect, testId = 'photo-crop',
}: {
  src: string;
  /** The photograph's own width / height. */
  aspect: number;
  /** The frame's width / height. */
  slotAspect: number;
  view: CropView;
  onChange: (view: CropView) => void;
  label: string;
  /** Learns the photograph's shape from the picture itself, once it loads. */
  onAspect?: (width: number, height: number) => void;
  testId?: string;
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
      className="photo-crop"
      style={{ aspectRatio: `${slotAspect}` }}
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
