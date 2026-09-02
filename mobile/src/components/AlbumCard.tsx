// AlbumCard: one album grid cell — cover mosaic (up to 4 server-provided
// coverItems), name, and photo/video counts. The mosaic comes straight from
// AlbumSummary.coverItems; the card issues NO per-cover media requests.

import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { AuthedImage } from './AuthedImage';
import { radii, spacing, type } from '../ui/tokens';
import { useI18n } from '../i18n';
import type { AlbumSummary } from '../api/albums';
import { themed } from '../ui/theme';

export function AlbumCard({
  album,
  tile,
  onPress,
  onLongPress,
}: {
  album: AlbumSummary;
  tile: number;
  onPress: () => void;
  onLongPress?: () => void;
}): React.JSX.Element {
  const styles = useStyles();
  const { t } = useI18n();
  const covers = album.coverItems.slice(0, 4);
  const photoLabel = t('albums.photoCount', { count: album.photoCount });
  const videoLabel = t('albums.videoCount', { count: album.videoCount });

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t('albums.open', { name: album.name })}
      onPress={onPress}
      onLongPress={onLongPress}
      style={({ pressed }) => [styles.card, { width: tile }, pressed && styles.pressed]}
    >
      <View style={[styles.mosaic, { width: tile, height: tile }]}>
        {covers.length === 0 ? (
          <View style={styles.mosaicPlaceholder}>
            <Text style={styles.placeholderGlyph}>🖼</Text>
          </View>
        ) : covers.length === 1 ? (
          <AuthedImage path={covers[0].thumbnailUrl} style={styles.cellFull} />
        ) : (
          <View style={styles.grid2x2}>
            {covers.map((cover) => (
              <AuthedImage
                key={cover.fileItemId}
                path={cover.thumbnailUrl}
                style={styles.cell}
              />
            ))}
          </View>
        )}
      </View>
      <Text style={styles.name} numberOfLines={1} ellipsizeMode="tail">
        {album.name}
      </Text>
      <Text style={styles.counts} numberOfLines={1}>
        {album.videoCount > 0
          ? t('albums.itemCounts', { photos: photoLabel, videos: videoLabel })
          : photoLabel}
      </Text>
    </Pressable>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    card: {
      marginBottom: spacing.l,
      marginHorizontal: spacing.xs,
    },
    pressed: { opacity: 0.8 },
    mosaic: {
      borderRadius: radii.l,
      overflow: 'hidden',
      backgroundColor: colors.tilePlaceholder,
    },
    mosaicPlaceholder: {
      flex: 1,
      alignItems: 'center',
      justifyContent: 'center',
    },
    placeholderGlyph: {
      fontSize: 34,
      color: colors.textTertiary,
    },
    cellFull: { width: '100%', height: '100%' },
    grid2x2: {
      flex: 1,
      flexDirection: 'row',
      flexWrap: 'wrap',
    },
    cell: { width: '50%', height: '50%' },
    name: {
      ...type.body,
      color: colors.textPrimary,
      fontWeight: '600',
      marginTop: spacing.s,
    },
    counts: {
      ...type.secondary,
      color: colors.textSecondary,
      marginTop: 2,
    },
  }),
);
