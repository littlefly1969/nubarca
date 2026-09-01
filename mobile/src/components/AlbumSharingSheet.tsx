// The owner's album-sharing screen (§25, §26).
//
// PRIVACY IS THE POINT OF THIS SCREEN, and most of it is carried by the shared
// types rather than by care taken here:
//
//   * a member has no `email` field at all — only a MASKED address, so the
//     owner can tell two people with the same display name apart without ever
//     being shown somebody's real address;
//   * there is no user id anywhere, and a row is addressed by `membershipId`;
//   * inviting requires an EXACT address. There is no directory and no
//     autocomplete: a lookup that accepted prefixes would let anyone enumerate
//     accounts. The server resolves the address to a display name, the owner
//     confirms that name, and only then is an invitation created.
//
// Revoked and declined memberships are shown as HISTORY (§25) rather than
// hidden: an owner needs to see that somebody declined, and needs to be able
// to tell a pending invitation from a dead one.
//
// The message-moderation switch is the narrow delegation of §37 — not a role,
// not party administration — and it is only meaningful while a membership is
// accepted, which is why it appears on active rows only.

import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import type { AlbumMember, AlbumRole } from '@nubarca/contracts';
import { isActiveMembership, isHistoricalMembership } from '@nubarca/contracts';
import {
  inviteAlbumMember,
  listAlbumMembers,
  resolveAlbumRecipient,
  revokeAlbumMember,
  setAlbumMemberDownload,
  setAlbumMemberPartyMessages,
  setAlbumMemberRole,
} from '../api/sharedAlbums';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

const ROLE_LABELS: Record<AlbumRole, string> = {
  viewer: 'sharing.roleViewer',
  contributor: 'sharing.roleContributor',
  editor: 'sharing.roleEditor',
};

const STATE_LABELS = {
  pending: 'sharing.statePending',
  accepted: 'sharing.stateAccepted',
  declined: 'sharing.stateDeclined',
  revoked: 'sharing.stateRevoked',
} as const;

export function AlbumSharingSheet({
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
  const [members, setMembers] = useState<AlbumMember[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [email, setEmail] = useState('');
  const [inviteRole, setInviteRole] = useState<AlbumRole>('viewer');
  const [inviteDownload, setInviteDownload] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setFailed(false);
    try {
      setMembers(await listAlbumMembers(albumId, signal));
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
      Alert.alert(t('sharing.actionFailed'));
    } finally {
      setBusy(false);
    }
  };

  // TWO STEPS, on purpose (§26). The address is resolved to a display name and
  // the owner confirms THAT — so an invitation cannot be sent to a mistyped
  // address, and a typo never silently reaches a stranger.
  const startInvite = async (): Promise<void> => {
    const address = email.trim();
    if (address.length === 0) return;
    setBusy(true);
    try {
      const recipient = await resolveAlbumRecipient(albumId, address);
      Alert.alert(
        t('sharing.resolved', { name: recipient.displayName }),
        undefined,
        [
          { text: t('albums.cancel'), style: 'cancel' },
          {
            text: t('sharing.confirmInvite'),
            onPress: () => {
              void run(async () => {
                await inviteAlbumMember(albumId, address, inviteRole, inviteDownload);
                setEmail('');
              });
            },
          },
        ],
      );
    } catch {
      // The server answers the same way for "no such account" as for anything
      // else it will not disclose. This message must not try to tell them
      // apart, or it becomes the oracle the two-step flow exists to prevent.
      Alert.alert(t('sharing.resolveFailed'));
    } finally {
      setBusy(false);
    }
  };

  const active = (members ?? []).filter((m) => isActiveMembership(m.state));
  const pending = (members ?? []).filter((m) => m.state === 'pending');
  const history = (members ?? []).filter((m) => isHistoricalMembership(m.state));

  const memberRow = (member: AlbumMember, showControls: boolean): React.JSX.Element => (
    <View key={member.membershipId} style={styles.member}>
      <View style={styles.memberHead}>
        <View style={styles.memberWho}>
          <Text style={styles.memberName}>{member.displayName}</Text>
          {/* MASKED, never the real address: it exists so two people with the
              same display name can be told apart, not to reveal anybody. */}
          {member.maskedEmail.length > 0 && (
            <Text style={styles.memberEmail}>{member.maskedEmail}</Text>
          )}
        </View>
        <Text style={styles.state}>{t(STATE_LABELS[member.state] as never)}</Text>
      </View>

      {showControls && (
        <>
          <View style={styles.roleRow}>
            {(['viewer', 'contributor', 'editor'] as AlbumRole[]).map((role) => (
              <Pressable
                key={role}
                accessibilityRole="radio"
                accessibilityState={{ selected: member.role === role }}
                disabled={busy}
                onPress={() => {
                  void run(() => setAlbumMemberRole(albumId, member.membershipId, role));
                }}
                style={[styles.roleChip, member.role === role && styles.roleChipOn]}
              >
                <Text style={[styles.roleText, member.role === role && styles.roleTextOn]}>
                  {t(ROLE_LABELS[role] as never)}
                </Text>
              </Pressable>
            ))}
          </View>

          <View style={styles.switchRow}>
            <Text style={styles.switchLabel}>{t('sharing.allowDownload')}</Text>
            <Switch
              value={member.allowOriginalDownload}
              disabled={busy}
              onValueChange={(next) => {
                void run(() => setAlbumMemberDownload(albumId, member.membershipId, next));
              }}
            />
          </View>

          {/* §37: a narrow delegation over this album's party MESSAGES. Not a
              role and not party governance — and only meaningful while the
              membership is accepted, so it is absent on a pending invitation. */}
          {isActiveMembership(member.state) && (
            <View style={styles.switchRow}>
              <Text style={styles.switchLabel}>{t('sharing.canManageMessages')}</Text>
              <Switch
                value={member.canManagePartyMessages}
                disabled={busy}
                onValueChange={(next) => {
                  void run(() =>
                    setAlbumMemberPartyMessages(albumId, member.membershipId, next));
                }}
              />
            </View>
          )}

          <Pressable
            accessibilityRole="button"
            disabled={busy}
            onPress={() => {
              Alert.alert(
                t('sharing.revokeTitle'),
                t('sharing.revokeBody', { name: member.displayName }),
                [
                  { text: t('albums.cancel'), style: 'cancel' },
                  {
                    text: t('sharing.revoke'),
                    style: 'destructive',
                    onPress: () => {
                      void run(() => revokeAlbumMember(albumId, member.membershipId));
                    },
                  },
                ],
              );
            }}
            style={({ pressed }) => [styles.revoke, pressed && styles.pressed]}
          >
            <Text style={styles.revokeText}>
              {member.state === 'pending' ? t('sharing.cancelInvite') : t('sharing.revoke')}
            </Text>
          </Pressable>
        </>
      )}
    </View>
  );

  return (
    <Modal visible={visible} animationType="slide" onRequestClose={onClose}>
      <View style={styles.root}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('sharing.title')}</Text>
          <Pressable accessibilityRole="button" accessibilityLabel={t('filters.close')} onPress={onClose} hitSlop={8}>
            <Ionicons name="close" size={26} color={colors.textPrimary} />
          </Pressable>
        </View>

        {failed ? (
          <Text style={styles.empty}>{t('sharing.loadFailed')}</Text>
        ) : members === null ? (
          <ActivityIndicator style={styles.loading} color={colors.accent} />
        ) : (
          <ScrollView contentContainerStyle={styles.body}>
            <View style={styles.group}>
              <Text style={styles.groupLabel}>{t('sharing.invite')}</Text>
              <TextInput
                style={styles.input}
                value={email}
                onChangeText={setEmail}
                autoCapitalize="none"
                autoCorrect={false}
                keyboardType="email-address"
                placeholderTextColor={colors.textTertiary}
                placeholder={t('sharing.email')}
                accessibilityLabel={t('sharing.email')}
              />
              <Text style={styles.hint}>{t('sharing.emailHint')}</Text>
              <View style={styles.roleRow}>
                {(['viewer', 'contributor', 'editor'] as AlbumRole[]).map((role) => (
                  <Pressable
                    key={role}
                    accessibilityRole="radio"
                    accessibilityState={{ selected: inviteRole === role }}
                    onPress={() => setInviteRole(role)}
                    style={[styles.roleChip, inviteRole === role && styles.roleChipOn]}
                  >
                    <Text style={[styles.roleText, inviteRole === role && styles.roleTextOn]}>
                      {t(ROLE_LABELS[role] as never)}
                    </Text>
                  </Pressable>
                ))}
              </View>
              <View style={styles.switchRow}>
                <Text style={styles.switchLabel}>{t('sharing.allowDownload')}</Text>
                <Switch value={inviteDownload} onValueChange={setInviteDownload} />
              </View>
              <Pressable
                accessibilityRole="button"
                disabled={busy || email.trim().length === 0}
                onPress={() => { void startInvite(); }}
                style={({ pressed }) => [
                  styles.primary,
                  (busy || email.trim().length === 0) && styles.disabled,
                  pressed && styles.pressed,
                ]}
              >
                <Text style={styles.primaryText}>{t('sharing.resolve')}</Text>
              </Pressable>
            </View>

            {active.length === 0 && pending.length === 0 && history.length === 0 && (
              <Text style={styles.empty}>{t('sharing.empty')}</Text>
            )}

            {active.length > 0 && (
              <>
                <Text style={styles.section}>{t('sharing.members')}</Text>
                {active.map((m) => memberRow(m, true))}
              </>
            )}
            {pending.length > 0 && (
              <>
                <Text style={styles.section}>{t('sharing.pending')}</Text>
                {pending.map((m) => memberRow(m, true))}
              </>
            )}
            {/* History is SHOWN, not hidden: an owner needs to see that
                somebody declined, and to tell a live invitation from a dead
                one. It carries no controls — there is nothing to change. */}
            {history.length > 0 && (
              <>
                <Text style={styles.section}>{t('sharing.history')}</Text>
                {history.map((m) => memberRow(m, false))}
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
    group: { gap: 8 },
    groupLabel: { fontSize: 13, color: colors.textTertiary, textTransform: 'uppercase' },
    section: {
      fontSize: 13, color: colors.textTertiary, textTransform: 'uppercase', marginTop: 18,
    },
    input: {
      paddingHorizontal: 12, paddingVertical: 10, borderRadius: 10,
      backgroundColor: colors.surfaceMuted, color: colors.textPrimary, fontSize: 15,
    },
    hint: { fontSize: 12, color: colors.textTertiary },
    roleRow: { flexDirection: 'row', gap: 8, flexWrap: 'wrap' },
    roleChip: { paddingHorizontal: 12, paddingVertical: 7, borderRadius: 14, backgroundColor: colors.surfaceMuted },
    roleChipOn: { backgroundColor: colors.accentStrong },
    roleText: { fontSize: 13, color: colors.textSecondary },
    roleTextOn: { color: colors.textOnAccent },
    switchRow: {
      flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingVertical: 4,
    },
    switchLabel: { fontSize: 14, color: colors.textPrimary },
    primary: {
      backgroundColor: colors.accentStrong, borderRadius: 12, paddingVertical: 12, alignItems: 'center',
    },
    primaryText: { color: colors.textOnAccent, fontSize: 15, fontWeight: '600' },
    disabled: { opacity: 0.5 },
    member: {
      backgroundColor: colors.surfaceMuted, borderRadius: 12, padding: 12, gap: 10, marginBottom: 8,
    },
    memberHead: { flexDirection: 'row', alignItems: 'flex-start', justifyContent: 'space-between' },
    memberWho: { flex: 1 },
    memberName: { fontSize: 15, fontWeight: '600', color: colors.textPrimary },
    memberEmail: { fontSize: 12, color: colors.textTertiary },
    state: { fontSize: 12, color: colors.textTertiary },
    revoke: { paddingVertical: 8, alignItems: 'center' },
    revokeText: { color: colors.danger, fontSize: 14 },
    empty: { textAlign: 'center', color: colors.textTertiary, paddingVertical: 24 },
    loading: { marginTop: 32 },
    pressed: { opacity: 0.7 },
  }),
);
