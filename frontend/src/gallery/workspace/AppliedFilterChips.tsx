import { useI18n } from '../../i18n';
import { buildFilterChips, type FilterChipDescriptor, type FilterChipKind, type GalleryQuery } from '../galleryQuery';
import type { PeopleIndex } from './usePeopleIndex';

// Concise, localized chips describing the APPLIED query only. Removing a chip
// clears exactly one filter (via `onRemove`); a clear-all appears when any chip
// is present. Long semantic/metadata text is visually truncated but exposed in
// full to assistive tech via the accessible label + title.
interface Props {
  query: GalleryQuery;
  people: PeopleIndex;
  onRemove(kind: FilterChipKind): void;
  onClearAll(): void;
}

export function AppliedFilterChips({ query, people, onRemove, onClearAll }: Props) {
  const { t, formatDate } = useI18n();
  const chips = buildFilterChips(query);
  if (chips.length === 0) return null;

  const nameList = (ids: string[], mode: 'all' | 'any') => {
    const join = t(mode === 'any' ? 'gallery.ws.chip.orJoin' : 'gallery.ws.chip.andJoin');
    return ids.map((id) => people.nameOf(id) ?? t('peopleFilter.unnamed')).join(join);
  };

  const dateLabel = (from: string, to: string) => {
    const f = from.length > 0 ? formatDate(from, { dateStyle: 'medium' }) : '';
    const to2 = to.length > 0 ? formatDate(to, { dateStyle: 'medium' }) : '';
    if (f && to2) return `${f} – ${to2}`;
    if (f) return `${t('gallery.ws.dateFrom')} ${f}`;
    return `${t('gallery.ws.dateTo')} ${to2}`;
  };

  const labelFor = (chip: FilterChipDescriptor): string => {
    switch (chip.kind) {
      case 'metadata':
        return t('gallery.ws.chip.metadata', { text: chip.text ?? '' });
      case 'visual':
        return chip.text ?? '';
      case 'people-include':
        return nameList(chip.personIds ?? [], chip.peopleMode ?? 'all');
      case 'people-exclude':
        return t('gallery.ws.chip.exclude', { names: nameList(chip.personIds ?? [], 'all') });
      case 'date':
        return dateLabel(chip.dateFrom ?? '', chip.dateTo ?? '');
      case 'favorite':
        return t(chip.favorite ? 'gallery.ws.chip.favorite' : 'gallery.ws.chip.notFavorite');
      case 'min-rating':
        return t('gallery.ws.chip.minRating', { n: chip.minRating ?? 0 });
      case 'gps':
        return t(chip.hasGps ? 'gallery.ws.chip.gpsPresent' : 'gallery.ws.chip.gpsAbsent');
      case 'collapse':
        return t('gallery.ws.chip.collapse');
      case 'album-membership':
        return t(chip.albumMembership === 'assigned'
          ? 'mediaFilters.albumAssigned'
          : 'mediaFilters.albumUnassigned');
      case 'similar':
        return t('gallery.similarChip');
      default:
        return '';
    }
  };

  return (
    <div className="ws-chips" data-testid="ws-active-chips">
      <ul className="ws-chips-list">
        {chips.map((chip) => {
          const label = labelFor(chip);
          return (
            <li key={chip.key}>
              <span className="ws-chip" data-testid={`ws-chip-${chip.kind}`}>
                <span className="ws-chip-text" title={label}>{label}</span>
                <button
                  type="button"
                  className="ws-chip-remove"
                  aria-label={t('gallery.ws.chip.remove', { label })}
                  data-testid={`ws-chip-remove-${chip.kind}`}
                  onClick={() => onRemove(chip.kind)}
                >
                  ×
                </button>
              </span>
            </li>
          );
        })}
      </ul>
      <button
        type="button"
        className="ws-chips-clear"
        data-testid="ws-clear-all"
        aria-label={t('gallery.ws.clearAllAria')}
        onClick={onClearAll}
      >
        {t('gallery.ws.clearAll')}
      </button>
    </div>
  );
}
