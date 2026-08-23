// Albums tab: server-provided cover mosaic, counts, create, delete.
// Deleting an album NEVER touches the underlying media — the confirm dialog
// says so explicitly.
import React, { useCallback, useState } from 'react';
import { Alert, FlatList, Pressable, StyleSheet } from 'react-native';
import { Redirect, router, useFocusEffect } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { Screen, AppHeader } from '../../src/ui/components';
import { EmptyState, ErrorState, LoadingState } from '../../src/ui/states';
import { AlbumCard } from '../../src/components/AlbumCard';
import { NamePromptModal } from '../../src/components/NamePromptModal';
import { useSession } from '../../src/session/SessionProvider';
import { listAlbums, createAlbum, deleteAlbum } from '../../src/api/albums.ts';
import type { AlbumSummary } from '../../src/api/albums.ts';
import { albumColumnsForWidth, colors, spacing } from '../../src/ui/tokens';
import { useWindowDimensions } from 'react-native';
import { useI18n } from '../../src/i18n';

export default function Albums(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const { width } = useWindowDimensions();
  const columns = albumColumnsForWidth(width);
  const tile = Math.floor((width - spacing.l * 2 - spacing.s * (columns - 1)) / columns);

  const [albums, setAlbums] = useState<AlbumSummary[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    if (session.status !== 'authed') return;
    setFailed(false);
    try {
      setAlbums(await listAlbums());
    } catch {
      setFailed(true);
    }
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

  function confirmDelete(album: AlbumSummary): void {
    Alert.alert(
      t('albums.deleteConfirmTitle'),
      t('albums.deleteConfirmBody', { name: album.name }),
      [
        { text: t('albums.cancel'), style: 'cancel' },
        {
          text: t('albums.delete'),
          style: 'destructive',
          onPress: () => {
            void (async () => {
              try {
                await deleteAlbum(album.id);
                await load();
              } catch {
                Alert.alert(
                  album.name,
                  t('gallery.loadErrorNetwork', { what: t('albums.delete') }),
                );
              }
            })();
          },
        },
      ],
    );
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

      {albums === null && !failed ? (
        <LoadingState />
      ) : failed ? (
        <ErrorState
          title={t('grid.errorTitle')}
          message={t('gallery.loadErrorNetwork', { what: t('tabs.albums') })}
          onRetry={() => {
            void load();
          }}
        />
      ) : albums !== null && albums.length === 0 ? (
        <EmptyState
          icon="🖼"
          title={t('albums.empty')}
          hint={t('albums.emptyHint')}
        />
      ) : (
        <FlatList
          data={albums}
          keyExtractor={(a) => a.id}
          numColumns={columns}
          key={columns}
          contentContainerStyle={styles.listContent}
          renderItem={({ item }) => (
            <AlbumCard
              album={item}
              tile={tile}
              onPress={() => router.push(`/album/${item.id}`)}
              onLongPress={() => confirmDelete(item)}
            />
          )}
          onRefresh={() => {
            void load();
          }}
          refreshing={false}
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

const styles = StyleSheet.create({
  iconBtn: {
    width: 40,
    height: 40,
    alignItems: 'center',
    justifyContent: 'center',
  },
  pressed: { opacity: 0.7 },
  listContent: {
    paddingHorizontal: spacing.l,
    paddingTop: spacing.m,
  },
});
