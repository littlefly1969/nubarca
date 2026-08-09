import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { CycleRow } from '../gallery/CycleRow';
import { PanelShell } from '../gallery/PanelShell';
import { TvKeyboardPanel } from '../gallery/TvKeyboardPanel';
import { useI18n } from '../../i18n';
import {
  clearActiveFilters,
  DURATION_PRESET_MINUTES,
  MIN_HEIGHT_PRESETS,
  minutesToSeconds,
  secondsToMinutes,
  type MediaSortDirection,
  type MediaSortField,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
} from '../../personal/mediaWorkspaceQuery';

// TV filter panel for the unified library.
//
// The SEMANTICS are the web Media Workspace's, exactly — same groups, same
// defaults, same tri-state meanings, same canonical units. The LAYOUT is not:
// this is a 10-foot remote UI, so the desktop sheet's dense multi-column form is
// replaced by one vertical list of large rows where SELECT cycles a value. One
// setting is focused at a time and there is nothing to hunt for.
//
// Which rows exist follows the active tab, and that is the product rule rather
// than a cosmetic one: photo controls appear only on Foto, video controls only
// on Video, and Tutti offers the common ones alone — because those are the only
// filters the backend will accept for that kind. A control that cannot be shown
// also cannot be sent (see mediaWorkspaceQuery.queryToWire), so the panel and
// the wire agree by construction.
//
// Nothing takes effect until Apply. The panel edits a DRAFT and hands it back
// whole; Reset clears the current kind's filters without touching the retained
// state of the others.

interface Props {
  applied: MediaWorkspaceIdentity;
  resultCount: number;
  onApply: (filters: MediaWorkspaceFilters, sort: MediaSortField, direction: MediaSortDirection) => void;
  onCancel: () => void;
}

type Keyboard = 'none' | 'metadata' | 'codec';

const SORTS: readonly MediaSortField[] = ['created', 'datetaken', 'name', 'size'];
const RATINGS: readonly (number | null)[] = [null, 1, 2, 3, 4, 5];
const TRISTATE: readonly (boolean | null)[] = [null, true, false];
const MEMBERSHIPS = ['any', 'assigned', 'unassigned'] as const;

function cycle<T>(values: readonly T[], current: T): T {
  const at = values.indexOf(current);
  return values[(at + 1) % values.length];
}

export function LibraryFilterPanel({ applied, resultCount, onApply, onCancel }: Props) {
  const { t, lang } = useI18n();
  const [filters, setFilters] = useState<MediaWorkspaceFilters>(() => ({
    common: { ...applied.filters.common },
    photo: { ...applied.filters.photo },
    video: { ...applied.filters.video },
  }));
  const [sort, setSort] = useState<MediaSortField>(applied.sort);
  const [direction, setDirection] = useState<MediaSortDirection>(applied.direction);
  const [keyboard, setKeyboard] = useState<Keyboard>('none');

  const isLibrary = applied.source.kind === 'library';
  const kind = applied.mediaKind;

  const patchCommon = (patch: Partial<MediaWorkspaceFilters['common']>) =>
    setFilters((f) => ({ ...f, common: { ...f.common, ...patch } }));
  const patchPhoto = (patch: Partial<MediaWorkspaceFilters['photo']>) =>
    setFilters((f) => ({ ...f, photo: { ...f.photo, ...patch } }));
  const patchVideo = (patch: Partial<MediaWorkspaceFilters['video']>) =>
    setFilters((f) => ({ ...f, video: { ...f.video, ...patch } }));

  const anyLabel = t('filters.any');
  const triLabel = (value: boolean | null, yes: string, no: string) =>
    value === null ? anyLabel : value ? yes : no;

  if (keyboard !== 'none') {
    const isCodec = keyboard === 'codec';
    return (
      <TvKeyboardPanel
        title={isCodec ? t('filters.codec') : t('filters.search')}
        mode="text"
        initialValue={isCodec ? filters.video.codec : filters.common.metadataQuery}
        onCancel={() => setKeyboard('none')}
        onSubmit={(value) => {
          if (isCodec) patchVideo({ codec: value.trim() });
          else patchCommon({ metadataQuery: value.trim() });
          setKeyboard('none');
        }}
      />
    );
  }

  return (
    <PanelShell title={t('filters.title')} onBack={onCancel}>
      <Text style={styles.section}>{t('filters.sectionCommon')}</Text>

      <CycleRow
        label={t('filters.search')}
        value={filters.common.metadataQuery || t('filters.none')}
        onCycle={() => setKeyboard('metadata')}
        hasTVPreferredFocus
      />
      <CycleRow
        label={t('filters.favorite')}
        value={triLabel(filters.common.favorite, t('filters.favoriteYes'), t('filters.favoriteNo'))}
        onCycle={() => patchCommon({ favorite: cycle(TRISTATE, filters.common.favorite) })}
      />
      <CycleRow
        label={t('filters.rating')}
        value={filters.common.minRating === null ? anyLabel : `★ ${filters.common.minRating}+`}
        onCycle={() => patchCommon({ minRating: cycle(RATINGS, filters.common.minRating) })}
      />
      <CycleRow
        label={t('filters.period')}
        value={periodLabel(filters.common.dateTakenFrom, filters.common.dateTakenTo, anyLabel)}
        onCycle={() => patchCommon(nextPeriod(filters.common.dateTakenFrom))}
      />
      {/* Album membership is a LIBRARY concept. Inside an album every item is a
          member, so the row is absent — and the album endpoint does not accept
          the parameter either, so it cannot be sent by accident. */}
      {isLibrary && (
        <CycleRow
          label={t('filters.albumMembership')}
          value={membershipLabel(filters.common.albumMembership, t)}
          onCycle={() =>
            patchCommon({ albumMembership: cycle(MEMBERSHIPS, filters.common.albumMembership) })}
        />
      )}

      {kind === 'image' && (
        <>
          <Text style={styles.section}>{t('filters.sectionPhoto')}</Text>
          <CycleRow
            label={t('filters.gps')}
            value={triLabel(filters.photo.hasGps, t('filters.gpsYes'), t('filters.gpsNo'))}
            onCycle={() => patchPhoto({ hasGps: cycle(TRISTATE, filters.photo.hasGps) })}
          />
          <CycleRow
            label={t('filters.duplicates')}
            value={filters.photo.collapseDuplicates ? t('filters.duplicatesCollapse') : t('filters.duplicatesShow')}
            onCycle={() => patchPhoto({ collapseDuplicates: !filters.photo.collapseDuplicates })}
          />
          {/* People selection is read-only here: the ids come from the people
              picker and are shown so the user can see (and clear) what is
              applied without the panel duplicating that whole surface. */}
          <CycleRow
            label={t('filters.people')}
            value={
              filters.photo.includePeople.length === 0 && filters.photo.excludePeople.length === 0
                ? anyLabel
                : `+${filters.photo.includePeople.length} / -${filters.photo.excludePeople.length}`
            }
            onCycle={() => patchPhoto({ includePeople: [], excludePeople: [] })}
          />
        </>
      )}

      {kind === 'video' && (
        <>
          <Text style={styles.section}>{t('filters.sectionVideo')}</Text>
          <CycleRow
            label={t('filters.durationMin')}
            value={minutesLabel(secondsToMinutes(filters.video.durationMinSeconds), anyLabel, lang)}
            onCycle={() => patchVideo({
              durationMinSeconds: minutesToSeconds(
                cycle([null, ...DURATION_PRESET_MINUTES], secondsToMinutes(filters.video.durationMinSeconds)),
              ),
            })}
          />
          <CycleRow
            label={t('filters.durationMax')}
            value={minutesLabel(secondsToMinutes(filters.video.durationMaxSeconds), anyLabel, lang)}
            onCycle={() => patchVideo({
              durationMaxSeconds: minutesToSeconds(
                cycle([null, ...DURATION_PRESET_MINUTES], secondsToMinutes(filters.video.durationMaxSeconds)),
              ),
            })}
          />
          <CycleRow
            label={t('filters.resolution')}
            value={filters.video.minHeight === null ? anyLabel : `${filters.video.minHeight}p+`}
            onCycle={() => patchVideo({
              minHeight: cycle([null, ...MIN_HEIGHT_PRESETS], filters.video.minHeight),
            })}
          />
          <CycleRow
            label={t('filters.codec')}
            value={filters.video.codec || anyLabel}
            onCycle={() => setKeyboard('codec')}
          />
          <CycleRow
            label={t('filters.audio')}
            value={triLabel(filters.video.hasAudio, t('filters.audioYes'), t('filters.audioNo'))}
            onCycle={() => patchVideo({ hasAudio: cycle(TRISTATE, filters.video.hasAudio) })}
          />
        </>
      )}

      <Text style={styles.section}>{t('filters.sectionOrder')}</Text>
      <CycleRow
        label={t('filters.sort')}
        value={t(`filters.sort.${sort}` as Parameters<typeof t>[0])}
        onCycle={() => setSort(cycle(SORTS, sort))}
      />
      <CycleRow
        label={t('filters.direction')}
        value={direction === 'desc' ? t('filters.newest') : t('filters.oldest')}
        onCycle={() => setDirection(direction === 'desc' ? 'asc' : 'desc')}
      />

      <View style={styles.actions}>
        <FocusableButton
          label={t('filters.reset')}
          onPress={() => {
            setFilters(clearActiveFilters({ ...applied, filters }));
            setSort('created');
            setDirection('desc');
          }}
        />
        <FocusableButton
          label={t('filters.apply')}
          onPress={() => onApply(filters, sort, direction)}
        />
        <FocusableButton label={t('common.cancel')} onPress={onCancel} />
      </View>
      <Text style={styles.hint}>{t('filters.currentCount', { count: String(resultCount) })}</Text>
    </PanelShell>
  );
}

// Period presets. Absolute instants are computed at cycle time so the panel
// sends the same ISO-8601 UTC boundaries the web sends.
function nextPeriod(from: string): { dateTakenFrom: string; dateTakenTo: string } {
  const now = Date.now();
  const day = 24 * 60 * 60 * 1000;
  const spans = [0, 30, 365];
  const currentDays = from.length === 0
    ? 0
    : Math.round((now - Date.parse(from)) / day) >= 300 ? 365 : 30;
  const next = spans[(spans.indexOf(currentDays) + 1) % spans.length];
  return next === 0
    ? { dateTakenFrom: '', dateTakenTo: '' }
    : { dateTakenFrom: new Date(now - next * day).toISOString(), dateTakenTo: '' };
}

function periodLabel(from: string, to: string, anyLabel: string): string {
  if (from.length === 0 && to.length === 0) return anyLabel;
  return `${from.slice(0, 10) || '…'} → ${to.slice(0, 10) || '…'}`;
}

function membershipLabel(
  value: 'any' | 'assigned' | 'unassigned',
  t: (key: 'filters.any' | 'filters.membershipAssigned' | 'filters.membershipUnassigned') => string,
): string {
  if (value === 'assigned') return t('filters.membershipAssigned');
  if (value === 'unassigned') return t('filters.membershipUnassigned');
  return t('filters.any');
}

function minutesLabel(minutes: number | null, anyLabel: string, lang: 'it' | 'en'): string {
  if (minutes === null) return anyLabel;
  return lang === 'it' ? `${minutes} min` : `${minutes} min`;
}

const styles = StyleSheet.create({
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
