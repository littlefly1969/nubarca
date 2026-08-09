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
import { videoTilePreview, type TvPreviewPriority } from '../video/videoTilePreview';

// Grid-tile media preview for the unified library.
//
// Cinematic framing, same as the slideshow's SlideImage but single-slot and
// cheap enough for a virtualized grid: a blurred COVER backdrop + dim behind an
// aspect-preserving CONTAIN foreground, both referencing the SAME downloaded
// local file:// URI (one download). The whole photo/poster is always visible and
// never cropped, and the tile box is fixed by the layout so the placeholder
// never changes size.
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
  priority?: TvPreviewPriority;
  // Ambient blur radius. Kept modest for Fire TV.
  blurRadius?: number;
}

export function MediaTilePreview({
  kind,
  path,
  fallbackPath,
  style,
  personal = false,
  priority = 'high',
  blurRadius = 12,
}: Props) {
  const { t } = useI18n();
  const resolved = kind === 'video'
    ? videoTilePreview({ posterUrl: path, stillFallbackUrl: fallbackPath })
    : ({ kind: 'poster', path, fallbackPath: fallbackPath ?? null } as const);

  // 'none' means "do not warm this tile" — it is not a reason to skip loading
  // when the tile is actually mounted and on screen, so it maps to the lowest
  // real priority rather than to no request.
  const loadPriority = priority === 'high' ? 'high' : 'low';
  const primary = resolved.kind === 'placeholder' ? null : resolved.path;
  const secondary = resolved.kind === 'placeholder' ? null : resolved.fallbackPath;
  const { uri, state, markFailed } = useTvMedia(primary, {
    fallbackPath: secondary,
    personal,
    priority: loadPriority,
  });

  const failed = resolved.kind === 'placeholder' || state === 'failed';

  return (
    <View style={[styles.frame, style]}>
      {uri && state === 'ready' ? (
        <>
          {/* Ambient blurred fill (same local file) — decorative, non-focusable. */}
          <Image
            source={{ uri }}
            style={StyleSheet.absoluteFill}
            resizeMode="cover"
            blurRadius={blurRadius}
          />
          <View style={styles.dim} />
          {/* Aspect-preserving foreground: the whole frame, never cropped. */}
          <Image
            source={{ uri }}
            style={StyleSheet.absoluteFill}
            resizeMode="contain"
            onError={markFailed}
          />
        </>
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
  dim: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: 'rgba(0,0,0,0.28)',
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
