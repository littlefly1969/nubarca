// MediaTile: one grid cell for a photo or a video.
//
// A MEDIA FRAME, not a card. It has no elevation, no border and no corner
// rounding: at a two-pixel gutter those read as a dashboard of tiles, and this
// is a private library where the photograph is the material and everything else
// is subordinate to it.
//
// Photos render the small thumbnail. Videos render the poster with a play
// affordance and duration badge; a missing/synthetic poster degrades to an
// explicit "video without preview" placeholder — never a blank rectangle (a
// TV lesson carried over).
//
// Selection is a 2 px accent EDGE plus a filled control, never a translucent
// blue wash over the picture: a wash recolours the very thing the user is
// trying to look at. And it is never colour alone — the control carries a tick,
// and the tile announces `selected` to assistive technology.

import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { AuthedImage } from './AuthedImage';
import { iconSizes, radius, spacing, touch, typography } from '../ui/tokens';
import { useI18n } from '../i18n';
import type { MediaItem } from '../api/media';
import { themed, useColors } from '../ui/theme';
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
  const colors = useColors();
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
      accessibilityState={{ selected: selecting ? selected : undefined }}
      onPress={onPress}
      onLongPress={onLongPress}
      style={({ pressed }) => [
        styles.tile,
        { width: size, height: size },
        pressed && styles.pressed,
        selecting && selected && styles.tileSelected,
      ]}
    >
      {/* The TILE is the interactive element and carries the name; the image
          must not announce it a second time. */}
      <AuthedImage path={item.thumbnailUrl} style={styles.image} />
      {isVideo && (
        <>
          <View style={styles.playBadge} pointerEvents="none">
            <Ionicons name="play" size={iconSizes.s} color={media.text} style={styles.playGlyph} />
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
          {selected && (
            <Ionicons name="checkmark" size={iconSizes.s} color={colors.textOnAccent} />
          )}
        </View>
      )}
    </Pressable>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    tile: {
      overflow: 'hidden',
      backgroundColor: colors.tilePlaceholder,
      justifyContent: 'center',
      alignItems: 'center',
    },
    pressed: { opacity: 0.8 },
    // A precise edge INSIDE the frame. Drawn on the tile rather than over the
    // image so it never tints the photograph.
    tileSelected: {
      borderWidth: 2,
      borderColor: colors.accent,
    },
    image: { width: '100%', height: '100%' },
    playBadge: {
      position: 'absolute',
      width: 40,
      height: 40,
      borderRadius: radius.pill,
      backgroundColor: media.scrim,
      alignItems: 'center',
      justifyContent: 'center',
    },
    // The optical centre of a triangle is left of its bounding box.
    playGlyph: { marginLeft: 2 },
    chip: {
      position: 'absolute',
      borderRadius: radius.compact,
      paddingHorizontal: spacing.s,
      paddingVertical: 2,
    },
    durationChip: {
      bottom: spacing.s,
      right: spacing.s,
      backgroundColor: media.chrome,
    },
    durationText: { ...typography.badge, color: media.text },
    syntheticChip: {
      bottom: spacing.s,
      left: spacing.s,
      backgroundColor: media.scrim,
      maxWidth: '86%',
    },
    syntheticText: { ...typography.badge, color: media.text },
    // NEUTRAL. Duplicate detection is arithmetic, not inference: Soft Violet
    // means intelligence, and spending it here would leave the product with no
    // way to say "this came from a model".
    dupChip: {
      top: spacing.s,
      left: spacing.s,
      backgroundColor: media.chrome,
    },
    dupText: { ...typography.badge, color: media.text },
    checkRing: {
      position: 'absolute',
      top: spacing.s,
      right: spacing.s,
      width: touch.minSize - 12,
      height: touch.minSize - 12,
      borderRadius: radius.pill,
      borderWidth: 2,
      borderColor: media.text,
      backgroundColor: media.scrimSoft,
      alignItems: 'center',
      justifyContent: 'center',
    },
    checkRingOn: {
      backgroundColor: colors.accentStrong,
      borderColor: colors.accent,
    },
  }),
);
