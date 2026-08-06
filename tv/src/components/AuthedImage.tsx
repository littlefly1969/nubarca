import {
  ActivityIndicator,
  Image,
  StyleSheet,
  Text,
  View,
  type ImageStyle,
  type StyleProp,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { useTvMedia } from '../media/useTvMedia';
import { useI18n } from '../i18n';

// Renders a DERIVED TV media image (album cover / grid thumbnail). The bytes are
// downloaded to the app-private cache WITH the limited TV session cookie (see
// loadTvMedia) and rendered from a local file:// URI, which decodes
// deterministically on Fire TV. Never loads original full-resolution bytes.
//
// Grid tiles use resizeMode="cover" (fill the tile). The slideshow uses the
// dedicated SlideImage component instead (aspect-preserving + blurred fill).
//
// States are explicit and visible: a centered spinner + "Caricamento…" while
// loading, and a centered "Anteprima non disponibile" on failure. The placeholder
// fills the same box as the image so tiles never collapse.
interface Props {
  path: string | null;
  style?: StyleProp<ImageStyle>;
  // Optional DERIVED fallback (e.g. the item's preview) if the primary thumbnail
  // is unavailable. Still /api/tv/media only — never an original. It is deferred
  // + serialized inside useTvMedia so it cannot slow the grid down.
  fallbackPath?: string | null;
  // Personal Gallery media: downloads also carry the unlock grant header.
  personal?: boolean;
}

export function AuthedImage({ path, style, fallbackPath, personal = false }: Props) {
  const { t } = useI18n();
  const { uri, state, markFailed } = useTvMedia(path, { fallbackPath, personal });

  if (uri && state === 'ready') {
    return (
      <Image
        source={{ uri }}
        style={style}
        resizeMode="cover"
        onError={markFailed}
      />
    );
  }

  return (
    <View style={[styles.placeholder, style as StyleProp<ImageStyle>]}>
      {state === 'loading' ? (
        <>
          <ActivityIndicator color={colors.muted} />
          <Text style={styles.text} numberOfLines={1}>{t('media.loading')}</Text>
        </>
      ) : (
        <Text style={styles.text} numberOfLines={2}>{t('media.unavailable')}</Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  placeholder: {
    backgroundColor: colors.panel,
    alignItems: 'center',
    justifyContent: 'center',
    padding: spacing.sm,
    overflow: 'hidden',
  },
  text: {
    color: colors.muted,
    fontSize: font.caption,
    marginTop: spacing.xs,
    textAlign: 'center',
  },
});
