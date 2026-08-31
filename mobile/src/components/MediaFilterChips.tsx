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
import { colors } from '../ui/tokens';

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
  onRemove,
  onClearAll,
}: {
  chips: FilterChipDescriptor[];
  people: ReadonlyMap<string, PersonSummary>;
  onRemove: (kind: FilterChipKind) => void;
  onClearAll: () => void;
}): React.JSX.Element | null {
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
      {chips.map((chip) => (
        <Pressable
          key={chip.key}
          accessibilityRole="button"
          accessibilityLabel={`${t('chips.remove')}: ${label(chip)}`}
          onPress={() => onRemove(chip.kind)}
          style={({ pressed }) => [styles.chip, pressed && styles.pressed]}
        >
          <Text style={styles.chipText} numberOfLines={1}>{label(chip)}</Text>
          <Ionicons name="close" size={14} color={colors.accent} style={styles.chipIcon} />
        </Pressable>
      ))}
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

const styles = StyleSheet.create({
  strip: { flexGrow: 0, maxHeight: 46 },
  stripContent: { paddingHorizontal: 12, paddingVertical: 6, gap: 8, alignItems: 'center' },
  chip: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#E8EEFB',
    borderRadius: 16,
    paddingLeft: 12,
    paddingRight: 8,
    paddingVertical: 6,
    maxWidth: 240,
  },
  chipText: { color: colors.accent, fontSize: 13, flexShrink: 1 },
  chipIcon: { marginLeft: 4 },
  clearAll: { paddingHorizontal: 10, paddingVertical: 6 },
  clearAllText: { color: colors.textTertiary, fontSize: 13, textDecorationLine: 'underline' },
  pressed: { opacity: 0.6 },
});
