export function safeViewerIndex(index: number, length: number): number {
  if (length <= 0) return 0;
  return Math.min(Math.max(index, 0), length - 1);
}

export function shouldReanchorViewer(previousWidth: number, nextWidth: number): boolean {
  return previousWidth !== nextWidth;
}

export function viewerOffsetForIndex(
  index: number,
  viewportWidth: number,
  length: number,
): number {
  if (!Number.isFinite(viewportWidth) || viewportWidth <= 0) return 0;
  return safeViewerIndex(index, length) * viewportWidth;
}

export function viewerContentCanReachIndex(
  contentWidth: number,
  index: number,
  viewportWidth: number,
  length: number,
): boolean {
  if (!Number.isFinite(contentWidth) || contentWidth <= 0 || length <= 0) {
    return false;
  }
  const requiredWidth =
    viewerOffsetForIndex(index, viewportWidth, length) + viewportWidth;
  return contentWidth + 0.5 >= requiredWidth;
}

/**
 * Translate the end of a REAL user gesture into a logical slide. The width
 * captured when the drag began must still be the measured pager width when it
 * ends. A late Android completion from the pre-rotation geometry therefore has
 * no authority over the current item.
 */
export function viewerIndexFromUserScroll(
  offset: number,
  dragWidth: number | null,
  viewportWidth: number,
  length: number,
): number | null {
  if (
    dragWidth === null
    || !Number.isFinite(offset)
    || !Number.isFinite(dragWidth)
    || dragWidth <= 0
    || dragWidth !== viewportWidth
    || length <= 0
  ) {
    return null;
  }
  return safeViewerIndex(Math.round(offset / dragWidth), length);
}
