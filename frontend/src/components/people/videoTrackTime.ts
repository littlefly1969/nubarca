// VFACE-02: millisecond → clock-label formatting for video face evidence.
//
// Pure and locale-independent on purpose: an interval inside a video timeline is
// a duration offset, not a date, so it is always rendered as m:ss (or h:mm:ss
// once past an hour) rather than through Intl date formatting.

export function formatTrackPosition(milliseconds: number): string {
  if (!Number.isFinite(milliseconds) || milliseconds < 0) {
    return '0:00';
  }

  const totalSeconds = Math.floor(milliseconds / 1000);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const hours = Math.floor(totalSeconds / 3600);

  const paddedSeconds = String(seconds).padStart(2, '0');
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, '0')}:${paddedSeconds}`
    : `${minutes}:${paddedSeconds}`;
}

// "1:05 – 1:32". A single instant (start === end) collapses to one label so a
// one-frame track does not read as a zero-length range.
export function formatTrackInterval(startMilliseconds: number, endMilliseconds: number): string {
  const start = formatTrackPosition(startMilliseconds);
  const end = formatTrackPosition(endMilliseconds);
  return start === end ? start : `${start} – ${end}`;
}
