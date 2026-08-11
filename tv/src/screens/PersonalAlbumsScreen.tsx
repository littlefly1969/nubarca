import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  FlatList,
  StyleSheet,
  Text,
  TVFocusGuideView,
  View,
  useWindowDimensions,
  type ListRenderItemInfo,
} from 'react-native';
import { colors, font, overscan, spacing } from '../theme';
import { ApiError } from '../api/client';
import { listPersonalAlbums, type TvPersonalAlbumCard } from '../api/personalMedia';
import { FocusableMediaTile } from '../components/FocusableMediaTile';
import { FocusableButton } from '../components/FocusableButton';
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
import type { PersonalAlbumRef } from '../personal/flow';

// The owner's ALBUMS, inside the Personal Area.
//
// Albums used to exist only under Party, whose authorization is a public-facing
// allowlist (`ShowOnTv`) with a different threat model entirely. This surface
// does NOT reuse it: it is gated by the TV session AND the Personal Area unlock
// grant AND owner scoping, re-checked server-side on every request and every
// byte, which is why it shows the owner every album they own — the same set the
// web shows them — rather than the party subset.
//
// CONSUMPTION-FIRST. Browse, open, view. There is deliberately no rename, no
// delete and no metadata editing: a television with a five-button remote is a
// poor album administration console, and adding those operations here would
// mean maintaining a second, weaker editor for the one on the web.
//
// The shelf uses the same proportional native-focus grid as every media wall,
// with a stable 16:10 card ratio because album DTOs have no cover size.

const GRID_GAP = TV_MEDIA_GRID_GAP;
const TILE_ASPECT = 16 / 10;

interface Props {
  onOpenAlbum: (album: PersonalAlbumRef) => void;
  onBack: () => void;
  onGrantInvalid: (reason?: 'pinChanged') => void;
  onSessionInvalid: () => void;
}

const AlbumTile = memo(function AlbumTile({
  album,
  index,
  width,
  height,
  preferred,
  onFocused,
  onOpen,
}: {
  album: TvPersonalAlbumCard;
  index: number;
  width: number;
  height: number;
  preferred: boolean;
  onFocused: (index: number, id: string) => void;
  onOpen: (album: TvPersonalAlbumCard) => void;
}) {
  const { t } = useI18n();
  // One cover image, not a mosaic: four simultaneous downloads per card is what
  // makes an album shelf slow on a Fire Stick, and the card already carries the
  // name and the counts.
  const cover = album.coverImageUrls[0] ?? null;
  return (
    <FocusableMediaTile
      accessibilityLabel={album.name}
      style={{ width }}
      hasTVPreferredFocus={preferred}
      onSelect={() => onOpen(album)}
      onFocusChange={(focused) => {
        if (focused) onFocused(index, album.id);
      }}
    >
      <MediaTilePreview
        kind="image"
        path={cover}
        personal
        style={{ width: '100%', height, borderRadius: 8 }}
      />
      <View style={styles.caption} pointerEvents="none">
        <Text style={styles.captionName} numberOfLines={1}>{album.name}</Text>
        <Text style={styles.captionCount}>
          {t('personalAlbums.itemCount', { count: String(album.itemCount) })}
        </Text>
      </View>
    </FocusableMediaTile>
  );
});

export function PersonalAlbumsScreen({
  onOpenAlbum, onBack, onGrantInvalid, onSessionInvalid,
}: Props) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const [albums, setAlbums] = useState<TvPersonalAlbumCard[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [reloadNonce, setReloadNonce] = useState(0);

  const handleAuthError = useCallback((err: unknown): boolean => {
    if (err instanceof ApiError && err.status === 401) {
      onSessionInvalid();
      return true;
    }
    if (err instanceof ApiError && err.status === 403) {
      const body = err.body as { error?: string } | null;
      onGrantInvalid(body?.error === 'pin_changed' ? 'pinChanged' : undefined);
      return true;
    }
    return false;
  }, [onGrantInvalid, onSessionInvalid]);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    setAlbums(null);
    listPersonalAlbums()
      .then((result) => { if (!cancelled) setAlbums(result); })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (handleAuthError(err)) return;
        setFailed(true);
      });
    return () => { cancelled = true; };
  }, [handleAuthError, reloadNonce]);

  useEffect(() => {
    const onBackPress = () => { onBack(); return true; };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack]);

  const contentWidth = Math.max(1, width - 2 * inset.x);

  const list = albums ?? [];
  const rows = useMemo(
    () => buildTvMediaGridRows({
      items: list,
      contentWidth,
      targetRowHeight: tvMediaGridTargetHeight(height - 2 * inset.y),
      getAspectRatio: () => TILE_ASPECT,
      getId: (album) => album.id,
    }),
    [list, contentWidth, height, inset.y],
  );
  const { restoreIndex, onTileFocused } = useTvGridFocusMemory(false, list);
  const openedRef = useRef(false);

  const open = useCallback((album: TvPersonalAlbumCard) => {
    // Guard against a double SELECT from a fast remote producing two pushes.
    if (openedRef.current) return;
    openedRef.current = true;
    onOpenAlbum({ id: album.id, name: album.name });
  }, [onOpenAlbum]);

  const renderRow = useCallback((
    { item: row }: ListRenderItemInfo<TvMediaGridRow<TvPersonalAlbumCard>>,
  ) => {
    return (
      <TVFocusGuideView
        style={styles.gridRow}
        scrollSnapAlign="start"
        trapFocusLeft
        trapFocusRight
      >
        {row.tiles.map((tile) => {
          const { item: album, originalIndex: index, width, height: tileHeight } = tile;
          return (
            <AlbumTile
              key={album.id}
              album={album}
              index={index}
              width={width}
              height={tileHeight}
              preferred={restoreIndex !== null && index === restoreIndex}
              onFocused={onTileFocused}
              onOpen={open}
            />
          );
        })}
      </TVFocusGuideView>
    );
  }, [onTileFocused, open, restoreIndex]);

  return (
    <View style={[styles.container, { paddingTop: inset.y, paddingHorizontal: inset.x }]}>
      <Text style={styles.title}>{t('personalAlbums.title')}</Text>
      {albums === null && !failed ? (
        <View style={styles.stateBox}>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.body}>{t('gallery.loading')}</Text>
        </View>
      ) : failed ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{t('gallery.loadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => setReloadNonce((n) => n + 1)}
            hasTVPreferredFocus
          />
          <FocusableButton label={t('gallery.backToHome')} onPress={onBack} />
        </View>
      ) : list.length === 0 ? (
        <View style={styles.stateBox}>
          <Text style={styles.body}>{t('personalAlbums.empty')}</Text>
          <FocusableButton
            label={t('gallery.backToHome')}
            onPress={onBack}
            hasTVPreferredFocus
          />
        </View>
      ) : (
        <FlatList
          data={rows}
          renderItem={renderRow}
          keyExtractor={(row) => row.key}
          contentContainerStyle={[styles.grid, { paddingBottom: inset.y }]}
          initialNumToRender={TV_MEDIA_GRID_INITIAL_ROWS}
          maxToRenderPerBatch={TV_MEDIA_GRID_BATCH_ROWS}
          windowSize={TV_MEDIA_GRID_WINDOW_SIZE}
          removeClippedSubviews={false}
          snapToAlignment="item"
          scrollAnimationEnabled={false}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.bg },
  title: {
    color: colors.text,
    fontSize: font.heading,
    fontWeight: '800',
    marginBottom: spacing.sm,
  },
  body: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  stateBox: { marginTop: spacing.xl, alignItems: 'center', gap: spacing.md },
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
  captionName: { color: colors.text, fontSize: font.caption, fontWeight: '700' },
  captionCount: { color: colors.muted, fontSize: 14 },
});
