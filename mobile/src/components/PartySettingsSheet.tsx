// The mobile Party screen (§30-§37).
//
// Everything it knows about the domain comes from @nubarca/contracts: the
// validation ranges (§33, §34), the message transition matrix (§36) and the
// guest URL rule (§32). This file decides only how a phone should present
// them, which is the one thing that is genuinely device-specific.
//
// Three rules it exists to respect:
//
//   * A NUMBER THE USER TYPED IS NOT CLAMPED SILENTLY. Out-of-range fields are
//     marked and the save is refused, because quietly correcting a typed value
//     hides the rule instead of teaching it. Clamping is for steppers.
//   * GUEST MEDIA AND MESSAGES ARE DIFFERENT DOMAINS (§35), so they are two
//     lists with two vocabularies, never one merged queue.
//   * WHICH MESSAGE ACTIONS EXIST IS NOT DECIDED HERE (§36). The shared matrix
//     answers, the server enforces; this only draws the answer.

import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Modal,
  Pressable,
  ScrollView,
  Share,
  StyleSheet,
  Switch,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import type {
  AlbumPartyStatus,
  PartyGameSettings,
  PartyMessage,
  PartyMessageAction,
  PartySlideshowSettings,
  PartyUploadItem,
} from '@nubarca/contracts';
import {
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  PARTY_GAME_RANGES,
  PARTY_SLIDESHOW_RANGES,
  invalidGameFields,
  invalidSlideshowFields,
  gameSettingsFromStatus,
  partyGuestUrl,
  partyMessageActions,
  partySettingsPatch,
  slideshowSettingsFromStatus,
} from '@nubarca/contracts';
import {
  getPartyStatus,
  listPartyMessages,
  listPartyUploads,
  moderatePartyMessage,
  moderatePartyUpload,
  setPartyGameSettings,
  setPartySlideshowSettings,
  updatePartySettings,
} from '../api/party';
import { getBaseUrl } from '../api/client';
import { AuthedImage } from './AuthedImage';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

const MESSAGE_ACTION_LABELS: Record<PartyMessageAction, string> = {
  approve: 'party.approve',
  reject: 'party.reject',
  hide: 'party.hide',
  restore: 'party.restore',
  'promote-hero': 'party.promoteHero',
  'demote-hero': 'party.demoteHero',
};

function NumberRow({
  label,
  hint,
  value,
  invalid,
  onChange,
}: {
  label: string;
  hint: string;
  value: number | null;
  invalid: boolean;
  onChange: (next: number | null) => void;
}): React.JSX.Element {
  const styles = useStyles();
  return (
    <View style={styles.numberRow}>
      <View style={styles.numberText}>
        <Text style={styles.rowLabel}>{label}</Text>
        <Text style={styles.rowHint}>{hint}</Text>
      </View>
      <TextInput
        style={[styles.numberInput, invalid && styles.invalid]}
        keyboardType="number-pad"
        value={value === null ? '' : String(value)}
        onChangeText={(text) => {
          const trimmed = text.trim();
          if (trimmed.length === 0) return onChange(null);
          const parsed = Number(trimmed);
          // A typed value is kept even when out of range, so the field can be
          // MARKED. Clamping here would hide the rule the user just broke.
          onChange(Number.isFinite(parsed) ? parsed : null);
        }}
        accessibilityLabel={label}
      />
    </View>
  );
}

export function PartySettingsSheet({
  albumId,
  visible,
  onClose,
}: {
  albumId: string;
  visible: boolean;
  onClose: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const [status, setStatus] = useState<AlbumPartyStatus | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [uploads, setUploads] = useState<PartyUploadItem[]>([]);
  const [messages, setMessages] = useState<PartyMessage[]>([]);
  const [slideshow, setSlideshow] = useState<PartySlideshowSettings | null>(null);
  const [game, setGame] = useState<PartyGameSettings | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setFailed(false);
    try {
      const next = await getPartyStatus(albumId, signal);
      setStatus(next);
      // Both mappings live in the contract: an unset game field falls back to
      // the SERVER's default, not to a number this screen made up.
      setSlideshow(slideshowSettingsFromStatus(next));
      setGame(gameSettingsFromStatus(next));
      // Moderation queues are only meaningful while a party is running.
      if (next.partyMode) {
        const [uploadList, messageList] = await Promise.all([
          listPartyUploads(albumId, signal),
          listPartyMessages(albumId, signal),
        ]);
        setUploads(uploadList.items);
        setMessages(messageList.items);
      } else {
        setUploads([]);
        setMessages([]);
      }
    } catch {
      if (signal?.aborted !== true) setFailed(true);
    }
  }, [albumId]);

  useEffect(() => {
    if (!visible) return undefined;
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [visible, load]);

  const run = async (action: () => Promise<unknown>): Promise<void> => {
    setBusy(true);
    try {
      await action();
      await load();
    } catch {
      Alert.alert(t('party.saveFailed'));
    } finally {
      setBusy(false);
    }
  };

  // §32: the CANONICAL url — the server's relative path, prefixed with this
  // installation's own origin. No mobile token, no alternative link.
  const guestUrl = partyGuestUrl(getBaseUrl(), status?.partyUrl ?? null);

  const slideshowInvalid = slideshow === null ? [] : invalidSlideshowFields(slideshow);
  const gameInvalid = game === null ? [] : invalidGameFields(game);
  const range = (r: { min: number; max: number }) =>
    t('party.range', { min: String(r.min), max: String(r.max) });

  return (
    <Modal visible={visible} animationType="slide" onRequestClose={onClose}>
      <View style={styles.root}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('party.title')}</Text>
          <Pressable accessibilityRole="button" accessibilityLabel={t('filters.close')} onPress={onClose} hitSlop={8}>
            <Ionicons name="close" size={26} color={colors.textPrimary} />
          </Pressable>
        </View>

        {failed ? (
          <Text style={styles.empty}>{t('party.loadFailed')}</Text>
        ) : status === null || slideshow === null || game === null ? (
          <ActivityIndicator style={styles.loading} color={colors.accent} />
        ) : (
          <ScrollView contentContainerStyle={styles.body}>
            {/* ---- core (§31) ---- */}
            <View style={styles.switchRow}>
              <Text style={styles.rowLabel}>{t('party.mode')}</Text>
              <Switch
                value={status.partyMode}
                disabled={busy}
                onValueChange={(next) => {
                  // Turning it OFF invalidates the guest link, which is the
                  // kind of consequence a switch should not have silently.
                  if (!next) {
                    Alert.alert(t('party.modeOffTitle'), t('party.modeOffBody'), [
                      { text: t('albums.cancel'), style: 'cancel' },
                      {
                        text: t('party.mode'),
                        style: 'destructive',
                        onPress: () => {
                          void run(() => updatePartySettings(
                            albumId, partySettingsPatch(status, { enabled: false })));
                        },
                      },
                    ]);
                    return;
                  }
                  void run(() => updatePartySettings(
                    albumId, partySettingsPatch(status, { enabled: true })));
                }}
              />
            </View>

            {guestUrl !== null && (
              <View style={styles.group}>
                <Text style={styles.groupLabel}>{t('party.link')}</Text>
                <Text style={styles.url} numberOfLines={2}>{guestUrl}</Text>
                {/* §32: the phone's own share sheet, on the CANONICAL url the
                    server minted. No mobile token, no alternative link. */}
                <Pressable
                  accessibilityRole="button"
                  onPress={() => { void Share.share({ message: guestUrl }); }}
                  style={({ pressed }) => [styles.shareBtn, pressed && styles.pressed]}
                >
                  <Ionicons name="share-outline" size={18} color={colors.textOnAccent} />
                  <Text style={styles.shareText}>{t('party.share')}</Text>
                </Pressable>
              </View>
            )}

            {status.partyMode && (
              <>
                <View style={styles.switchRow}>
                  <Text style={styles.rowLabel}>{t('party.uploads')}</Text>
                  <Switch
                    value={status.uploadEnabled}
                    disabled={busy}
                    onValueChange={(next) => {
                      // The patch carries `enabled` from the current status, so
                      // changing a sub-switch cannot turn the party off.
                      void run(() => updatePartySettings(
                        albumId, partySettingsPatch(status, { uploadEnabled: next })));
                    }}
                  />
                </View>
                <View style={styles.switchRow}>
                  <Text style={styles.rowLabel}>{t('party.requireUploadApproval')}</Text>
                  <Switch
                    value={status.requireUploadApproval}
                    disabled={busy}
                    onValueChange={(next) => {
                      void run(() => updatePartySettings(
                        albumId, partySettingsPatch(status, { requireUploadApproval: next })));
                    }}
                  />
                </View>
                <View style={styles.switchRow}>
                  <Text style={styles.rowLabel}>{t('party.requireMessageApproval')}</Text>
                  <Switch
                    value={status.requireMessageApproval}
                    disabled={busy}
                    onValueChange={(next) => {
                      void run(() => updatePartySettings(
                        albumId, partySettingsPatch(status, { requireMessageApproval: next })));
                    }}
                  />
                </View>
              </>
            )}

            {/* ---- slideshow (§33) ---- */}
            <Text style={styles.section}>{t('party.slideshow')}</Text>
            <NumberRow
              label={t('party.photoSlideSeconds')}
              hint={range(PARTY_SLIDESHOW_RANGES.photoSeconds)}
              value={slideshow.photoSlideSeconds}
              invalid={slideshowInvalid.includes('photoSlideSeconds')}
              onChange={(v) => setSlideshow({ ...slideshow, photoSlideSeconds: v ?? 0 })}
            />
            <NumberRow
              label={t('party.maxVideoSlideSeconds')}
              hint={range(PARTY_SLIDESHOW_RANGES.maxVideoSeconds)}
              value={slideshow.maxVideoSlideSeconds}
              invalid={slideshowInvalid.includes('maxVideoSlideSeconds')}
              onChange={(v) => setSlideshow({ ...slideshow, maxVideoSlideSeconds: v ?? 0 })}
            />
            <NumberRow
              label={t('party.maxPhotoUploads')}
              hint={t('party.unlimited')}
              value={slideshow.maxPhotoUploadsPerParticipant}
              invalid={slideshowInvalid.includes('maxPhotoUploadsPerParticipant')}
              onChange={(v) => setSlideshow({ ...slideshow, maxPhotoUploadsPerParticipant: v ?? 0 })}
            />
            <NumberRow
              label={t('party.maxVideoUploads')}
              hint={t('party.unlimited')}
              value={slideshow.maxVideoUploadsPerParticipant}
              invalid={slideshowInvalid.includes('maxVideoUploadsPerParticipant')}
              onChange={(v) => setSlideshow({ ...slideshow, maxVideoUploadsPerParticipant: v ?? 0 })}
            />
            <Pressable
              accessibilityRole="button"
              disabled={busy}
              onPress={() => {
                // Refused, not corrected: the server would reject it too, and
                // the user learns which field is wrong.
                if (slideshowInvalid.length > 0) return Alert.alert(t('party.invalid'));
                void run(() => setPartySlideshowSettings(albumId, slideshow));
              }}
              style={({ pressed }) => [styles.save, pressed && styles.pressed]}
            >
              <Text style={styles.saveText}>{t('party.save')}</Text>
            </Pressable>

            {/* ---- game (§34) ---- */}
            <Text style={styles.section}>{t('party.game')}</Text>
            <View style={styles.switchRow}>
              <Text style={styles.rowLabel}>{t('party.gameEnabled')}</Text>
              <Switch
                value={game.gameEnabled}
                disabled={busy}
                onValueChange={(next) => setGame({ ...game, gameEnabled: next })}
              />
            </View>
            <NumberRow
              label={t('party.minInterval')}
              hint={range(PARTY_GAME_RANGES.intervalSeconds)}
              value={game.minChallengeIntervalSeconds}
              invalid={gameInvalid.includes('minChallengeIntervalSeconds')}
              onChange={(v) => setGame({ ...game, minChallengeIntervalSeconds: v ?? 0 })}
            />
            <NumberRow
              label={t('party.maxInterval')}
              hint={range(PARTY_GAME_RANGES.intervalSeconds)}
              value={game.maxChallengeIntervalSeconds}
              invalid={gameInvalid.includes('maxChallengeIntervalSeconds')}
              onChange={(v) => setGame({ ...game, maxChallengeIntervalSeconds: v ?? 0 })}
            />
            <NumberRow
              label={t('party.votesPerGuest')}
              hint={range(PARTY_GAME_RANGES.votes)}
              value={game.votesPerGuest}
              invalid={gameInvalid.includes('votesPerGuest')}
              onChange={(v) => setGame({ ...game, votesPerGuest: v ?? 0 })}
            />
            <NumberRow
              label={t('party.maxChallenges')}
              hint={range(PARTY_GAME_RANGES.maxPerSession)}
              value={game.maxChallengesPerSession}
              invalid={gameInvalid.includes('maxChallengesPerSession')}
              onChange={(v) => setGame({ ...game, maxChallengesPerSession: v })}
            />
            <Pressable
              accessibilityRole="button"
              disabled={busy}
              onPress={() => {
                if (gameInvalid.length > 0) return Alert.alert(t('party.invalid'));
                void run(() => setPartyGameSettings(albumId, game));
              }}
              style={({ pressed }) => [styles.save, pressed && styles.pressed]}
            >
              <Text style={styles.saveText}>{t('party.save')}</Text>
            </Pressable>

            {/* ---- guest MEDIA moderation (§35) — its own domain ---- */}
            {status.partyMode && (
              <>
                <Text style={styles.section}>{t('party.pendingUploads')}</Text>
                {uploads.length === 0 ? (
                  <Text style={styles.empty}>{t('party.noUploads')}</Text>
                ) : uploads.map((item) => (
                  <View key={item.fileItemId} style={styles.uploadRow}>
                    <AuthedImage
                      path={item.thumbnailUrl}
                      style={styles.uploadThumb}
                      resizeMode="cover"
                      accessibilityLabel=""
                    />
                    <Text style={styles.uploadName} numberOfLines={1}>{item.name}</Text>
                    {item.status === 'pending' && (
                      <>
                        <Pressable
                          accessibilityRole="button"
                          accessibilityLabel={t('party.approve')}
                          disabled={busy}
                          onPress={() => {
                            void run(() => moderatePartyUpload(albumId, item.fileItemId, 'approve'));
                          }}
                          style={styles.iconAction}
                        >
                          <Ionicons name="checkmark" size={20} color={colors.accent} />
                        </Pressable>
                        <Pressable
                          accessibilityRole="button"
                          accessibilityLabel={t('party.reject')}
                          disabled={busy}
                          onPress={() => {
                            void run(() => moderatePartyUpload(albumId, item.fileItemId, 'reject'));
                          }}
                          style={styles.iconAction}
                        >
                          <Ionicons name="close" size={20} color={colors.danger} />
                        </Pressable>
                      </>
                    )}
                  </View>
                ))}

                {/* ---- MESSAGE moderation (§36) — a separate domain ---- */}
                <Text style={styles.section}>{t('party.messages')}</Text>
                {messages.length === 0 ? (
                  <Text style={styles.empty}>{t('party.noMessages')}</Text>
                ) : messages.map((message) => (
                  <View key={message.id} style={styles.messageRow}>
                    <View style={styles.messageHead}>
                      <Text style={styles.messageAuthor}>
                        {message.displayName ?? t('party.anonymous')}
                      </Text>
                      {message.isHero && <Text style={styles.heroTag}>{t('party.hero')}</Text>}
                    </View>
                    {/* PLAIN TEXT, rendered as text. Never through any markup
                        or URI interpretation — a guest wrote it. */}
                    <Text style={styles.messageText}>{message.text}</Text>
                    <View style={styles.messageActions}>
                      {/* Which actions exist comes from the shared matrix, not
                          from conditions written here (§36). */}
                      {partyMessageActions(message).map((action) => (
                        <Pressable
                          key={action}
                          accessibilityRole="button"
                          accessibilityLabel={t(MESSAGE_ACTION_LABELS[action] as never)}
                          disabled={busy}
                          onPress={() => {
                            void run(() => moderatePartyMessage(albumId, message.id, action));
                          }}
                          style={[
                            styles.messageAction,
                            DESTRUCTIVE_PARTY_MESSAGE_ACTIONS.includes(action) && styles.destructive,
                          ]}
                        >
                          <Text
                            style={[
                              styles.messageActionText,
                              DESTRUCTIVE_PARTY_MESSAGE_ACTIONS.includes(action)
                                && styles.destructiveText,
                            ]}
                          >
                            {t(MESSAGE_ACTION_LABELS[action] as never)}
                          </Text>
                        </Pressable>
                      ))}
                    </View>
                  </View>
                ))}
              </>
            )}
          </ScrollView>
        )}
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
    body: { paddingHorizontal: 16, paddingBottom: 32, gap: 12 },
    section: {
      fontSize: 13, color: colors.textTertiary, textTransform: 'uppercase',
      marginTop: 20, marginBottom: 4,
    },
    group: { gap: 8 },
    groupLabel: { fontSize: 13, color: colors.textTertiary, textTransform: 'uppercase' },
    switchRow: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
      paddingVertical: 6,
    },
    rowLabel: { fontSize: 15, color: colors.textPrimary },
    rowHint: { fontSize: 12, color: colors.textTertiary },
    numberRow: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 4 },
    numberText: { flex: 1 },
    numberInput: {
      width: 92, paddingHorizontal: 12, paddingVertical: 8, borderRadius: 10,
      backgroundColor: colors.surfaceMuted, color: colors.textPrimary, fontSize: 15,
      textAlign: 'right',
    },
    invalid: { backgroundColor: colors.dangerSurface, color: colors.danger },
    url: { fontSize: 13, color: colors.textSecondary },
    shareBtn: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8,
      backgroundColor: colors.accent, borderRadius: 12, paddingVertical: 12,
    },
    shareText: { color: colors.textOnAccent, fontSize: 15, fontWeight: '600' },
    save: {
      backgroundColor: colors.accent, borderRadius: 12, paddingVertical: 12,
      alignItems: 'center', marginTop: 8,
    },
    saveText: { color: colors.textOnAccent, fontSize: 15, fontWeight: '600' },
    uploadRow: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 6 },
    uploadThumb: { width: 48, height: 48, borderRadius: 8 },
    uploadName: { flex: 1, fontSize: 14, color: colors.textPrimary },
    iconAction: { width: 36, height: 36, alignItems: 'center', justifyContent: 'center' },
    messageRow: {
      backgroundColor: colors.surfaceMuted, borderRadius: 12, padding: 12, gap: 8, marginBottom: 4,
    },
    messageHead: { flexDirection: 'row', alignItems: 'center', gap: 8 },
    messageAuthor: { fontSize: 13, fontWeight: '600', color: colors.textSecondary },
    heroTag: {
      fontSize: 11, color: colors.textOnAccent, backgroundColor: colors.accent,
      paddingHorizontal: 8, paddingVertical: 2, borderRadius: 8, overflow: 'hidden',
    },
    messageText: { fontSize: 15, color: colors.textPrimary },
    messageActions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
    messageAction: {
      paddingHorizontal: 12, paddingVertical: 6, borderRadius: 14, backgroundColor: colors.accentSubtle,
    },
    messageActionText: { fontSize: 13, color: colors.accent },
    destructive: { backgroundColor: colors.dangerSurface },
    destructiveText: { color: colors.danger },
    empty: { textAlign: 'center', color: colors.textTertiary, paddingVertical: 16 },
    loading: { marginTop: 32 },
    pressed: { opacity: 0.7 },
  }),
);
