import { useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from '../gallery/PanelShell';
import { TvKeyboardPanel } from '../gallery/TvKeyboardPanel';
import { FilterRow } from './FilterRow';
import { LibraryPeoplePanel } from './LibraryPeoplePanel';
import { LibraryPeriodPanel } from './LibraryPeriodPanel';
import { useI18n } from '../../i18n';
import {
  activeFilterCount,
  clearActiveFilters,
  cloneMediaFilters,
  DURATION_PRESET_MINUTES,
  isoToDateInput,
  MIN_HEIGHT_PRESETS,
  minutesToSeconds,
  secondsToMinutes,
  type MediaSortDirection,
  type MediaSortField,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
} from '../../personal/mediaWorkspaceQuery';
import {
  resolveTvFilterFocus,
  tvFilterRows,
  type TvFilterFocusKey,
  type TvFilterId,
} from '../../personal/tvFilterCatalog';

// TV filter panel for the unified library.
//
// The SEMANTICS are the web Media Workspace's, exactly — same groups, same
// defaults, same tri-state meanings, same canonical units. The LAYOUT is not:
// this is a 10-foot remote UI, so the desktop sheet's dense multi-column form
// is one vertical list of large rows, one setting focused at a time, nothing to
// hunt for and nothing that needs a pointer.
//
// The rows are no longer written out by hand. They come from
// `tvFilterCatalog.tvFilterRows`, which decides applicability from the active
// tab and source, and every row it can produce carries an EDITOR. That is the
// structural fix: the panel used to be the list of filters, so when the people
// row was written as a read-only summary — it could clear a selection but never
// make one — nothing could notice the television had a filter it could not
// operate. A row with no way to edit it is now unrepresentable, and a new
// domain filter field does not compile until it is given a home here.
//
// Which rows exist follows the tab, and that is the product rule rather than a
// cosmetic one: a control that cannot be shown also cannot be sent (see
// mediaWorkspaceQuery.queryToWire), so the panel and the wire agree by
// construction and are checked against each other in the tests.
//
// Nothing takes effect until Apply. The panel edits a DRAFT, sub-editors edit
// the same draft, BACK out of a sub-editor keeps the edit and returns to the
// exact row that opened it, and BACK out of the panel discards the draft
// without issuing a query. Reset clears the current tab's filters and stays
// open — the reset is applied like anything else.

interface Props {
  applied: MediaWorkspaceIdentity;
  resultCount: number;
  onApply: (filters: MediaWorkspaceFilters, sort: MediaSortField, direction: MediaSortDirection) => void;
  onCancel: () => void;
  // 401/403 from the person picker bubble up to the screen's pairing/lock handling.
  onAuthError: (err: unknown) => boolean;
}

type SubEditor =
  | { kind: 'none' }
  | { kind: 'text'; target: 'metadataQuery' | 'codec' }
  | { kind: 'period' }
  | { kind: 'people' };

const SORTS: readonly MediaSortField[] = ['created', 'datetaken', 'name', 'size'];
const RATINGS: readonly (number | null)[] = [null, 1, 2, 3, 4, 5];
const TRISTATE: readonly (boolean | null)[] = [null, true, false];
const MEMBERSHIPS = ['any', 'assigned', 'unassigned'] as const;

function cycle<T>(values: readonly T[], current: T): T {
  const at = values.indexOf(current);
  return values[(at + 1) % values.length];
}

interface RowView {
  label: string;
  value: string;
  onSelect: () => void;
}

export function LibraryFilterPanel({ applied, resultCount, onApply, onCancel, onAuthError }: Props) {
  const { t } = useI18n();
  const [filters, setFilters] = useState<MediaWorkspaceFilters>(
    () => cloneMediaFilters(applied.filters),
  );
  const [sort, setSort] = useState<MediaSortField>(applied.sort);
  const [direction, setDirection] = useState<MediaSortDirection>(applied.direction);
  const [editor, setEditor] = useState<SubEditor>({ kind: 'none' });

  // Where the remote is, as a ref: moving between rows must not re-render a
  // panel whose rows all recompute their labels. It is read at render time,
  // which is what makes a return from a sub-editor land on the opener row —
  // the sub-editor unmounts the list, so the request is honoured on remount.
  const focusRef = useRef<TvFilterFocusKey | null>(null);

  const patchCommon = (patch: Partial<MediaWorkspaceFilters['common']>) =>
    setFilters((f) => ({ ...f, common: { ...f.common, ...patch } }));
  const patchPhoto = (patch: Partial<MediaWorkspaceFilters['photo']>) =>
    setFilters((f) => ({ ...f, photo: { ...f.photo, ...patch } }));
  const patchVideo = (patch: Partial<MediaWorkspaceFilters['video']>) =>
    setFilters((f) => ({ ...f, video: { ...f.video, ...patch } }));

  const open = (next: SubEditor, opener: TvFilterId) => {
    focusRef.current = opener;
    setEditor(next);
  };

  const anyLabel = t('filters.any');
  const none = t('filters.none');
  const triLabel = (value: boolean | null, yes: string, no: string) =>
    value === null ? anyLabel : value ? yes : no;

  // ------------------------------------------------------------ sub-editors

  if (editor.kind === 'text') {
    const isCodec = editor.target === 'codec';
    return (
      <TvKeyboardPanel
        title={isCodec ? t('filters.codec') : t('filters.search')}
        mode="text"
        initialValue={isCodec ? filters.video.codec : filters.common.metadataQuery}
        onCancel={() => setEditor({ kind: 'none' })}
        onSubmit={(value) => {
          if (isCodec) patchVideo({ codec: value.trim() });
          else patchCommon({ metadataQuery: value.trim() });
          setEditor({ kind: 'none' });
        }}
      />
    );
  }

  if (editor.kind === 'period') {
    return (
      <LibraryPeriodPanel
        from={filters.common.dateTakenFrom}
        to={filters.common.dateTakenTo}
        onChange={(from, to) => patchCommon({ dateTakenFrom: from, dateTakenTo: to })}
        onClose={() => setEditor({ kind: 'none' })}
      />
    );
  }

  if (editor.kind === 'people') {
    return (
      <LibraryPeoplePanel
        include={filters.photo.includePeople}
        exclude={filters.photo.excludePeople}
        mode={filters.photo.includePeopleMode}
        onChange={(include, exclude, mode) => patchPhoto({
          includePeople: include,
          excludePeople: exclude,
          includePeopleMode: mode,
        })}
        onClose={() => setEditor({ kind: 'none' })}
        onAuthError={onAuthError}
      />
    );
  }

  // ------------------------------------------------------------------ rows

  const draftIdentity: MediaWorkspaceIdentity = { ...applied, filters, sort, direction };
  const rows = tvFilterRows(applied, filters);
  // Deterministic and total: the same row when it is still there, else the
  // nearest one, else the primary action. Never the bare container.
  const focusKey = resolveTvFilterFocus(focusRef.current, rows);
  focusRef.current = focusKey;

  const peopleValue = (): string => {
    const { includePeople, excludePeople, includePeopleMode } = filters.photo;
    if (includePeople.length === 0 && excludePeople.length === 0) return anyLabel;
    const parts: string[] = [];
    if (includePeople.length > 0) {
      parts.push(t('filters.peopleIncluded', { count: String(includePeople.length) }));
    }
    if (excludePeople.length > 0) {
      parts.push(t('filters.peopleExcluded', { count: String(excludePeople.length) }));
    }
    if (includePeople.length >= 2) {
      // The compact form: the row has one line, and the full sentence
      // ("con almeno una persona selezionata") is what the picker's own mode
      // row spells out, where there is room for it.
      parts.push(includePeopleMode === 'all'
        ? t('filters.peopleModeAllShort')
        : t('filters.peopleModeAnyShort'));
    }
    return parts.join(' · ');
  };

  const periodValue = (): string => {
    const from = isoToDateInput(filters.common.dateTakenFrom);
    const to = isoToDateInput(filters.common.dateTakenTo);
    if (from.length === 0 && to.length === 0) return anyLabel;
    return `${from.length > 0 ? from : none} → ${to.length > 0 ? to : none}`;
  };

  const minutesLabel = (minutes: number | null): string =>
    minutes === null ? anyLabel : `${minutes} min`;

  // A Record, not a switch: a TvFilterId with no view here is a compile error,
  // so the catalog cannot offer a row this panel does not know how to draw.
  const views: Record<TvFilterId, RowView> = {
    metadataQuery: {
      label: t('filters.search'),
      value: filters.common.metadataQuery.length > 0 ? filters.common.metadataQuery : anyLabel,
      onSelect: () => open({ kind: 'text', target: 'metadataQuery' }, 'metadataQuery'),
    },
    favorite: {
      label: t('filters.favorite'),
      value: triLabel(filters.common.favorite, t('filters.favoriteYes'), t('filters.favoriteNo')),
      onSelect: () => patchCommon({ favorite: cycle(TRISTATE, filters.common.favorite) }),
    },
    minRating: {
      label: t('filters.rating'),
      value: filters.common.minRating === null ? anyLabel : `★ ${filters.common.minRating}+`,
      onSelect: () => patchCommon({ minRating: cycle(RATINGS, filters.common.minRating) }),
    },
    period: {
      label: t('filters.period'),
      value: periodValue(),
      onSelect: () => open({ kind: 'period' }, 'period'),
    },
    albumMembership: {
      label: t('filters.albumMembership'),
      value: filters.common.albumMembership === 'assigned'
        ? t('filters.membershipAssigned')
        : filters.common.albumMembership === 'unassigned'
          ? t('filters.membershipUnassigned')
          : anyLabel,
      onSelect: () => patchCommon({
        albumMembership: cycle(MEMBERSHIPS, filters.common.albumMembership),
      }),
    },
    people: {
      label: t('filters.people'),
      value: peopleValue(),
      onSelect: () => open({ kind: 'people' }, 'people'),
    },
    hasGps: {
      label: t('filters.gps'),
      value: triLabel(filters.photo.hasGps, t('filters.gpsYes'), t('filters.gpsNo')),
      onSelect: () => patchPhoto({ hasGps: cycle(TRISTATE, filters.photo.hasGps) }),
    },
    collapseDuplicates: {
      label: t('filters.duplicates'),
      value: filters.photo.collapseDuplicates
        ? t('filters.duplicatesCollapse')
        : t('filters.duplicatesShow'),
      onSelect: () => patchPhoto({ collapseDuplicates: !filters.photo.collapseDuplicates }),
    },
    durationMin: {
      label: t('filters.durationMin'),
      value: minutesLabel(secondsToMinutes(filters.video.durationMinSeconds)),
      onSelect: () => patchVideo({
        durationMinSeconds: minutesToSeconds(cycle(
          [null, ...DURATION_PRESET_MINUTES],
          secondsToMinutes(filters.video.durationMinSeconds),
        )),
      }),
    },
    durationMax: {
      label: t('filters.durationMax'),
      value: minutesLabel(secondsToMinutes(filters.video.durationMaxSeconds)),
      onSelect: () => patchVideo({
        durationMaxSeconds: minutesToSeconds(cycle(
          [null, ...DURATION_PRESET_MINUTES],
          secondsToMinutes(filters.video.durationMaxSeconds),
        )),
      }),
    },
    minHeight: {
      label: t('filters.resolution'),
      value: filters.video.minHeight === null ? anyLabel : `${filters.video.minHeight}p+`,
      onSelect: () => patchVideo({ minHeight: cycle([null, ...MIN_HEIGHT_PRESETS], filters.video.minHeight) }),
    },
    codec: {
      label: t('filters.codec'),
      value: filters.video.codec.length > 0 ? filters.video.codec : anyLabel,
      onSelect: () => open({ kind: 'text', target: 'codec' }, 'codec'),
    },
    hasAudio: {
      label: t('filters.audio'),
      value: triLabel(filters.video.hasAudio, t('filters.audioYes'), t('filters.audioNo')),
      onSelect: () => patchVideo({ hasAudio: cycle(TRISTATE, filters.video.hasAudio) }),
    },
  };

  const activeCount = activeFilterCount(draftIdentity);
  let renderedSection: string | null = null;

  return (
    <PanelShell title={t('filters.title')} onBack={onCancel}>
      <Text style={styles.activeSummary}>
        {activeCount === 0
          ? t('filters.noneActive')
          : t('filters.activeCount', { count: String(activeCount) })}
      </Text>

      {rows.map((row) => {
        const view = views[row.id];
        const sectionHeader = renderedSection === row.section ? null : row.section;
        renderedSection = row.section;
        return (
          <View key={row.id}>
            {sectionHeader !== null && (
              <Text style={styles.section}>
                {sectionHeader === 'photo'
                  ? t('filters.sectionPhoto')
                  : sectionHeader === 'video'
                    ? t('filters.sectionVideo')
                    : t('filters.sectionCommon')}
              </Text>
            )}
            <FilterRow
              label={view.label}
              value={view.value}
              active={row.active}
              opensEditor={row.editor !== 'cycle'}
              accessibilityLabel={row.active
                ? t('filters.rowA11yActive', { label: view.label, value: view.value })
                : t('filters.rowA11y', { label: view.label, value: view.value })}
              hasTVPreferredFocus={focusKey === row.id}
              onFocus={() => { focusRef.current = row.id; }}
              onSelect={view.onSelect}
            />
          </View>
        );
      })}

      <Text style={styles.section}>{t('filters.sectionOrder')}</Text>
      <FilterRow
        label={t('filters.sort')}
        value={t(`filters.sort.${sort}` as Parameters<typeof t>[0])}
        active={sort !== 'created'}
        opensEditor={false}
        accessibilityLabel={t('filters.rowA11y', {
          label: t('filters.sort'),
          value: t(`filters.sort.${sort}` as Parameters<typeof t>[0]),
        })}
        hasTVPreferredFocus={focusKey === 'sort'}
        onFocus={() => { focusRef.current = 'sort'; }}
        onSelect={() => setSort(cycle(SORTS, sort))}
      />
      <FilterRow
        label={t('filters.direction')}
        value={direction === 'desc' ? t('filters.newest') : t('filters.oldest')}
        active={direction !== 'desc'}
        opensEditor={false}
        accessibilityLabel={t('filters.rowA11y', {
          label: t('filters.direction'),
          value: direction === 'desc' ? t('filters.newest') : t('filters.oldest'),
        })}
        hasTVPreferredFocus={focusKey === 'direction'}
        onFocus={() => { focusRef.current = 'direction'; }}
        onSelect={() => setDirection(direction === 'desc' ? 'asc' : 'desc')}
      />

      <View style={styles.actions}>
        <FocusableButton
          label={t('filters.resetAll')}
          onPress={() => {
            setFilters(clearActiveFilters(draftIdentity));
            setSort('created');
            setDirection('desc');
          }}
          hasTVPreferredFocus={focusKey === 'reset'}
          onFocusChange={(f) => { if (f) focusRef.current = 'reset'; }}
        />
        <FocusableButton
          label={t('common.cancel')}
          onPress={onCancel}
          hasTVPreferredFocus={focusKey === 'cancel'}
          onFocusChange={(f) => { if (f) focusRef.current = 'cancel'; }}
        />
        <FocusableButton
          label={activeCount === 0
            ? t('filters.apply')
            : t('filters.applyCount', { count: String(activeCount) })}
          onPress={() => onApply(filters, sort, direction)}
          hasTVPreferredFocus={focusKey === 'apply'}
          onFocusChange={(f) => { if (f) focusRef.current = 'apply'; }}
        />
      </View>
      <Text style={styles.hint}>{t('filters.currentCount', { count: String(resultCount) })}</Text>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  activeSummary: {
    color: colors.text,
    fontSize: font.body,
    fontWeight: '700',
    marginBottom: spacing.xs,
  },
  section: {
    color: colors.text,
    fontSize: font.caption,
    fontWeight: '800',
    letterSpacing: 2,
    marginTop: spacing.md,
    marginBottom: spacing.xs,
  },
  actions: {
    flexDirection: 'row',
    gap: spacing.md,
    marginTop: spacing.lg,
    justifyContent: 'center',
  },
  hint: {
    color: colors.muted,
    fontSize: font.caption,
    textAlign: 'center',
    marginTop: spacing.sm,
  },
});
