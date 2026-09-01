// MediaTile: one grid cell for a photo or a video.
//
// Photos render the small thumbnail. Videos render the poster with a play
// affordance and duration badge; a missing/synthetic poster degrades to an
// explicit "video without preview" placeholder — never a blank rectangle (a
// TV lesson carried over). Selection mode overlays a check indicator.

import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { AuthedImage } from './AuthedImage';
import { radii, spacing, touch } from '../ui/tokens';
import { useI18n } from '../i18n';
import type { MediaItem } from '../api/media';
import { themed } from '../ui/theme.ts';
import { media } from '../ui/palette.ts';

export interface MediaTileProps {
  item: MediaItem;
  size: number;
  selected: boolean;
  selecting: boolean;
  onPress: () => void;
  onLongPress?: () => void;
}

function formatDuration(seconds: number | null): string | null {
  if (seconds === null || seconds <= 0) return null;
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${String(s).padStart(2, '0')}`;
}

export function MediaTile({
  item,
  size,
  selected,
  selecting,
  onPress,
  onLongPress,
}: MediaTileProps): React.JSX.Element {
  const styles = useStyles();
  const { t } = useI18n();
  const isVideo = item.kind === 'video';
  const syntheticPoster =
    isVideo && (item.posterSource === 'synthetic' || item.posterSource === null);
  const duration = isVideo ? formatDuration(item.durationSeconds) : null;

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={
        isVideo
          ? t('albums.open', { name: item.displayName })
          : item.displayName
      }
      onPress={onPress}
      onLongPress={onLongPress}
      style={({ pressed }) => [styles.tile, { width: size, height: size }, pressed && styles.pressed]}
    >
      <AuthedImage
        path={item.thumbnailUrl}
        style={styles.image}
        accessibilityLabel={item.displayName}
      />
      {isVideo && (
        <>
          <View style={styles.playBadge} pointerEvents="none">
            <Text style={styles.playGlyph}>▶</Text>
          </View>
          {duration !== null && (
            <View style={[styles.chip, styles.durationChip]} pointerEvents="none">
              <Text style={styles.durationText}>{duration}</Text>
            </View>
          )}
          {syntheticPoster && (
            <View style={[styles.chip, styles.syntheticChip]} pointerEvents="none">
              <Text style={styles.syntheticText}>{t('grid.syntheticPoster')}</Text>
            </View>
          )}
        </>
      )}
      {item.hasDuplicates && item.occurrenceCount > 1 && (
        <View style={[styles.chip, styles.dupChip]} pointerEvents="none">
          <Text style={styles.dupText}>
            {t('grid.duplicateBadge', { count: item.occurrenceCount })}
          </Text>
        </View>
      )}
      {selecting && (
        <View
          style={[styles.checkRing, selected && styles.checkRingOn]}
          pointerEvents="none"
        >
          {selected && <Text style={styles.checkMark}>✓</Text>}
        </View>
      )}
    </Pressable>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    tile: {
      borderRadius: radii.s,
      overflow: 'hidden',
      backgroundColor: colors.tilePlaceholder,
      justifyContent: 'center',
      alignItems: 'center',
    },
    pressed: { opacity: 0.8 },
    image: { width: '100%', height: '100%' },
    playBadge: {
      position: 'absolute',
      width: 40,
      height: 40,
      borderRadius: 20,
      backgroundColor: media.scrim,
      alignItems: 'center',
      justifyContent: 'center',
    },
    playGlyph: {
      color: media.text,
      fontSize: 16,
      marginLeft: 2,
    },
    chip: {
      position: 'absolute',
      borderRadius: radii.s,
      paddingHorizontal: spacing.s,
      paddingVertical: 2,
    },
    durationChip: {
      bottom: spacing.s,
      right: spacing.s,
      backgroundColor: media.chrome,
    },
    durationText: {
      color: media.text,
      fontSize: 11,
      fontWeight: '600',
    },
    syntheticChip: {
      bottom: spacing.s,
      left: spacing.s,
      backgroundColor: media.scrim,
      maxWidth: '86%',
    },
    syntheticText: {
      color: media.text,
      fontSize: 10,
    },
    dupChip: {
      top: spacing.s,
      left: spacing.s,
      backgroundColor: media.highlight,
    },
    dupText: {
      color: media.text,
      fontSize: 11,
      fontWeight: '600',
    },
    checkRing: {
      position: 'absolute',
      top: spacing.s,
      right: spacing.s,
      width: touch.minSize - 12,
      height: touch.minSize - 12,
      borderRadius: 18,
      borderWidth: 2,
      borderColor: media.text,
      backgroundColor: media.scrimSoft,
      alignItems: 'center',
      justifyContent: 'center',
    },
    checkRingOn: {
      backgroundColor: colors.accent,
      borderColor: colors.accent,
    },
    checkMark: {
      color: media.text,
      fontWeight: '700',
      fontSize: 16,
    },
  }),
);
