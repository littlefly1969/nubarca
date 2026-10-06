import type { MediaItem, MediaSortField } from '@nubarca/api-client';
import { LOCALE, type Language } from '../../i18n';

export function mediaNavigationKey(item: MediaItem, sort: MediaSortField): string {
  if (sort === 'name') return 'n:' + (Array.from(item.displayName.toLowerCase())[0] ?? '');
  // Capture dates are wall-clock values. Use the server's calendar components,
  // not the browser zone (which could move a midnight photo to another month).
  return (sort === 'datetaken' ? item.takenAt ?? item.createdAt : item.createdAt).slice(0, 7);
}

export function navigationLabel(key: string, lang: Language, short = false): string {
  if (key.startsWith('n:')) return key.slice(2).toLocaleUpperCase(LOCALE[lang]) || '#';
  const [year, month] = key.split('-').map(Number);
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, 1);
  return new Intl.DateTimeFormat(LOCALE[lang], {
    month: short ? 'short' : 'long', year: 'numeric', timeZone: 'UTC',
  }).format(date);
}

export function navigationIndexAt(y: number, top: number, height: number, count: number): number {
  const fraction = Math.max(0, Math.min(1, (y - top) / Math.max(1, height)));
  return Math.min(count - 1, Math.floor(fraction * count));
}
