import type { Person, ImageSortDirection, ImageSortField } from '@nubarca/api-client';
import { useI18n, type MessageKey } from '../../i18n';
import {
  dateInputToIso,
  isSemanticActive,
  isoToDateInput,
  type GalleryQuery,
} from '../galleryQuery';
import type { PeopleMode } from '../PeopleFilterPanel';
import { AlbumMembershipFilter } from '../../media/filters/AlbumMembershipFilter';
import { PeopleCombobox } from './PeopleCombobox';

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

// Edits the DRAFT query in place through `onChange`. Physical filters +
// content fields are always visible in clearly separated sections; the sort
// section is hidden (replaced by a "Relevance" note) whenever a visual query is
// active, because the semantic path ignores sort server-side. People semantics
// (include/exclude, all/any) are unchanged — only the control shape improved.
interface Props {
  draft: GalleryQuery;
  onChange(next: GalleryQuery): void;
  people: Person[];
}

export function ManualFilterEditor({ draft, onChange, people }: Props) {
  const { t } = useI18n();
  const patch = (p: Partial<GalleryQuery>) => onChange({ ...draft, ...p });
  const semantic = isSemanticActive(draft);

  return (
    <div className="ws-manual">
      {/* Content ------------------------------------------------------------ */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('gallery.ws.sectionContent')}</legend>
        <label className="ws-field">
          <span className="ws-field-label">{t('gallery.ws.visualContent')}</span>
          <input
            type="text"
            className="ws-input"
            data-testid="ws-visual-input"
            placeholder={t('gallery.ws.visualPlaceholder')}
            value={draft.visualQuery}
            onChange={(e) => patch({ visualQuery: e.target.value })}
          />
          <span className="ws-help">{t('gallery.ws.visualHelp')}</span>
        </label>
        <label className="ws-field">
          <span className="ws-field-label">{t('gallery.ws.textMeta')}</span>
          <input
            type="text"
            className="ws-input"
            data-testid="ws-metadata-input"
            placeholder={t('gallery.ws.textMetaPlaceholder')}
            value={draft.metadataQuery}
            onChange={(e) => patch({ metadataQuery: e.target.value })}
          />
          <span className="ws-help">{t('gallery.ws.textMetaHelp')}</span>
        </label>
      </fieldset>

      {/* People ------------------------------------------------------------- */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('gallery.ws.sectionPeople')}</legend>
        <PeopleCombobox
          variant="include"
          label={t('gallery.ws.peopleInclude')}
          people={people}
          selected={draft.includePeople}
          otherGroup={draft.excludePeople}
          onAdd={(id) => patch({ includePeople: [...draft.includePeople, id] })}
          onRemove={(id) => patch({ includePeople: draft.includePeople.filter((x) => x !== id) })}
        />
        {draft.includePeople.length > 1 && (
          <div className="ws-radio-group" role="radiogroup" aria-label={t('peopleFilter.modeLabel')}>
            {(['all', 'any'] as PeopleMode[]).map((mode) => (
              <label key={mode} className="ws-radio">
                <input
                  type="radio"
                  name="ws-people-mode"
                  checked={draft.includePeopleMode === mode}
                  onChange={() => patch({ includePeopleMode: mode })}
                />
                <span>{t(mode === 'all' ? 'gallery.ws.peopleMatchAll' : 'gallery.ws.peopleMatchAny')}</span>
              </label>
            ))}
          </div>
        )}
        <PeopleCombobox
          variant="exclude"
          label={t('gallery.ws.peopleExclude')}
          people={people}
          selected={draft.excludePeople}
          otherGroup={draft.includePeople}
          onAdd={(id) => patch({ excludePeople: [...draft.excludePeople, id] })}
          onRemove={(id) => patch({ excludePeople: draft.excludePeople.filter((x) => x !== id) })}
        />
      </fieldset>

      {/* Date period -------------------------------------------------------- */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('gallery.ws.sectionDate')}</legend>
        <div className="ws-date-row">
          <label className="ws-field">
            <span className="ws-field-label">{t('gallery.ws.dateFrom')}</span>
            <input
              type="date"
              className="ws-input"
              data-testid="ws-date-from"
              value={isoToDateInput(draft.dateTakenFrom)}
              onChange={(e) => patch({ dateTakenFrom: dateInputToIso(e.target.value) })}
            />
          </label>
          <label className="ws-field">
            <span className="ws-field-label">{t('gallery.ws.dateTo')}</span>
            <input
              type="date"
              className="ws-input"
              data-testid="ws-date-to"
              value={isoToDateInput(draft.dateTakenTo)}
              onChange={(e) => patch({ dateTakenTo: dateInputToIso(e.target.value) })}
            />
          </label>
        </div>
      </fieldset>

      {/* Album membership --------------------------------------------------- */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('mediaFilters.album')}</legend>
        {/* The SAME component the video gallery uses, so the wording and the
            keyboard behaviour cannot drift between the two galleries. */}
        <AlbumMembershipFilter
          value={draft.albumMembership}
          onChange={(albumMembership) => patch({ albumMembership })}
        />
      </fieldset>

      {/* Attributes --------------------------------------------------------- */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('gallery.ws.sectionAttributes')}</legend>
        <div className="ws-field">
          <span className="ws-field-label">{t('gallery.favorite')}</span>
          <div className="ws-radio-group" role="radiogroup" aria-label={t('gallery.favorite')}>
            <TriState
              value={draft.favorite}
              onChange={(v) => patch({ favorite: v })}
              name="ws-favorite"
              trueKey="gallery.onlyFavorites"
              falseKey="gallery.notFavorites"
            />
          </div>
        </div>
        <label className="ws-field">
          <span className="ws-field-label">{t('gallery.minRating')}</span>
          <select
            className="ws-input"
            data-testid="ws-min-rating"
            aria-label={t('gallery.minRatingAria')}
            value={draft.minRating === null ? '' : String(draft.minRating)}
            onChange={(e) => patch({ minRating: e.target.value === '' ? null : Number(e.target.value) })}
          >
            <option value="">{t('gallery.any')}</option>
            <option value="1">★ 1+</option>
            <option value="2">★ 2+</option>
            <option value="3">★ 3+</option>
            <option value="4">★ 4+</option>
            <option value="5">★ 5</option>
          </select>
        </label>
        <div className="ws-field">
          <span className="ws-field-label">{t('gallery.gps')}</span>
          <div className="ws-radio-group" role="radiogroup" aria-label={t('gallery.gpsAria')}>
            <TriState
              value={draft.hasGps}
              onChange={(v) => patch({ hasGps: v })}
              name="ws-gps"
              trueKey="gallery.hasGps"
              falseKey="gallery.noGps"
            />
          </div>
        </div>
        <label className="ws-checkbox">
          <input
            type="checkbox"
            data-testid="ws-collapse-duplicates"
            checked={draft.collapseDuplicates}
            onChange={(e) => patch({ collapseDuplicates: e.target.checked })}
          />
          <span>{t('gallery.hideDuplicates')}</span>
        </label>
      </fieldset>

      {/* Sorting ------------------------------------------------------------ */}
      <fieldset className="ws-section">
        <legend className="ws-section-title">{t('gallery.ws.sectionSort')}</legend>
        {semantic ? (
          <p className="ws-help" data-testid="ws-sort-relevance">
            {t('gallery.ws.sortLabel')}: {t('gallery.ws.sortRelevance')}
          </p>
        ) : (
          <div className="ws-date-row">
            <label className="ws-field">
              <span className="ws-field-label">{t('common.sort')}</span>
              <select
                className="ws-input"
                data-testid="ws-sort-field"
                value={draft.sort}
                onChange={(e) => patch({ sort: e.target.value as ImageSortField })}
              >
                {SORT_OPTIONS.map((o) => <option key={o.value} value={o.value}>{t(o.labelKey)}</option>)}
              </select>
            </label>
            <label className="ws-field">
              <span className="ws-field-label">{t('gallery.direction')}</span>
              <select
                className="ws-input"
                data-testid="ws-sort-direction"
                value={draft.direction}
                onChange={(e) => patch({ direction: e.target.value as ImageSortDirection })}
              >
                {DIRECTION_OPTIONS.map((o) => <option key={o.value} value={o.value}>{t(o.labelKey)}</option>)}
              </select>
            </label>
          </div>
        )}
      </fieldset>
    </div>
  );
}

// A three-state (Any / yes / no) radio group used for the favorite + GPS flags.
function TriState({
  value,
  onChange,
  name,
  trueKey,
  falseKey,
}: {
  value: boolean | null;
  onChange(v: boolean | null): void;
  name: string;
  trueKey: MessageKey;
  falseKey: MessageKey;
}) {
  const { t } = useI18n();
  const options: { key: string; label: string; v: boolean | null }[] = [
    { key: 'any', label: t('gallery.any'), v: null },
    { key: 'yes', label: t(trueKey), v: true },
    { key: 'no', label: t(falseKey), v: false },
  ];
  return (
    <>
      {options.map((o) => (
        <label key={o.key} className="ws-radio">
          <input
            type="radio"
            name={name}
            checked={value === o.v}
            onChange={() => onChange(o.v)}
          />
          <span>{o.label}</span>
        </label>
      ))}
    </>
  );
}
