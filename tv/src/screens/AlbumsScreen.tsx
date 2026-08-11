import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  StyleSheet,
  Text,
  TVFocusGuideView,
  View,
  useWindowDimensions,
  type ListRenderItemInfo,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { listTvAlbums, type TvAlbum } from '../api/tv';
import { ApiError } from '../api/client';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { MediaTilePreview } from '../components/MediaTilePreview';
import {
  buildTvMediaGridRows,
  tvMediaGridTargetHeight,
  TV_MEDIA_GRID_BATCH_ROWS,
  TV_MEDIA_GRID_FOCUS_BLEED,
  TV_MEDIA_GRID_GAP,
  TV_MEDIA_GRID_INITIAL_ROWS,
  TV_MEDIA_GRID_WINDOW_SIZE,
  type TvMediaGridRow,
} from '../lib/tvMediaGrid';
import { useTvGridFocusMemory } from '../lib/mediaMenuFocus';
import { useI18n } from '../i18n';

const GRID_GAP = TV_MEDIA_GRID_GAP;
const TILE_ASPECT = 16 / 10;

interface Props {
  onOpenAlbum: (album: TvAlbum) => void;
  // Called on a 401 (session revoked/expired) so the app returns to pairing.
  onSessionInvalid: () => void;
}

// Shows only the owner's ShowOnTv-enabled albums. Refreshes periodically so an
// album the owner disables disappears without restarting the app.
export function AlbumsScreen({ onOpenAlbum, onSessionInvalid }: Props) {
  const { t, tn } = useI18n();
  const { width, height } = useWindowDimensions();
  const [albums, setAlbums] = useState<TvAlbum[] | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const load = () => {
      listTvAlbums()
        .then((list) => {
          if (!cancelled) {
            setAlbums(list);
            setError(false);
          }
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          if (err instanceof ApiError && err.status === 401) {
            onSessionInvalid();
            return;
          }
          setError(true);
        });
    };
    load();
    const timer = setInterval(load, 20000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [onSessionInvalid]);

  const list = albums ?? [];
  const contentWidth = Math.max(1, width - 2 * spacing.lg);
  const rows = useMemo(
    () => buildTvMediaGridRows({
      items: list,
      contentWidth,
      targetRowHeight: tvMediaGridTargetHeight(height - 2 * spacing.lg),
      getAspectRatio: () => TILE_ASPECT,
      getId: (album) => album.id,
    }),
    [list, contentWidth, height],
  );
  const { restoreIndex, onTileFocused } = useTvGridFocusMemory(false, list);

  const renderRow = useCallback(({
    item: row,
  }: ListRenderItemInfo<TvMediaGridRow<TvAlbum>>) => {
    return (
      <TVFocusGuideView
        style={styles.gridRow}
        scrollSnapAlign="start"
        trapFocusLeft
        trapFocusRight
      >
        {row.tiles.map((tile) => {
          const { item: album, originalIndex: index, width: tileWidth, height: tileHeight } = tile;
          return (
            <FocusableMediaTile
              key={album.id}
              accessibilityLabel={t('albums.tileAccessibility', {
                name: album.name,
                count: album.itemCount,
              })}
              style={{ width: tileWidth }}
              hasTVPreferredFocus={restoreIndex !== null && index === restoreIndex}
              onSelect={() => onOpenAlbum(album)}
              onFocusChange={(focused) => {
                if (focused) onTileFocused(index, album.id);
              }}
            >
              <MediaTilePreview
                kind="image"
                path={album.coverThumbnailUrl}
                style={{ width: '100%', height: tileHeight, borderRadius: 8 }}
              />
              <View style={styles.caption} pointerEvents="none">
                <Text style={styles.name} numberOfLines={1}>{album.name}</Text>
                <Text style={styles.count}>{tn(album.itemCount, 'albums.itemCount')}</Text>
              </View>
            </FocusableMediaTile>
          );
        })}
      </TVFocusGuideView>
    );
  }, [onOpenAlbum, onTileFocused, restoreIndex, t, tn]);

  if (error) {
    return (
      <View style={styles.centered}>
        <Text style={styles.body}>{t('albums.loadError')}</Text>
      </View>
    );
  }

  if (albums === null) {
    return (
      <View style={styles.centered}>
        <ActivityIndicator size="large" color={colors.accent} />
      </View>
    );
  }

  if (albums.length === 0) {
    return (
      <View style={styles.centered}>
        <Text style={styles.heading}>{t('albums.title')}</Text>
        <Text style={styles.body}>{t('albums.empty')}</Text>
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <Text style={styles.heading}>{t('albums.title')}</Text>
      <FlatList
        data={rows}
        renderItem={renderRow}
        keyExtractor={(row) => row.key}
        contentContainerStyle={styles.grid}
        initialNumToRender={TV_MEDIA_GRID_INITIAL_ROWS}
        maxToRenderPerBatch={TV_MEDIA_GRID_BATCH_ROWS}
        windowSize={TV_MEDIA_GRID_WINDOW_SIZE}
        removeClippedSubviews={false}
        snapToAlignment="item"
        scrollAnimationEnabled={false}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg, padding: spacing.lg },
  centered: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg,
    padding: spacing.xl,
  },
  heading: { color: colors.text, fontSize: font.heading, fontWeight: '700', marginBottom: spacing.md },
  body: { color: colors.muted, fontSize: font.body, marginTop: spacing.md, textAlign: 'center' },
  grid: { gap: GRID_GAP },
  gridRow: {
    flexDirection: 'row',
    gap: GRID_GAP,
    paddingVertical: TV_MEDIA_GRID_FOCUS_BLEED,
    overflow: 'visible',
  },
  caption: {
    position: 'absolute',
    left: 0,
    right: 0,
    bottom: 0,
    paddingHorizontal: spacing.xs,
    paddingVertical: spacing.xs,
    backgroundColor: 'rgba(0,0,0,0.72)',
  },
  name: { color: colors.text, fontSize: font.body, fontWeight: '600' },
  count: { color: colors.muted, fontSize: font.caption },
});
