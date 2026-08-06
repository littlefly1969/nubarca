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

// Grid-tile media preview with the SAME cinematic framing as the slideshow's
// SlideImage, but single-slot (no cross-fade) and cheap enough for a virtualized
// grid: a blurred COVER backdrop + dim behind an aspect-preserving CONTAIN
// foreground. The whole photo/poster is always visible (never cropped) and the
// tile is filled without hard letterbox bands. Both layers reference the SAME
// downloaded local file:// URI — one download. The tile's box (width/height)
// is fixed by the justified layout, so the placeholder never changes size.
interface Props {
  path: string | null;
  fallbackPath?: string | null;
  style?: StyleProp<ViewStyle>;
  personal?: boolean;
  // Ambient blur radius. Kept modest for Fire TV; the caller may lower it under
  // a measured performance regression, but not to zero (the viewer keeps it).
  blurRadius?: number;
}

export function AuthedTilePreview({
  path,
  fallbackPath,
  style,
  personal = false,
  blurRadius = 12,
}: Props) {
  const { t } = useI18n();
  const { uri, state, markFailed } = useTvMedia(path, { fallbackPath, personal });

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
      ) : (
        <View style={styles.placeholder}>
          {state === 'loading' ? (
            <>
              <ActivityIndicator color={colors.muted} />
              <Text style={styles.text} numberOfLines={1}>{t('media.loading')}</Text>
            </>
          ) : (
            <Text style={styles.text} numberOfLines={2}>{t('media.unavailable')}</Text>
          )}
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
  text: {
    color: colors.muted,
    fontSize: font.caption,
    marginTop: spacing.xs,
    textAlign: 'center',
  },
});
