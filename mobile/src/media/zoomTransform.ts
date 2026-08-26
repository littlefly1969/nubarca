// Pure zoom/pan transform state for the image viewer.
//
// Kept free of React so the gesture math is unit-testable. Rules:
//   * scale is clamped to [1, 5];
//   * panning is only meaningful while scale > 1;
//   * releasing under ~1x snaps back to identity;
//   * double-tap toggles 1x ↔ 2x.

export interface ZoomState {
  scale: number;
  tx: number;
  ty: number;
}

export const MIN_SCALE = 1;
export const MAX_SCALE = 5;

const IDENTITY: ZoomState = { scale: 1, tx: 0, ty: 0 };

export function identity(): ZoomState {
  return { ...IDENTITY };
}

function clamp(v: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, v));
}

export function applyPinch(
  state: ZoomState,
  distanceRatio: number,
): ZoomState {
  const scale = clamp(state.scale * distanceRatio, MIN_SCALE, MAX_SCALE);
  return { ...state, scale };
}

export function applyPan(state: ZoomState, dx: number, dy: number): ZoomState {
  if (state.scale <= 1) return state; // pan only while zoomed
  return { ...state, tx: state.tx + dx, ty: state.ty + dy };
}

export function release(state: ZoomState): ZoomState {
  if (state.scale <= 1.05) return identity();
  return state;
}

export function toggleZoom(state: ZoomState): ZoomState {
  if (state.scale > 1.05) return identity();
  return { ...IDENTITY, scale: 2 };
}
