// Albums tab: ONE destination for owned albums, albums shared with the user,
// and pending invitations (accept/decline only).
//
// Loading is parallel across /api/albums, /api/shared-albums and
// /api/shared-albums/invitations. Normalization is PRESENTATION ONLY
// (albumCardModel): the two API families and their authority stay separate.
//
// Invariants carried over from v1: creating/deleting an OWNED album never
// touches files; nothing in this tab deletes media.

import React, { useCallback, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  FlatList,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Redirect, router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { AuthedImage } from '../../src/components/AuthedImage';
import { AlbumCard } from '../../src/components/AlbumCard';
import { NamePromptModal } from '../../src/components/NamePromptModal';
import { useSession } from '../../src/session/SessionProvider';
import { listAlbums, createAlbum, deleteAlbum } from '../../src/api/albums.ts';
import type { AlbumSummary } from '../../src/api/albums.ts';
import {
  listSharedAlbums,
  listAlbumInvitations,
  acceptAlbumInvitation,
  declineAlbumInvitation,
  type AlbumInvitation,
  type AlbumRole,
  type SharedAlbumSummary,
} from '../../src/api/sharedAlbums.ts';
import {
  buildUnifiedCards,
  filterCards,
  type AlbumFilter,
  type UnifiedAlbumCard,
} from '../../src/albums/albumCardModel.ts';
import { albumColumnsForWidth, radii, spacing, touch } from '../../src/ui/tokens';
import { useWindowDimensions } from 'react-native';
import { useI18n } from '../../src/i18n';
import { themed, useColors } from '../../src/ui/theme.ts';

function roleLabel(role: AlbumRole, t: TFn): string {
  if (role === 'viewer') return t('shared.roleViewer');
  if (role === 'contributor') return t('shared.roleContributor');
  return t('shared.roleEditor');
}

type TFn = ReturnType<typeof useI18n>['t'];

export default function Albums(): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const session = useSession();
  const { t } = useI18n();
  const { width } = useWindowDimensions();
  const columns = albumColumnsForWidth(width);
  const tile = Math.floor((width - spacing.l * 2 - spacing.s * (columns - 1)) / columns);

  const [owned, setOwned] = useState<AlbumSummary[] | null>(null);
  const [shared, setShared] = useState<SharedAlbumSummary[] | null>(null);
  const [invitations, setInvitations] = useState<AlbumInvitation[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [creating, setCreating] = useState(false);
  const [filter, setFilter] = useState<AlbumFilter>('all');
  const [busyMembership, setBusyMembership] = useState<string | null>(null);

  // Parallel load of the three independent surfaces. The screen fails fully
  // only when ALL THREE fail; a partial failure keeps whatever answered.
  const load = useCallback(async () => {
    if (session.status !== 'authed') return;
    setFailed(false);
    setRefreshing(true);
    const [ownedRes, sharedRes, invitesRes] = await Promise.allSettled([
      listAlbums(),
      listSharedAlbums(),
      listAlbumInvitations(),
    ]);
    if (ownedRes.status === 'fulfilled') setOwned(ownedRes.value);
    if (sharedRes.status === 'fulfilled') setShared(sharedRes.value);
    if (invitesRes.status === 'fulfilled') setInvitations(invitesRes.value);
    const allFailed =
      ownedRes.status === 'rejected' &&
      sharedRes.status === 'rejected' &&
      invitesRes.status === 'rejected';
    if (allFailed) setFailed(true);
    setRefreshing(false);
  }, [session.status]);

  useFocusEffect(
    useCallback(() => {
      void load();
      return undefined;
    }, [load]),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  function respond(invite: AlbumInvitation, accept: boolean): void {
    if (busyMembership !== null) return;
    setBusyMembership(invite.membershipId);
    void (async () => {
      try {
        if (accept) await acceptAlbumInvitation(invite.membershipId);
        else await declineAlbumInvitation(invite.membershipId);
      } catch {
        Alert.alert(
          invite.albumName,
          t('gallery.loadErrorNetwork', { what: t('shared.inviteAction') }),
        );
      } finally {
        setBusyMembership(null);
        // The REAL server state is reloaded after accept/decline — never a
        // local guess about what the membership list looks like now.
        await load();
      }
    })();
  }

  function confirmDelete(albumId: string, name: string): void {
    Alert.alert(t('albums.deleteConfirmTitle'), t('albums.deleteConfirmBody', { name }), [
      { text: t('albums.cancel'), style: 'cancel' },
      {
        text: t('albums.delete'),
        style: 'destructive',
        onPress: () => {
          void (async () => {
            try {
              await deleteAlbum(albumId);
              await load();
            } catch {
              Alert.alert(name, t('gallery.loadErrorNetwork', { what: t('albums.delete') }));
            }
          })();
        },
      },
    ]);
  }


  const ownedMap = useMemo(() => new Map((owned ?? []).map((a) => [a.id, a])), [owned]);
  const sharedMap = useMemo(
    () => new Map((shared ?? []).map((a) => [a.albumId, a])),
    [shared],
  );
  const cards = useMemo(() => buildUnifiedCards(owned ?? [], shared ?? []), [owned, shared]);
  const visibleCards = useMemo(() => filterCards(cards, filter), [cards, filter]);
  const pendingInvites = invitations ?? [];

  function openCard(card: UnifiedAlbumCard): void {
    if (card.origin === 'owned') router.push(`/album/${card.albumId}`);
    else router.push(`/shared-album/${card.albumId}`);
  }

  return (
    <Screen>
      <AppHeader
        title={t('tabs.albums')}
        actions={
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('albums.create')}
            onPress={() => setCreating(true)}
            style={({ pressed }) => [styles.iconBtn, pressed && styles.pressed]}
            hitSlop={4}
          >
            <Ionicons name="add-circle-outline" size={24} color={colors.accent} />
          </Pressable>
        }
      />

      {/* Filter chips: Tutti / Miei / Condivisi */}
      <View style={styles.filters}>
        {(['all', 'mine', 'shared'] as const).map((f) => (
          <Pressable
            key={f}
            accessibilityRole="button"
            accessibilityState={{ selected: filter === f }}
            onPress={() => setFilter(f)}
            style={({ pressed }) => [
              styles.chip,
              pressed && styles.pressed,
              filter === f && styles.chipOn,
            ]}
          >
            <Text style={[styles.chipText, filter === f && styles.chipTextOn]}>
              {f === 'all'
                ? t('albums.filterAll')
                : f === 'mine'
                  ? t('albums.filterMine')
                  : t('albums.filterShared')}
            </Text>
          </Pressable>
        ))}
      </View>

      {pendingInvites.length > 0 && (
        <View style={styles.invitesBlock}>
          <Text style={styles.invitesTitle}>{t('shared.pendingInvitations')}</Text>
          {pendingInvites.map((invite) => (
            <View key={invite.membershipId} style={styles.inviteRow}>
              <View style={styles.inviteInfo}>
                <Text style={styles.inviteName} numberOfLines={1}>
                  {invite.albumName}
                </Text>
                <Text style={styles.inviteMeta} numberOfLines={1}>
                  {t('shared.sharedBy', { name: invite.ownerDisplayName })} ·{' '}
                  {roleLabel(invite.role, t)} ·{' '}
                  {t('shared.itemsCount', { count: invite.itemCount })}
                </Text>
              </View>
              {busyMembership === invite.membershipId ? (
                <ActivityIndicator color={colors.accent} />
              ) : (
                <View style={styles.inviteActions}>
                  <Pressable
                    accessibilityRole="button"
                    accessibilityLabel={t('shared.inviteAccept')}
                    onPress={() => respond(invite, true)}
                    style={({ pressed }) => [
                      styles.inviteBtn,
                      styles.inviteAccept,
                      pressed && styles.pressed,
                    ]}
                  >
                    <Text style={styles.inviteAcceptText}>{t('shared.inviteAccept')}</Text>
                  </Pressable>
                  <Pressable
                    accessibilityRole="button"
                    accessibilityLabel={t('shared.inviteDecline')}
                    onPress={() => respond(invite, false)}
                    style={({ pressed }) => [styles.inviteBtn, pressed && styles.pressed]}
                  >
                    <Text style={styles.inviteDeclineText}>{t('shared.inviteDecline')}</Text>
                  </Pressable>
                </View>
              )}
            </View>
          ))}
        </View>
      )}

      {failed && (owned === null || shared === null || invitations === null) ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('tabs.albums') })}
          onRetry={() => {
            void load();
          }}
        />
      ) : owned === null && shared === null && !refreshing ? (
        <LoadingState />
      ) : visibleCards.length === 0 ? (
        <EmptyState icon="🖼" title={t('albums.empty')} hint={t('albums.emptyHint')} />
      ) : (
        <FlatList
          data={visibleCards}
          keyExtractor={(c) => c.key}
          numColumns={columns}
          key={columns}
          contentContainerStyle={styles.listContent}
          renderItem={({ item }) =>
            item.origin === 'owned' ? (
              <AlbumCard
                album={ownedMap.get(item.albumId)!}
                tile={tile}
                onPress={() => openCard(item)}
                onLongPress={() => confirmDelete(item.albumId, item.name)}
              />
            ) : (
              (() => {
                const sharedAlbum = sharedMap.get(item.albumId);
                return (
                  <Pressable
                    accessibilityRole="button"
                    accessibilityLabel={`${item.name} — ${t('shared.badgeShared')} ${item.ownerDisplayName}`}
                    onPress={() => openCard(item)}
                    style={({ pressed }) => [styles.card, pressed && styles.pressed]}
                  >
                    <View style={[styles.coverRow, { height: tile * 0.62 }]}>
                      {(sharedAlbum?.coverItems ?? []).slice(0, 3).map((cover) => (
                        <AuthedImage
                          key={cover.fileItemId}
                          path={cover.thumbnailUrl /* SERVER-PROVIDED, album-scoped */}
                          style={styles.coverImg}
                          accessibilityLabel=""
                        />
                      ))}
                      {(sharedAlbum?.coverItems.length ?? 0) === 0 && (
                        <View style={[styles.coverImg, styles.coverEmpty]} />
                      )}
                    </View>
                    <Text style={styles.cardName} numberOfLines={1} ellipsizeMode="middle">
                      {item.name}
                    </Text>
                    <Text style={styles.cardMeta} numberOfLines={1}>
                      {t('shared.badgeShared')} {item.ownerDisplayName} ·{' '}
                      {roleLabel(item.role as AlbumRole, t)} ·{' '}
                      {t('shared.itemsCount', { count: item.itemCount })}
                    </Text>
                  </Pressable>
                );
              })()
            )
          }
          onRefresh={() => {
            void load();
          }}
          refreshing={refreshing}
        />
      )}

      <NamePromptModal
        visible={creating}
        title={t('albums.createTitle')}
        onCancel={() => setCreating(false)}
        onSubmit={async (name) => {
          await createAlbum(name);
          setCreating(false);
          await load();
        }}
      />
    </Screen>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    iconBtn: {
      width: 40,
      height: 40,
      alignItems: 'center',
      justifyContent: 'center',
    },
    pressed: { opacity: 0.7 },
    filters: {
      flexDirection: 'row',
      gap: spacing.s,
      paddingHorizontal: spacing.l,
      paddingBottom: spacing.s,
    },
    chip: {
      borderRadius: radii.m,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      paddingHorizontal: spacing.m,
      minHeight: touch.minSize - 12,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: colors.surface,
    },
    chipOn: { backgroundColor: colors.accent, borderColor: colors.accent },
    chipText: { fontSize: 13, color: colors.textSecondary },
    chipTextOn: { color: colors.textOnAccent, fontWeight: '600' },
    invitesBlock: {
      marginHorizontal: spacing.l,
      marginBottom: spacing.s,
      borderRadius: radii.m,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      backgroundColor: colors.surface,
      paddingVertical: spacing.s,
    },
    invitesTitle: {
      fontSize: 12,
      fontWeight: '600',
      textTransform: 'uppercase',
      letterSpacing: 0.5,
      color: colors.textTertiary,
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.xs,
    },
    inviteRow: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.s,
      gap: spacing.s,
    },
    inviteInfo: { flex: 1 },
    inviteName: { fontSize: 14, color: colors.textPrimary, fontWeight: '600' },
    inviteMeta: { fontSize: 12, color: colors.textSecondary, marginTop: 2 },
    inviteActions: { flexDirection: 'row', gap: spacing.s },
    inviteBtn: {
      minHeight: touch.minSize - 10,
      paddingHorizontal: spacing.m,
      borderRadius: radii.m,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      alignItems: 'center',
      justifyContent: 'center',
    },
    inviteAccept: { backgroundColor: colors.accent, borderColor: colors.accent },
    inviteAcceptText: { color: colors.textOnAccent, fontSize: 13, fontWeight: '600' },
    inviteDeclineText: { color: colors.textSecondary, fontSize: 13 },
    listContent: {
      paddingHorizontal: spacing.l,
      paddingTop: spacing.s,
      paddingBottom: spacing.xl,
    },
    card: {
      flex: 1,
      margin: spacing.xs,
      borderRadius: radii.m,
      backgroundColor: colors.surface,
      overflow: 'hidden',
    },
    coverRow: {
      flexDirection: 'row',
      gap: 1,
      backgroundColor: colors.tilePlaceholder,
    },
    coverImg: { flex: 1, height: '100%' },
    coverEmpty: { backgroundColor: colors.tilePlaceholder },
    cardName: {
      fontSize: 14,
      fontWeight: '600',
      color: colors.textPrimary,
      marginTop: spacing.s,
      paddingHorizontal: spacing.s,
    },
    cardMeta: {
      fontSize: 11,
      color: colors.textSecondary,
      marginBottom: spacing.s,
      paddingHorizontal: spacing.s,
    },
  }),
);
