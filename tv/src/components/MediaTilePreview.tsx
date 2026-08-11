import {
  ActivityIndicator,
  Image,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { useTvMedia } from '../media/useTvMedia';
import { useI18n } from '../i18n';
import { videoTilePreview } from '../video/videoTilePreview';

// Grid-tile media preview for the unified library.
//
// One aspect-preserving still image per tile. The slideshow may afford a
// cinematic blurred backdrop; a virtualized wall may contain dozens of mounted
// tiles, so duplicating every decoder and blur surface here is needlessly costly
// on a Fire TV. The tile box is fixed by layout and never changes size.
//
// For VIDEOS this component is the only preview path, and it is a STILL IMAGE
// path: no VideoView, no player, no decode of the original. Its fallback ladder
// is `videoTilePreview` (poster → derived still → explicit placeholder), so a
// video whose poster is missing or still being generated shows a deliberate
// "video, no preview" card rather than a blank focusable rectangle — the
// reported defect.

interface Props {
  kind: 'image' | 'video';
  // Primary still. For a photo the small thumbnail; for a video the poster.
  path: string | null;
  // Derived still to try once if the primary fails (never the 6-cell preview
  // sprite — see videoTilePreview).
  fallbackPath?: string | null;
  style?: StyleProp<ViewStyle>;
  personal?: boolean;
}

export function MediaTilePreview({
  kind,
  path,
  fallbackPath,
  style,
  personal = false,
}: Props) {
  const { t } = useI18n();
  const resolved = kind === 'video'
    ? videoTilePreview({ posterUrl: path, stillFallbackUrl: fallbackPath })
    : ({ kind: 'poster', path, fallbackPath: fallbackPath ?? null } as const);

  const primary = resolved.kind === 'placeholder' ? null : resolved.path;
  const secondary = resolved.kind === 'placeholder' ? null : resolved.fallbackPath;
  const { uri, state, markFailed } = useTvMedia(primary, {
    fallbackPath: secondary,
    personal,
  });

  const failed = resolved.kind === 'placeholder' || state === 'failed';

  return (
    <View style={[styles.frame, style]}>
      {uri && state === 'ready' ? (
        <Image
          source={{ uri }}
          style={StyleSheet.absoluteFill}
          resizeMode="contain"
          onError={markFailed}
        />
      ) : failed ? (
        // The explicit placeholder. A video says so with a play glyph, because
        // "no preview available" on a video tile is a normal, temporary state
        // (the poster may still be generating) and must not read as breakage.
        <View style={styles.placeholder}>
          {kind === 'video' && <Text style={styles.playGlyph}>▶</Text>}
          <Text style={styles.text} numberOfLines={2}>
            {kind === 'video' ? t('media.videoNoPreview') : t('media.unavailable')}
          </Text>
        </View>
      ) : (
        <View style={styles.placeholder}>
          <ActivityIndicator color={colors.muted} />
          <Text style={styles.text} numberOfLines={1}>{t('media.loading')}</Text>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  frame: {
    overflow: 'hidden',
    backgroundColor: '#05070b',
  },
  placeholder: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: colors.panel,
    alignItems: 'center',
    justifyContent: 'center',
    padding: spacing.sm,
  },
  playGlyph: {
    color: colors.muted,
    fontSize: 40,
    marginBottom: spacing.xs,
  },
  text: {
    color: colors.muted,
    fontSize: font.caption,
    marginTop: spacing.xs,
    textAlign: 'center',
  },
});
