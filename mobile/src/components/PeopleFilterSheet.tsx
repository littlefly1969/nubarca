// The mobile People picker (§13).
//
// Deliberately NOT the web PeopleCombobox. A phone gets a full-screen sheet
// with a search field and a list of rows, each with two explicit affordances:
// include and exclude. Tapping a row's chosen side again removes it.
//
// READ-ONLY (§15, §16). This sheet can choose people to filter by, and that is
// all it can do: there is no rename, no merge, no face assignment, no
// suggestion review. Management belongs to a future dedicated screen reached
// from the library, not from inside a filter — putting it here is exactly the
// architecture the slice asks to avoid.
//
// Identity is the personId throughout. Names are labels: they are searched and
// displayed, never stored in the filter.

import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  Modal,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { matchesPersonQuery, type MediaWorkspaceFilters } from '@nubarca/contracts';
import { personSide, togglePerson, withPeopleMode } from '../media/mediaFilterState';
import { listPeopleForFilter, type PersonSummary } from '../api/people';
import { AuthedImage } from './AuthedImage';
import { personAvatarPath } from '@nubarca/contracts';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

export function PeopleFilterSheet({
  visible,
  filters,
  onChange,
  onClose,
}: {
  visible: boolean;
  filters: MediaWorkspaceFilters;
  onChange: (next: MediaWorkspaceFilters) => void;
  onClose: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const [people, setPeople] = useState<PersonSummary[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [query, setQuery] = useState('');

  // Reload each time the sheet opens: the catalogue changes as recognition
  // runs, and a stale list would offer people who are gone or miss new ones.
  useEffect(() => {
    if (!visible) return undefined;
    const controller = new AbortController();
    setFailed(false);
    setPeople(null);
    listPeopleForFilter(controller.signal).then(
      (loaded) => { if (!controller.signal.aborted) setPeople(loaded); },
      () => { if (!controller.signal.aborted) setFailed(true); },
    );
    return () => controller.abort();
  }, [visible]);

  const shown = useMemo(
    () => (people ?? []).filter((p) => matchesPersonQuery(p, query)),
    [people, query],
  );

  const includedCount = filters.photo.includePeople.length;

  return (
    <Modal visible={visible} animationType="slide" onRequestClose={onClose}>
      <View style={styles.root}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('filters.people')}</Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('filters.close')}
            onPress={onClose}
            hitSlop={8}
            style={({ pressed }) => pressed && styles.pressed}
          >
            <Ionicons name="close" size={26} color={colors.textPrimary} />
          </Pressable>
        </View>

        <TextInput
          style={styles.search}
          placeholderTextColor={colors.textTertiary}
          placeholder={t('filters.peopleSearch')}
          value={query}
          onChangeText={setQuery}
          autoCorrect={false}
          accessibilityLabel={t('filters.peopleSearch')}
        />

        {/* The mode only means something once two people are included. */}
        {includedCount > 1 && (
          <View style={styles.modeRow}>
            <Text style={styles.modeLabel}>{t('filters.peopleMode')}</Text>
            {(['all', 'any'] as const).map((mode) => (
              <Pressable
                key={mode}
                accessibilityRole="radio"
                accessibilityState={{ selected: filters.photo.includePeopleMode === mode }}
                onPress={() => onChange(withPeopleMode(filters, mode))}
                style={[
                  styles.modeChip,
                  filters.photo.includePeopleMode === mode && styles.modeChipOn,
                ]}
              >
                <Text
                  style={[
                    styles.modeChipText,
                    filters.photo.includePeopleMode === mode && styles.modeChipTextOn,
                  ]}
                >
                  {mode === 'all' ? t('filters.peopleModeAll') : t('filters.peopleModeAny')}
                </Text>
              </Pressable>
            ))}
          </View>
        )}

        {failed ? (
          <Text style={styles.empty}>{t('filters.peopleLoadError')}</Text>
        ) : people === null ? (
          <ActivityIndicator style={styles.loading} color={colors.accent} />
        ) : shown.length === 0 ? (
          <Text style={styles.empty}>{t('filters.peopleEmpty')}</Text>
        ) : (
          <FlatList
            data={shown}
            keyExtractor={(p) => p.personId}
            renderItem={({ item }) => {
              const side = personSide(filters, item.personId);
              return (
                <View style={styles.row}>
                  {item.representativeFaceId !== null ? (
                    <AuthedImage
                      path={personAvatarPath(item.representativeFaceId)}
                      style={styles.avatar}
                      resizeMode="cover"
                      accessibilityLabel=""
                    />
                  ) : (
                    <View style={[styles.avatar, styles.avatarEmpty]}>
                      <Ionicons name="person" size={18} color={colors.textTertiary} />
                    </View>
                  )}
                  <View style={styles.rowText}>
                    <Text style={styles.name} numberOfLines={1}>
                      {item.name !== null && item.name.length > 0
                        ? item.name
                        : t('filters.peopleUnnamed')}
                    </Text>
                    <Text style={styles.count}>{item.faceCount}</Text>
                  </View>
                  {(['include', 'exclude'] as const).map((target) => (
                    <Pressable
                      key={target}
                      accessibilityRole="button"
                      accessibilityState={{ selected: side === target }}
                      accessibilityLabel={
                        target === 'include' ? t('filters.peopleInclude') : t('filters.peopleExclude')
                      }
                      onPress={() => onChange(togglePerson(filters, item.personId, target))}
                      style={[
                        styles.sideBtn,
                        side === target && (target === 'include' ? styles.includeOn : styles.excludeOn),
                      ]}
                    >
                      <Ionicons
                        name={target === 'include' ? 'add' : 'remove'}
                        size={18}
                        color={side === target ? colors.textOnAccent : colors.textTertiary}
                      />
                    </Pressable>
                  ))}
                </View>
              );
            }}
          />
        )}
      </View>
    </Modal>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    root: { flex: 1, backgroundColor: colors.surface, paddingTop: 48 },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      paddingHorizontal: 16,
      paddingBottom: 12,
    },
    title: { fontSize: 20, fontWeight: '600', color: colors.textPrimary },
    search: {
      color: colors.textPrimary,
      marginHorizontal: 16,
      marginBottom: 12,
      paddingHorizontal: 12,
      paddingVertical: 10,
      borderRadius: 10,
      backgroundColor: colors.surfaceMuted,
      fontSize: 15,
    },
    modeRow: { flexDirection: 'row', alignItems: 'center', gap: 8, paddingHorizontal: 16, paddingBottom: 12 },
    modeLabel: { color: colors.textTertiary, fontSize: 13, marginRight: 4 },
    modeChip: { paddingHorizontal: 12, paddingVertical: 6, borderRadius: 14, backgroundColor: colors.surfaceMuted },
    modeChipOn: { backgroundColor: colors.accentStrong },
    modeChipText: { fontSize: 13, color: colors.textSecondary },
    modeChipTextOn: { color: colors.textOnAccent },
    row: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 16, paddingVertical: 8, gap: 12 },
    avatar: { width: 44, height: 44, borderRadius: 22 },
    avatarEmpty: { backgroundColor: colors.surfaceMuted, alignItems: 'center', justifyContent: 'center' },
    rowText: { flex: 1 },
    name: { fontSize: 15, color: colors.textPrimary },
    count: { fontSize: 12, color: colors.textTertiary },
    sideBtn: {
      width: 36, height: 36, borderRadius: 18,
      alignItems: 'center', justifyContent: 'center', backgroundColor: colors.surfaceMuted,
    },
    includeOn: { backgroundColor: colors.accentStrong },
    excludeOn: { backgroundColor: colors.danger },
    loading: { marginTop: 32 },
    empty: { textAlign: 'center', marginTop: 32, color: colors.textTertiary },
    pressed: { opacity: 0.6 },
  }),
);
