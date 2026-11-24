/** Position within the available space of the photo frame, independent of its pixels.
 * (0.5,0.5) centres the block; 0/1 align it with the corresponding edge. */
export interface PhotoTextLayout {
  mode: 'below' | 'overlay' | 'bubble';
  x: number;
  y: number;
  width: number;
  height: number;
  tail: 'left' | 'right';
}
export const DEFAULT_PHOTO_TEXT: PhotoTextLayout = {
  mode: 'below', x: 0.5, y: 0.5, width: 0.65, height: 0.35, tail: 'left',
};
export function validPhotoText(value: PhotoTextLayout): boolean {
  const between = (n: number, lo: number, hi: number) => Number.isFinite(n) && n >= lo && n <= hi;
  return ['below', 'overlay', 'bubble'].includes(value.mode)
    && between(value.x, 0, 1) && between(value.y, 0, 1)
    && between(value.width, 0.2, 0.95) && between(value.height, 0.15, 0.8)
    && ['left', 'right'].includes(value.tail);
}
export function photoTextRect(value: PhotoTextLayout) {
  return { left: value.x * (1 - value.width), top: value.y * (1 - value.height),
    width: value.width, height: value.height };
}
