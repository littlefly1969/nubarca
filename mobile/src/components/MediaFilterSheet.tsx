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
import { Modal, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
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
import { Button, IconButton } from '../ui/components';
import { TextField } from '../ui/fields';
import { iconSizes, radius, spacing, touch, typography } from '../ui/tokens';
import { themed, useColors } from '../ui/theme';

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
              style={({ pressed }) => [styles.option, on && styles.optionOn, pressed && styles.pressed]}
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
    <TextField
      containerStyle={styles.numberField}
      label={label}
      keyboardType="number-pad"
      value={value === null ? '' : String(value)}
      onChangeText={(text) => {
        const trimmed = text.trim();
        if (trimmed.length === 0) return onChange(null);
        const parsed = Number(trimmed);
        onChange(Number.isFinite(parsed) && parsed >= 0 ? parsed : null);
      }}
    />
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
  const insets = useSafeAreaInsets();
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
      <View style={[styles.root, { paddingTop: insets.top }]}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('filters.title')}</Text>
          <IconButton accessibilityLabel={t('filters.close')} onPress={onClose}>
            <Ionicons name="close" size={iconSizes.l} color={colors.textPrimary} />
          </IconButton>
        </View>

        <ScrollView contentContainerStyle={styles.body}>
          <TextField
            label={t('filters.search')}
            value={filters.common.metadataQuery}
            onChangeText={(text) => setCommon({ metadataQuery: text })}
            autoCorrect={false}
          />

          {/* VISUAL search (§10) is a different backend operation from the
              metadata search above: it ranks by what a photo or video SHOWS,
              and its relevance cursor is its own. It works on BOTH kinds.

              It applies everywhere, so there is no condition here at all: the
              semantic route takes an optional albumId, and inside an album the
              search is CONFINED to it rather than hidden or answered from the
              whole library. The predicate that used to guard this disappeared
              with the limitation it described. */}
          <TextField
            label={t('filters.visual')}
            hint={t('filters.visualHint')}
            value={filters.photo.visualQuery}
            onChangeText={(text) => setPhoto({ visualQuery: text })}
            autoCorrect={false}
          />

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

          {/* Two labelled fields rather than one shared label over two boxes:
              each date now says which end of the range it is. */}
          <View style={styles.dateRow}>
            <TextField
              containerStyle={styles.dateInput}
              label={t('filters.dateFrom')}
              placeholder="YYYY-MM-DD"
              value={filters.common.dateTakenFrom.slice(0, 10)}
              onChangeText={(text) =>
                setCommon({ dateTakenFrom: text.length === 10 ? `${text}T00:00:00.000Z` : '' })}
            />
            <TextField
              containerStyle={styles.dateInput}
              label={t('filters.dateTo')}
              placeholder="YYYY-MM-DD"
              value={filters.common.dateTakenTo.slice(0, 10)}
              onChangeText={(text) =>
                setCommon({ dateTakenTo: text.length === 10 ? `${text}T23:59:59.999Z` : '' })}
            />
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
                <Ionicons name="people-outline" size={iconSizes.m} color={colors.accent} />
                <Text style={styles.peopleBtnText}>{t('filters.people')}</Text>
                {peopleCount > 0 && <Text style={styles.peopleCount}>{peopleCount}</Text>}
                <Ionicons name="chevron-forward" size={iconSizes.s} color={colors.textTertiary} />
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
              <TextField
                label={t('filters.codec')}
                value={filters.video.codec}
                autoCapitalize="none"
                autoCorrect={false}
                onChangeText={(text) => setVideo({ codec: text.trim() })}
              />
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

        <View style={[styles.footer, { paddingBottom: spacing.l + insets.bottom }]}>
          <Button
            label={t('filters.apply')}
            onPress={() => onApply(draft.filters, draft.sort, draft.direction)}
          />
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
    // Real safe area, not a 48 px guess at where a status bar might be: that
    // number is wrong on a notch and wasteful without one.
    root: { flex: 1, backgroundColor: colors.surface },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      paddingLeft: spacing.l,
      paddingRight: spacing.s,
      paddingBottom: spacing.s,
    },
    title: { ...typography.pageTitle, color: colors.textPrimary },
    // Groups are separated by RHYTHM. No card around each one: this is a dense
    // precision tool, and a page of boxes would make it a form to fill in.
    body: { paddingHorizontal: spacing.l, paddingBottom: spacing.xl, gap: spacing.xl },
    group: { gap: spacing.s },
    // Sentence case: the brand does not shout its labels.
    groupLabel: { ...typography.label, color: colors.textSecondary },
    optionRow: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.s },
    // A CHOICE, not an action: the selected state is the accent wash and an
    // accent border, never a filled primary button. A row of blue fills would
    // claim that picking a sort order is the dominant action on the screen.
    option: {
      paddingHorizontal: spacing.m + 2,
      paddingVertical: spacing.s,
      borderRadius: radius.pill,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      backgroundColor: colors.surfaceSubtle,
    },
    optionOn: { backgroundColor: colors.accentSubtle, borderColor: colors.accent },
    optionText: { ...typography.label, color: colors.textSecondary },
    optionTextOn: { color: colors.accent },
    dateRow: { flexDirection: 'row', gap: spacing.m },
    dateInput: { flex: 1 },
    numberField: { flex: 1 },
    peopleBtn: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.s + 2,
      minHeight: touch.minSize,
      paddingHorizontal: spacing.m + 2,
      paddingVertical: spacing.m,
      borderRadius: radius.control,
      backgroundColor: colors.surfaceSubtle,
    },
    peopleBtnText: { ...typography.body, flex: 1, color: colors.textPrimary },
    peopleCount: {
      ...typography.badge,
      minWidth: 22,
      textAlign: 'center',
      color: colors.textOnAccent,
      backgroundColor: colors.accentStrong,
      borderRadius: radius.pill,
      paddingVertical: 2,
      overflow: 'hidden',
    },
    footer: {
      paddingHorizontal: spacing.l,
      paddingTop: spacing.l,
      borderTopWidth: StyleSheet.hairlineWidth,
      borderTopColor: colors.separator,
    },
    pressed: { opacity: 0.7 },
  }),
);
