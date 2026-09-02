// Active-filter chips (§18).
//
// The chips describe the APPLIED query, never the draft, so they can never
// advertise a filter the visible results are not actually under. Their content
// is decided by the shared model (buildFilterChips), which already knows that
// a photo filter is inert on the video tab and omits it; this component only
// localizes the descriptors and attaches a remove handler.
//
// Person chips arrive as IDS. The label is resolved from the People catalogue
// at render time, so renaming somebody changes what the chip says without
// touching the filter — the query still keys on the id.

import React from 'react';
import { Pressable, ScrollView, StyleSheet, Text } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import type { FilterChipDescriptor, FilterChipKind } from '../media/mediaFilterState';
import type { PersonSummary } from '../api/people';
import { useI18n } from '../i18n';
import { radius, spacing, typography } from '../ui/tokens';
import { themed, useColors } from '../ui/theme';

function personLabel(
  ids: string[] | undefined,
  people: ReadonlyMap<string, PersonSummary>,
  unnamed: string,
): string {
  if (ids === undefined || ids.length === 0) return '';
  return ids
    .map((id) => {
      const name = people.get(id)?.name;
      return name !== null && name !== undefined && name.length > 0 ? name : unnamed;
    })
    .join(', ');
}

export function MediaFilterChips({
  chips,
  people,
  inert,
  onRemove,
  onClearAll,
}: {
  chips: FilterChipDescriptor[];
  people: ReadonlyMap<string, PersonSummary>;
  /** Chips a running visual search does NOT apply. They are drawn dimmed and
   * announced as inert: a filter that is set, shown, and silently ignored
   * makes the results look filtered when they are not. */
  inert?: readonly FilterChipKind[];
  onRemove: (kind: FilterChipKind) => void;
  onClearAll: () => void;
}): React.JSX.Element | null {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  if (chips.length === 0) return null;

  const unnamed = t('filters.peopleUnnamed');

  function label(chip: FilterChipDescriptor): string {
    switch (chip.kind) {
      case 'metadata':
        return `${t('chips.search')}: ${chip.text ?? ''}`;
      case 'visual':
        return `${t('chips.search')}: ${chip.text ?? ''}`;
      case 'people-include': {
        const names = personLabel(chip.personIds, people, unnamed);
        const mode = chip.peopleMode === 'any' ? t('filters.peopleModeAny') : t('filters.peopleModeAll');
        // The mode only means something with more than one person.
        return (chip.personIds?.length ?? 0) > 1
          ? `${t('chips.with')}: ${names} (${mode})`
          : `${t('chips.with')}: ${names}`;
      }
      case 'people-exclude':
        return `${t('chips.without')}: ${personLabel(chip.personIds, people, unnamed)}`;
      case 'date': {
        const from = chip.dateFrom !== undefined && chip.dateFrom.length > 0 ? chip.dateFrom.slice(0, 10) : '…';
        const to = chip.dateTo !== undefined && chip.dateTo.length > 0 ? chip.dateTo.slice(0, 10) : '…';
        return `${t('chips.date')}: ${from} → ${to}`;
      }
      case 'favorite':
        return chip.favorite === true ? t('chips.favorite') : t('chips.notFavorite');
      case 'min-rating':
        return t('chips.minRating', { n: String(chip.minRating ?? 0) });
      case 'gps':
        return chip.hasGps === true ? t('chips.gps') : t('chips.noGps');
      case 'collapse':
        return t('chips.collapse');
      case 'album-membership':
        return chip.albumMembership === 'assigned' ? t('chips.inAlbum') : t('chips.notInAlbum');
      case 'similar':
        return t('chips.similar');
      case 'duration': {
        const min = chip.durationMinSeconds ?? null;
        const max = chip.durationMaxSeconds ?? null;
        const range = `${min ?? '…'}–${max ?? '…'}s`;
        return `${t('chips.duration')}: ${range}`;
      }
      case 'min-height':
        return t('chips.minHeight', { n: String(chip.minHeight ?? 0) });
      case 'codec':
        return t('chips.codec', { v: chip.text ?? '' });
      case 'has-audio':
        return chip.hasAudio === true ? t('chips.audio') : t('chips.noAudio');
    }
  }

  return (
    <ScrollView
      horizontal
      showsHorizontalScrollIndicator={false}
      style={styles.strip}
      contentContainerStyle={styles.stripContent}
    >
      {chips.map((chip) => {
        const isInert = inert !== undefined && inert.includes(chip.kind);
        return (
          <Pressable
            key={chip.key}
            accessibilityRole="button"
            accessibilityLabel={
              isInert
                ? `${label(chip)} — ${t('chips.inert')}`
                : `${t('chips.remove')}: ${label(chip)}`
            }
            onPress={() => onRemove(chip.kind)}
            style={({ pressed }) => [styles.chip, isInert && styles.inert, pressed && styles.pressed]}
          >
            {isInert && (
              <Ionicons
                name="alert-circle-outline"
                size={13}
                color={colors.textTertiary}
                style={styles.chipIcon}
              />
            )}
            <Text
              style={[styles.chipText, isInert && styles.inertText]}
              numberOfLines={1}
            >
              {label(chip)}
            </Text>
            <Ionicons
              name="close"
              size={14}
              color={isInert ? colors.textTertiary : colors.accent}
              style={styles.chipIcon}
            />
          </Pressable>
        );
      })}
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t('filters.clearAll')}
        onPress={onClearAll}
        style={({ pressed }) => [styles.clearAll, pressed && styles.pressed]}
      >
        <Text style={styles.clearAllText}>{t('filters.clearAll')}</Text>
      </Pressable>
    </ScrollView>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    strip: { flexGrow: 0, maxHeight: 46 },
    stripContent: {
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.s - 2,
      gap: spacing.s,
      alignItems: 'center',
    },
    // An APPLIED filter: the accent wash with an accent border and an accent
    // label. One language, whatever the filter is about.
    chip: {
      flexDirection: 'row',
      alignItems: 'center',
      backgroundColor: colors.accentSubtle,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.accent,
      borderRadius: radius.pill,
      paddingLeft: spacing.m,
      paddingRight: spacing.s,
      paddingVertical: spacing.s - 2,
      maxWidth: 240,
    },
    chipText: { ...typography.label, color: colors.accent, flexShrink: 1 },
    // INERT: set, shown, and not applied to this query. It recedes to a quiet
    // recess with tertiary text, and the strike-through says so without colour.
    inert: { backgroundColor: colors.surfaceSubtle, borderColor: colors.separator },
    inertText: { color: colors.textTertiary, textDecorationLine: 'line-through' },
    chipIcon: { marginLeft: spacing.xs },
    // Tertiary, deliberately: Clear All removes everything, and dressing it as
    // another blue chip would make the most destructive control the loudest.
    clearAll: { paddingHorizontal: spacing.s + 2, paddingVertical: spacing.s - 2 },
    clearAllText: {
      ...typography.label,
      color: colors.textTertiary,
      textDecorationLine: 'underline',
    },
    pressed: { opacity: 0.6 },
  }),
);
