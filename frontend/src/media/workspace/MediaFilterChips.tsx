import type { MediaItem } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import type { PeopleIndex } from '../../gallery/workspace/usePeopleIndex';
import {
  buildFilterChips,
  type FilterChipKind,
  type MediaWorkspaceIdentity,
} from './mediaWorkspaceQuery';

// Localized, removable chips describing the APPLIED filters for the current
// kind. `buildFilterChips` already gates each chip by kind/source, so a chip can
// never describe a filter that is not actually applied to the visible results.
// Removing a chip clears only its field; "clear all" resets the active filters.
// People ids resolve to display names via the shared people index; the
// similarity chip shows the anchor image's display name when it is loaded.

interface Props {
  identity: MediaWorkspaceIdentity;
  people?: PeopleIndex;
  items?: MediaItem[];
  onRemove(kind: FilterChipKind): void;
  onClearAll(): void;
}

export function MediaFilterChips({ identity, people, items, onRemove, onClearAll }: Props) {
  const { t } = useI18n();
  const chips = buildFilterChips(identity);
  if (chips.length === 0) return null;

  const names = (ids: string[] | undefined): string =>
    (ids ?? [])
      .map((id) => people?.nameOf(id) ?? t('peopleFilter.unnamed'))
      .join(', ');

  const label = (chip: ReturnType<typeof buildFilterChips>[number]): string => {
    switch (chip.kind) {
      case 'metadata': return t('mediaChip.metadata', { value: chip.text ?? '' });
      case 'visual': return t('mediaChip.visual', { value: chip.text ?? '' });
      case 'people-include':
        return chip.peopleMode === 'any' && (chip.personIds?.length ?? 0) > 1
          ? `${t('mediaChip.peopleAny')} · ${names(chip.personIds)}`
          : t('mediaChip.personInclude', { names: names(chip.personIds) });
      case 'people-exclude': return t('mediaChip.personExclude', { names: names(chip.personIds) });
      case 'date': return t('mediaChip.date');
      case 'favorite': return t('mediaChip.favorite');
      case 'min-rating': return t('mediaChip.minRating', { value: String(chip.minRating ?? '') });
      case 'gps': return t('mediaChip.gps');
      case 'collapse': return t('mediaChip.collapse');
      // Was a placeholder returning the People label, which was invisible while
      // this filter had no UI. The command-bar toggle makes it reachable, so it
      // now says what it actually is.
      case 'album-membership':
        return identity.filters.common.albumMembership === 'unassigned'
          ? t('mediaChip.unassigned')
          : t('mediaChip.assigned');
      case 'similar': {
        const anchorId = identity.filters.photo.similarTo;
        const anchor = items?.find((it) => it.id === anchorId);
        return t('mediaChip.similarTo', { name: anchor?.displayName ?? anchorId.slice(0, 8) });
      }
      case 'duration': return t('mediaChip.duration');
      case 'min-height': return t('mediaChip.minHeight', { value: String(chip.minHeight ?? '') });
      case 'codec': return t('mediaChip.codec', { value: chip.text ?? '' });
      case 'has-audio': return t('mediaChip.hasAudio');
      default: return '';
    }
  };

  return (
    <div className="media-filter-chips" data-testid="media-filter-chips">
      {chips.map((chip) => {
        const text = label(chip);
        return (
          <span key={chip.key} className="media-filter-chip" data-testid={`media-chip-${chip.kind}`}>
            {text}
            <button
              type="button"
              className="media-filter-chip-remove"
              aria-label={t('mediaFilter.chipRemoveAria', { label: text })}
              data-testid={`media-chip-remove-${chip.kind}`}
              onClick={() => onRemove(chip.kind)}
            >
              ×
            </button>
          </span>
        );
      })}
      <button
        type="button"
        className="media-filter-clear-all"
        data-testid="media-chips-clear-all"
        onClick={onClearAll}
      >
        {t('mediaFilter.clearAll')}
      </button>
    </div>
  );
}
