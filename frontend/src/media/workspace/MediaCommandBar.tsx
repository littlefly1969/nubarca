import type { RefObject } from 'react';
import type { ImageSortDirection, ImageSortField } from '@nubarca/api-client';
import { useI18n } from '../../i18n';
import { Icon } from '../../components/icons/Icon';
import { MediaLibraryScopeTabs } from './MediaLibraryScopeTabs';
import type { MediaLibraryScope } from './mediaWorkspaceQuery';

// One toolbar for every workspace command: search, filters, sort and the
// library-scope selector.
//
// It replaces a loose row of controls plus a second full-width tab row. Nothing
// about the underlying queries changes — the same handlers are invoked with the
// same values; this component only groups them into a single, predictable
// surface and surfaces HOW MANY filters are active on the trigger that opens
// them.

const SORT_FIELDS: ImageSortField[] = ['created', 'datetaken', 'name', 'size'];

interface Props {
  searchPlaceholder: string;
  searchText: string;
  onSearchText(value: string): void;
  onSubmitSearch(): void;

  // Count of APPLIED filters, from the same source as the chips below the bar,
  // so the badge can never disagree with what is actually shown.
  activeFilterCount: number;
  onOpenFilters(): void;
  filtersButtonRef?: RefObject<HTMLButtonElement | null>;

  // Sort is meaningless while a semantic search ranks by relevance.
  showSort: boolean;
  sort: ImageSortField;
  direction: ImageSortDirection;
  onChangeSort(sort: ImageSortField, direction: ImageSortDirection): void;

  scope: MediaLibraryScope;
  onChangeScope(scope: MediaLibraryScope): void;
  // Library organization: hide media already filed into an album. Only the
  // standard library passes these — album detail, shared albums and People
  // grids leave them undefined and render no control at all.
  unassignedOnly?: boolean;
  onToggleUnassignedOnly?(next: boolean): void;
}

export function MediaCommandBar({
  searchPlaceholder,
  searchText,
  onSearchText,
  onSubmitSearch,
  activeFilterCount,
  onOpenFilters,
  filtersButtonRef,
  showSort,
  sort,
  direction,
  onChangeSort,
  scope,
  onChangeScope,
  unassignedOnly,
  onToggleUnassignedOnly,
}: Props) {
  const { t } = useI18n();

  return (
    <div className="ws-toolbar" data-testid="ws-command-bar">
      <form
        className="ws-search"
        onSubmit={(e) => { e.preventDefault(); onSubmitSearch(); }}
        role="search"
      >
        <span className="ws-search__icon" aria-hidden="true"><Icon name="search" /></span>
        <input
          type="search"
          aria-label={searchPlaceholder}
          placeholder={searchPlaceholder}
          data-testid="ws-search-input"
          value={searchText}
          onChange={(e) => onSearchText(e.target.value)}
          onBlur={onSubmitSearch}
        />
      </form>

      <div className="ws-toolbar__actions">
        <button
          type="button"
          ref={filtersButtonRef}
          className={`ws-tool-button${activeFilterCount > 0 ? ' has-active' : ''}`}
          data-testid="ws-open-filters"
          // The count is in the accessible name too, not only in the badge.
          aria-label={activeFilterCount > 0
            ? t('mediaWs.filtersWithCount', { count: activeFilterCount })
            : t('mediaWs.filters')}
          onClick={onOpenFilters}
        >
          <Icon name="filter" />
          <span className="ws-tool-button__label">{t('mediaWs.filters')}</span>
          {activeFilterCount > 0 && (
            <span className="ws-filter-badge" data-testid="ws-filter-count" aria-hidden="true">
              {activeFilterCount}
            </span>
          )}
        </button>

        {showSort && (
          <label className="ws-sort" data-testid="ws-sort">
            <span className="visually-hidden">{t('mediaSort.label')}</span>
            <span className="ws-sort__icon" aria-hidden="true"><Icon name="sort" /></span>
            <select
              value={`${sort}:${direction}`}
              onChange={(e) => {
                const [s, d] = e.target.value.split(':') as [ImageSortField, ImageSortDirection];
                onChangeSort(s, d);
              }}
            >
              {SORT_FIELDS.map((f) => (
                <optgroup key={f} label={t(`mediaSort.${f}` as 'mediaSort.created')}>
                  <option value={`${f}:desc`}>
                    {t(`mediaSort.${f}` as 'mediaSort.created')} · {t('mediaSort.desc')}
                  </option>
                  <option value={`${f}:asc`}>
                    {t(`mediaSort.${f}` as 'mediaSort.created')} · {t('mediaSort.asc')}
                  </option>
                </optgroup>
              ))}
            </select>
          </label>
        )}

        {/* Library scope: compact and subordinate to the kind switcher, not a
            second competing tab row. */}
        <MediaLibraryScopeTabs value={scope} onChange={onChangeScope} />

        {/* "Solo da organizzare" — sits beside the scope tabs rather than in a
            new row, because it answers the same question ("which slice of the
            library am I looking at?"). A pressed toggle, not a checkbox: the
            state IS the control, and aria-pressed carries it to assistive tech
            without relying on the accent colour. */}
        {onToggleUnassignedOnly && (
          <button
            type="button"
            className={`ws-chip-toggle${unassignedOnly ? ' ws-chip-toggle--on' : ''}`}
            data-testid="ws-unassigned-only"
            aria-pressed={unassignedOnly ? 'true' : 'false'}
            title={t('mediaWs.unassignedOnlyHelp')}
            onClick={() => onToggleUnassignedOnly(!unassignedOnly)}
          >
            <Icon name="albums" aria-hidden="true" />
            <span className="ws-chip-toggle-label">{t('mediaWs.unassignedOnly')}</span>
          </button>
        )}
      </div>
    </div>
  );
}
