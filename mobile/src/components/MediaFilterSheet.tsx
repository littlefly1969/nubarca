// The mobile filter sheet (§7, §9, §11, §17).
//
// It shows exactly the controls valid for the CURRENT kind, which is the same
// rule the shared model enforces on the wire: common filters always, photo
// filters only on the image tab, video filters only on the video tab. A
// control the backend would reject is never offered, so an incompatible filter
// cannot be applied by accident and then silently dropped.
//
// The sheet edits a DRAFT and commits on Apply. Nothing refetches while the
// user is still choosing, and dismissing throws the draft away — a phone has
// no room for the web's apply-as-you-type, and a list that reloads under a
// half-finished choice is worse than one that waits.

import React, { useEffect, useState } from 'react';
import {
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import type {
  AlbumMembership,
  MediaKindScope,
  MediaSortField,
  MediaWorkspaceFilters,
  MediaWorkspaceIdentity,
} from '@nubarca/contracts';
import { draftFrom, referencedPersonIds } from '../media/mediaFilterState';
import { PeopleFilterSheet } from './PeopleFilterSheet';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

/** A row of mutually exclusive choices; the selected one can be tapped off. */
function Choice<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T | null;
  options: Array<{ value: T; label: string }>;
  onChange: (next: T | null) => void;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <View style={styles.group}>
      <Text style={styles.groupLabel}>{label}</Text>
      <View style={styles.optionRow}>
        {options.map((option) => {
          const on = value === option.value;
          return (
            <Pressable
              key={option.value}
              accessibilityRole="radio"
              accessibilityState={{ selected: on }}
              onPress={() => onChange(on ? null : option.value)}
              style={[styles.option, on && styles.optionOn]}
            >
              <Text style={[styles.optionText, on && styles.optionTextOn]}>{option.label}</Text>
            </Pressable>
          );
        })}
      </View>
    </View>
  );
}

function NumberField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number | null;
  onChange: (next: number | null) => void;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <View style={styles.numberField}>
      <Text style={styles.numberLabel}>{label}</Text>
      <TextInput
        style={styles.numberInput}
        keyboardType="number-pad"
        value={value === null ? '' : String(value)}
        onChangeText={(text) => {
          const trimmed = text.trim();
          if (trimmed.length === 0) return onChange(null);
          const parsed = Number(trimmed);
          onChange(Number.isFinite(parsed) && parsed >= 0 ? parsed : null);
        }}
        accessibilityLabel={label}
      />
    </View>
  );
}

export function MediaFilterSheet({
  visible,
  identity,
  onApply,
  onClose,
}: {
  visible: boolean;
  identity: MediaWorkspaceIdentity;
  onApply: (filters: MediaWorkspaceFilters, sort: MediaSortField, direction: 'asc' | 'desc') => void;
  onClose: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const [draft, setDraft] = useState<MediaWorkspaceIdentity>(() => draftFrom(identity));
  const [peopleOpen, setPeopleOpen] = useState(false);

  // Re-seed from the applied state each time the sheet opens, so a previously
  // abandoned draft never reappears.
  useEffect(() => {
    if (visible) setDraft(draftFrom(identity));
  }, [visible, identity]);

  const kind: MediaKindScope = draft.mediaKind;
  const filters = draft.filters;
  const setFilters = (next: MediaWorkspaceFilters) => setDraft((d) => ({ ...d, filters: next }));
  const setCommon = (patch: Partial<MediaWorkspaceFilters['common']>) =>
    setFilters({ ...filters, common: { ...filters.common, ...patch } });
  const setPhoto = (patch: Partial<MediaWorkspaceFilters['photo']>) =>
    setFilters({ ...filters, photo: { ...filters.photo, ...patch } });
  const setVideo = (patch: Partial<MediaWorkspaceFilters['video']>) =>
    setFilters({ ...filters, video: { ...filters.video, ...patch } });

  const peopleCount = referencedPersonIds(filters).length;

  return (
    <Modal visible={visible} animationType="slide" onRequestClose={onClose}>
      <View style={styles.root}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('filters.title')}</Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('filters.close')}
            onPress={onClose}
            hitSlop={8}
          >
            <Ionicons name="close" size={26} color={colors.textPrimary} />
          </Pressable>
        </View>

        <ScrollView contentContainerStyle={styles.body}>
          <View style={styles.group}>
            <Text style={styles.groupLabel}>{t('filters.search')}</Text>
            <TextInput
              style={styles.textInput}
              value={filters.common.metadataQuery}
              onChangeText={(text) => setCommon({ metadataQuery: text })}
              autoCorrect={false}
              accessibilityLabel={t('filters.search')}
            />
          </View>

          {/* VISUAL search (§10) is a different backend operation from the
              metadata search above: it ranks by what a photo or video SHOWS,
              and its relevance cursor is its own. It works on BOTH kinds.

              It applies everywhere, so there is no condition here at all: the
              semantic route takes an optional albumId, and inside an album the
              search is CONFINED to it rather than hidden or answered from the
              whole library. The predicate that used to guard this disappeared
              with the limitation it described. */}
          <View style={styles.group}>
            <Text style={styles.groupLabel}>{t('filters.visual')}</Text>
            <TextInput
              style={styles.textInput}
              value={filters.photo.visualQuery}
              onChangeText={(text) => setPhoto({ visualQuery: text })}
              autoCorrect={false}
              accessibilityLabel={t('filters.visual')}
            />
            <Text style={styles.hint}>{t('filters.visualHint')}</Text>
          </View>

          <Choice
            label={t('filters.favorite')}
            value={filters.common.favorite === null ? null : filters.common.favorite ? 'yes' : 'no'}
            options={[
              { value: 'yes', label: t('filters.favoriteOnly') },
              { value: 'no', label: t('filters.favoriteExclude') },
            ]}
            onChange={(next) => setCommon({ favorite: next === null ? null : next === 'yes' })}
          />

          <Choice
            label={t('filters.minRating')}
            value={filters.common.minRating === null ? null : String(filters.common.minRating)}
            options={[1, 2, 3, 4, 5].map((n) => ({ value: String(n), label: `${n}★` }))}
            onChange={(next) => setCommon({ minRating: next === null ? null : Number(next) })}
          />

          <View style={styles.group}>
            <Text style={styles.groupLabel}>{t('filters.dateFrom')} / {t('filters.dateTo')}</Text>
            <View style={styles.dateRow}>
              <TextInput
                style={[styles.textInput, styles.dateInput]}
                placeholderTextColor={colors.textTertiary}
                placeholder="YYYY-MM-DD"
                value={filters.common.dateTakenFrom.slice(0, 10)}
                onChangeText={(text) =>
                  setCommon({ dateTakenFrom: text.length === 10 ? `${text}T00:00:00.000Z` : '' })}
                accessibilityLabel={t('filters.dateFrom')}
              />
              <TextInput
                style={[styles.textInput, styles.dateInput]}
                placeholderTextColor={colors.textTertiary}
                placeholder="YYYY-MM-DD"
                value={filters.common.dateTakenTo.slice(0, 10)}
                onChangeText={(text) =>
                  setCommon({ dateTakenTo: text.length === 10 ? `${text}T23:59:59.999Z` : '' })}
                accessibilityLabel={t('filters.dateTo')}
              />
            </View>
          </View>

          {/* Album membership is a LIBRARY concern: inside an album every item
              is a member, so the control is not offered there at all. */}
          {draft.source.kind === 'library' && (
            <Choice<AlbumMembership>
              label={t('filters.albumMembership')}
              value={filters.common.albumMembership === 'any' ? null : filters.common.albumMembership}
              options={[
                { value: 'assigned', label: t('filters.membershipAssigned') },
                { value: 'unassigned', label: t('filters.membershipUnassigned') },
              ]}
              onChange={(next) => setCommon({ albumMembership: next ?? 'any' })}
            />
          )}

          <Choice<MediaSortField>
            label={t('filters.sort')}
            value={draft.sort}
            options={[
              { value: 'datetaken', label: t('filters.sortDatetaken') },
              { value: 'created', label: t('filters.sortCreated') },
              { value: 'name', label: t('filters.sortName') },
              { value: 'size', label: t('filters.sortSize') },
            ]}
            onChange={(next) => setDraft((d) => ({ ...d, sort: next ?? 'datetaken' }))}
          />
          <Choice<'asc' | 'desc'>
            label={t('filters.directionDesc')}
            value={draft.direction}
            options={[
              { value: 'desc', label: t('filters.directionDesc') },
              { value: 'asc', label: t('filters.directionAsc') },
            ]}
            onChange={(next) => setDraft((d) => ({ ...d, direction: next ?? 'desc' }))}
          />

          {/* ---- photo-only, and only on the photo tab ---- */}
          {kind === 'image' && (
            <>
              <Pressable
                accessibilityRole="button"
                onPress={() => setPeopleOpen(true)}
                style={({ pressed }) => [styles.peopleBtn, pressed && styles.pressed]}
              >
                <Ionicons name="people-outline" size={20} color={colors.accent} />
                <Text style={styles.peopleBtnText}>{t('filters.people')}</Text>
                {peopleCount > 0 && <Text style={styles.peopleCount}>{peopleCount}</Text>}
                <Ionicons name="chevron-forward" size={18} color={colors.textTertiary} />
              </Pressable>

              <Choice
                label={t('filters.hasGps')}
                value={filters.photo.hasGps === null ? null : filters.photo.hasGps ? 'yes' : 'no'}
                options={[
                  { value: 'yes', label: t('filters.hasGpsYes') },
                  { value: 'no', label: t('filters.hasGpsNo') },
                ]}
                onChange={(next) => setPhoto({ hasGps: next === null ? null : next === 'yes' })}
              />
              <Choice
                label={t('filters.collapseDuplicates')}
                value={filters.photo.collapseDuplicates ? 'on' : null}
                options={[{ value: 'on', label: t('filters.collapseDuplicates') }]}
                onChange={(next) => setPhoto({ collapseDuplicates: next !== null })}
              />
            </>
          )}

          {/* ---- video-only, and only on the video tab ---- */}
          {kind === 'video' && (
            <>
              <View style={styles.group}>
                <Text style={styles.groupLabel}>{t('filters.duration')}</Text>
                <View style={styles.dateRow}>
                  <NumberField
                    label={t('filters.durationMin')}
                    value={filters.video.durationMinSeconds}
                    onChange={(next) => setVideo({ durationMinSeconds: next })}
                  />
                  <NumberField
                    label={t('filters.durationMax')}
                    value={filters.video.durationMaxSeconds}
                    onChange={(next) => setVideo({ durationMaxSeconds: next })}
                  />
                </View>
              </View>
              <Choice
                label={t('filters.minHeight')}
                value={filters.video.minHeight === null ? null : String(filters.video.minHeight)}
                options={[720, 1080, 2160].map((h) => ({ value: String(h), label: `${h}p` }))}
                onChange={(next) => setVideo({ minHeight: next === null ? null : Number(next) })}
              />
              <View style={styles.group}>
                <Text style={styles.groupLabel}>{t('filters.codec')}</Text>
                <TextInput
                  style={styles.textInput}
                  value={filters.video.codec}
                  autoCapitalize="none"
                  autoCorrect={false}
                  onChangeText={(text) => setVideo({ codec: text.trim() })}
                  accessibilityLabel={t('filters.codec')}
                />
              </View>
              <Choice
                label={t('filters.hasAudio')}
                value={filters.video.hasAudio === null ? null : filters.video.hasAudio ? 'yes' : 'no'}
                options={[
                  { value: 'yes', label: t('filters.hasAudioYes') },
                  { value: 'no', label: t('filters.hasAudioNo') },
                ]}
                onChange={(next) => setVideo({ hasAudio: next === null ? null : next === 'yes' })}
              />
            </>
          )}
        </ScrollView>

        <View style={styles.footer}>
          <Pressable
            accessibilityRole="button"
            onPress={() => onApply(draft.filters, draft.sort, draft.direction)}
            style={({ pressed }) => [styles.apply, pressed && styles.pressed]}
          >
            <Text style={styles.applyText}>{t('filters.apply')}</Text>
          </Pressable>
        </View>

        <PeopleFilterSheet
          visible={peopleOpen}
          filters={filters}
          onChange={setFilters}
          onClose={() => setPeopleOpen(false)}
        />
      </View>
    </Modal>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    root: { flex: 1, backgroundColor: colors.surface, paddingTop: 48 },
    header: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
      paddingHorizontal: 16, paddingBottom: 8,
    },
    title: { fontSize: 20, fontWeight: '600', color: colors.textPrimary },
    body: { paddingHorizontal: 16, paddingBottom: 24, gap: 18 },
    group: { gap: 8 },
    groupLabel: { fontSize: 13, color: colors.textTertiary, textTransform: 'uppercase' },
    hint: { fontSize: 12, color: colors.textTertiary },
    optionRow: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
    option: { paddingHorizontal: 14, paddingVertical: 8, borderRadius: 16, backgroundColor: colors.surfaceMuted },
    optionOn: { backgroundColor: colors.accentStrong },
    optionText: { fontSize: 14, color: colors.textSecondary },
    optionTextOn: { color: colors.textOnAccent },
    textInput: {
      paddingHorizontal: 12, paddingVertical: 10, borderRadius: 10,
      backgroundColor: colors.surfaceMuted, color: colors.textPrimary, fontSize: 15,
    },
    dateRow: { flexDirection: 'row', gap: 12 },
    dateInput: { flex: 1 },
    numberField: { flex: 1, gap: 6 },
    numberLabel: { fontSize: 12, color: colors.textTertiary },
    numberInput: {
      paddingHorizontal: 12, paddingVertical: 10, borderRadius: 10,
      backgroundColor: colors.surfaceMuted, color: colors.textPrimary, fontSize: 15,
    },
    peopleBtn: {
      flexDirection: 'row', alignItems: 'center', gap: 10,
      paddingHorizontal: 14, paddingVertical: 14, borderRadius: 12, backgroundColor: colors.surfaceMuted,
    },
    peopleBtnText: { flex: 1, fontSize: 15, color: colors.textPrimary },
    peopleCount: {
      minWidth: 22, textAlign: 'center', color: colors.textOnAccent, backgroundColor: colors.accentStrong,
      borderRadius: 11, paddingVertical: 2, fontSize: 12, overflow: 'hidden',
    },
    footer: { padding: 16, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: colors.separator },
    apply: { backgroundColor: colors.accentStrong, borderRadius: 12, paddingVertical: 14, alignItems: 'center' },
    applyText: { color: colors.textOnAccent, fontSize: 16, fontWeight: '600' },
    pressed: { opacity: 0.7 },
  }),
);
