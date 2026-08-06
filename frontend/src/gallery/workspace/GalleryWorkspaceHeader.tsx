import { useState } from 'react';
import type { ImageSortDirection, ImageSortField } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../../i18n';
import { buildFilterChips, isSemanticActive, type FilterChipKind, type GalleryQuery } from '../galleryQuery';
import { AppliedFilterChips } from './AppliedFilterChips';
import type { PeopleIndex } from './usePeopleIndex';

const SORT_OPTIONS: ReadonlyArray<{ value: ImageSortField; labelKey: MessageKey }> = [
  { value: 'created', labelKey: 'common.created' },
  { value: 'name', labelKey: 'common.name' },
  { value: 'size', labelKey: 'common.size' },
  { value: 'datetaken', labelKey: 'gallery.sortDateTaken' },
];
const DIRECTION_OPTIONS: ReadonlyArray<{ value: ImageSortDirection; labelKey: MessageKey }> = [
  { value: 'desc', labelKey: 'gallery.dirDescending' },
  { value: 'asc', labelKey: 'gallery.dirAscending' },
];

// Compact sticky workspace header: NL input, Filters button (with active count),
// Sort control (hidden under an active visual query — relevance is server-ranked),
// the server-authoritative result count, and the applied-filter chips.
interface Props {
  query: GalleryQuery;
  total: number | null;
  loading: boolean;
  people: PeopleIndex;
  filtersButtonRef: React.RefObject<HTMLButtonElement | null>;
  onOpenFilters(opts?: { tab?: 'describe' | 'manual'; command?: string }): void;
  onSortChange(sort: ImageSortField, direction: ImageSortDirection): void;
  onRemoveChip(kind: FilterChipKind): void;
  onClearAll(): void;
}

export function GalleryWorkspaceHeader({
  query,
  total,
  loading,
  people,
  filtersButtonRef,
  onOpenFilters,
  onSortChange,
  onRemoveChip,
  onClearAll,
}: Props) {
  const { t, tn } = useI18n();
  const [nl, setNl] = useState('');
  const semantic = isSemanticActive(query);
  const activeCount = buildFilterChips(query).length;

  function submitNl(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const text = nl.trim();
    onOpenFilters({ tab: 'describe', command: text.length > 0 ? text : undefined });
    setNl('');
  }

  return (
    <header className="ws-header" data-testid="gallery-workspace-header">
      <div className="ws-header-row ws-header-top">
        <h2 className="ws-header-title">{t('gallery.heading')}</h2>
        <span className="ws-header-count" role="status" aria-live="polite" data-testid="ws-result-count">
          {loading && total === null
            ? t('gallery.ws.resultsLoading')
            : total !== null
              ? tn(total, 'gallery.ws.results')
              : ''}
        </span>
      </div>

      <div className="ws-header-row ws-header-controls">
        <form className="ws-nl-form" role="search" onSubmit={submitNl}>
          <label htmlFor="ws-nl-input" className="visually-hidden">{t('gallery.ws.nlAria')}</label>
          <input
            id="ws-nl-input"
            type="search"
            className="ws-nl-input"
            data-testid="ws-nl-input"
            placeholder={t('gallery.ws.nlPlaceholder')}
            value={nl}
            maxLength={512}
            onChange={(e) => setNl(e.target.value)}
          />
          <button type="submit" className="row-action-primary ws-nl-submit" data-testid="ws-nl-submit">
            {t('common.search')}
          </button>
        </form>

        <div className="ws-header-actions">
          <button
            ref={filtersButtonRef}
            type="button"
            className={`row-action ws-filters-button${activeCount > 0 ? ' is-active' : ''}`}
            data-testid="ws-open-filters"
            aria-haspopup="dialog"
            onClick={() => onOpenFilters({ tab: 'manual' })}
          >
            {activeCount > 0 ? t('gallery.ws.filtersWithCount', { count: activeCount }) : t('gallery.ws.filters')}
          </button>

          {semantic ? (
            <span className="ws-sort-relevance" data-testid="ws-sort-relevance-header">
              {t('gallery.ws.sortLabel')}: {t('gallery.ws.sortRelevance')}
            </span>
          ) : (
            <div className="ws-sort">
              <label htmlFor="ws-sort-field-h" className="visually-hidden">{t('common.sort')}</label>
              <select
                id="ws-sort-field-h"
                className="ws-select"
                data-testid="ws-sort-field-header"
                value={query.sort}
                onChange={(e) => onSortChange(e.target.value as ImageSortField, query.direction)}
              >
                {SORT_OPTIONS.map((o) => <option key={o.value} value={o.value}>{t(o.labelKey)}</option>)}
              </select>
              <label htmlFor="ws-sort-dir-h" className="visually-hidden">{t('gallery.direction')}</label>
              <select
                id="ws-sort-dir-h"
                className="ws-select"
                data-testid="ws-sort-direction-header"
                value={query.direction}
                onChange={(e) => onSortChange(query.sort, e.target.value as ImageSortDirection)}
              >
                {DIRECTION_OPTIONS.map((o) => <option key={o.value} value={o.value}>{t(o.labelKey)}</option>)}
              </select>
            </div>
          )}
        </div>
      </div>

      <AppliedFilterChips query={query} people={people} onRemove={onRemoveChip} onClearAll={onClearAll} />
    </header>
  );
}
