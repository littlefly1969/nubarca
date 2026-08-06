import type { KeyboardEvent, MouseEvent as ReactMouseEvent } from 'react';
import type { SemanticBestMatch } from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// SEARCH-SEM-01: the moments inside one video that matched a semantic search.
//
// The backend already returned every one of these as `bestMatch` +
// `additionalMatches`; the grid simply used the first and threw the rest away.
// This renders them as a timeline so a video is one result with several
// reachable moments, rather than one result with one hidden timestamp.
//
// It is presentation only. It never re-ranks, never re-orders by score, and
// never invents a timestamp: the value sent to the player is always the
// backend's own `representativeMilliseconds`, even when the DOT had to be
// clamped to stay on the track.

export interface SemanticMarker {
  representativeMilliseconds: number;
  isBest: boolean;
}

// Backend order is best-first, which is the wrong order to LOOK at: a timeline
// reads left to right. Sort chronologically for display while remembering which
// one was best, and drop exact duplicate timestamps defensively so two dots can
// never sit on the same pixel with the same meaning.
export function toMarkers(
  bestMatch: SemanticBestMatch | null | undefined,
  additionalMatches: readonly SemanticBestMatch[] | null | undefined,
): SemanticMarker[] {
  const out: SemanticMarker[] = [];
  const seen = new Set<number>();

  const push = (match: SemanticBestMatch | null | undefined, isBest: boolean) => {
    const ms = match?.representativeMilliseconds;
    if (ms == null || !Number.isFinite(ms) || seen.has(ms)) {
      return;
    }
    seen.add(ms);
    out.push({ representativeMilliseconds: ms, isBest });
  };

  // Best first so it wins the de-duplication if an additional match repeats it.
  push(bestMatch, true);
  for (const m of additionalMatches ?? []) {
    push(m, false);
  }

  return out.sort((a, b) => a.representativeMilliseconds - b.representativeMilliseconds);
}

// Fraction of the way along the track, always a usable CSS percentage.
// A missing, zero, negative or non-finite duration would otherwise produce
// `NaN%` / `Infinity%`, and a timestamp past the end would place a dot outside
// the tile — so both are clamped here, and ONLY here, for display.
export function markerLeftPercent(ms: number, durationSeconds: number | null): number {
  if (durationSeconds == null || !Number.isFinite(durationSeconds) || durationSeconds <= 0) {
    return 0;
  }
  const fraction = ms / 1000 / durationSeconds;
  if (!Number.isFinite(fraction)) {
    return 0;
  }
  return Math.min(100, Math.max(0, fraction * 100));
}

interface Props {
  markers: readonly SemanticMarker[];
  durationSeconds: number | null;
  // Formats a duration OFFSET (not a date) — the grid's existing formatter.
  formatOffset(seconds: number): string;
  onSeek(milliseconds: number): void;
}

export function SemanticMarkerStrip({ markers, durationSeconds, formatOffset, onSeek }: Props) {
  const { t } = useI18n();
  if (markers.length === 0) {
    return null;
  }

  // Without a usable duration the dots would all pile up at 0%, which would be
  // a lie about where the moments are. Fall back to an evenly spaced, still
  // chronological row: positions become indicative, the timestamps stay exact.
  const positioned = durationSeconds != null
    && Number.isFinite(durationSeconds) && durationSeconds > 0;

  function activate(e: ReactMouseEvent | KeyboardEvent, ms: number) {
    // The marker sits inside the tile's own click surface. Without this the
    // tile would ALSO open (at its best match) and selection would toggle.
    e.preventDefault();
    e.stopPropagation();
    onSeek(ms);
  }

  return (
    <div
      className="media-tile__semantic-markers"
      data-testid="semantic-marker-strip"
      data-positioned={positioned ? 'duration' : 'even'}
      role="group"
      aria-label={t('mediaWs.semanticMarkersLabel')}
      // Stop pointer-down too: the grid starts range-selection on mousedown,
      // so preventing only the click would still drag-select the tile.
      onMouseDown={(e) => e.stopPropagation()}
    >
      <span className="media-tile__semantic-track" aria-hidden="true" />
      {markers.map((marker, i) => {
        const seconds = marker.representativeMilliseconds / 1000;
        const label = marker.isBest
          ? t('mediaWs.semanticBestMarkerAt', { time: formatOffset(seconds) })
          : t('mediaWs.semanticMarkerAt', { time: formatOffset(seconds) });
        const left = positioned
          ? markerLeftPercent(marker.representativeMilliseconds, durationSeconds)
          : (markers.length === 1 ? 50 : (i / (markers.length - 1)) * 100);
        return (
          <button
            key={marker.representativeMilliseconds}
            type="button"
            className={`media-tile__semantic-marker${
              marker.isBest ? ' media-tile__semantic-marker--best' : ''}`}
            style={{ left: `${left}%` }}
            data-testid={marker.isBest ? 'semantic-marker-best' : 'semantic-marker'}
            data-ms={marker.representativeMilliseconds}
            // The best match is announced as pressed AND drawn with a ring and
            // a larger dot, so it is never distinguished by colour alone.
            aria-pressed={marker.isBest}
            aria-label={label}
            title={label}
            onClick={(e) => activate(e, marker.representativeMilliseconds)}
            onKeyDown={(e) => {
              // Enter fires click natively; Space would scroll the page, so
              // both are handled explicitly for identical behaviour.
              if (e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar') {
                activate(e, marker.representativeMilliseconds);
              }
            }}
          >
            <span className="media-tile__semantic-dot" aria-hidden="true" />
          </button>
        );
      })}
    </div>
  );
}
