export function safeViewerIndex(index: number, length: number): number {
  if (length <= 0) return 0;
  return Math.min(Math.max(index, 0), length - 1);
}

export function shouldReanchorViewer(previousWidth: number, nextWidth: number): boolean {
  return previousWidth !== nextWidth;
}
