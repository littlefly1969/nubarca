import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  BackHandler,
  FlatList,
  StyleSheet,
  Text,
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
import { useTvFixedGridFocus } from '../lib/useTvFixedGridFocus';
import {
  buildTvGridRows,
  tvGridColumns,
  tvGridTileWidth,
  type TvGridRow,
} from '../lib/tvFixedGrid';
import { MEDIA_GRID_FOCUS_BLEED, MEDIA_GRID_VISUAL_GAP } from '../lib/mediaGridPresentation';
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
// The shelf is the SAME deterministic fixed-column grid as the library, so
// navigation behaves identically in both — one engine, one set of guarantees.

const GRID_GAP = MEDIA_GRID_VISUAL_GAP;
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
  focusTargets,
  onOpen,
}: {
  album: TvPersonalAlbumCard;
  index: number;
  width: number;
  height: number;
  preferred: boolean;
  focusTargets: ReturnType<ReturnType<typeof useTvFixedGridFocus>['targetsFor']>;
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
      index={index}
      hasTVPreferredFocus={preferred}
      focusTargets={focusTargets}
      onSelect={() => onOpen(album)}
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
  const columns = tvGridColumns(contentWidth);
  const tileWidth = tvGridTileWidth(contentWidth, columns, GRID_GAP);
  const tileHeight = Math.round(tileWidth / TILE_ASPECT);

  const list = albums ?? [];
  const rows = useMemo(
    () => buildTvGridRows(list, columns, (album) => album.id),
    [list, columns],
  );
  const gridFocus = useTvFixedGridFocus(list.length, columns);
  const openedRef = useRef(false);

  const open = useCallback((album: TvPersonalAlbumCard) => {
    // Guard against a double SELECT from a fast remote producing two pushes.
    if (openedRef.current) return;
    openedRef.current = true;
    onOpenAlbum({ id: album.id, name: album.name });
  }, [onOpenAlbum]);

  const renderRow = useCallback((
    { item: row }: ListRenderItemInfo<TvGridRow<TvPersonalAlbumCard>>,
  ) => (
    <View style={styles.gridRow}>
      {row.items.map((album, offset) => {
        const index = row.firstIndex + offset;
        return (
          <AlbumTile
            key={album.id}
            album={album}
            index={index}
            width={tileWidth}
            height={tileHeight}
            preferred={index === 0}
            focusTargets={gridFocus.targetsFor(index)}
            onOpen={open}
          />
        );
      })}
    </View>
  ), [tileWidth, tileHeight, gridFocus, open]);

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
          initialNumToRender={8}
          maxToRenderPerBatch={4}
          windowSize={11}
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
    paddingVertical: MEDIA_GRID_FOCUS_BLEED,
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
