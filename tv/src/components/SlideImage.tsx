import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Animated,
  Image,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { useTvMedia } from '../media/useTvMedia';
import { useI18n } from '../i18n';

// Cinematic slideshow image. Renders the DERIVED preview (never an original,
// never an upscaled thumbnail) in two layers so aspect ratio is always preserved:
//
//  1. Background — the same preview, resizeMode="cover" + blurred + dimmed, as an
//     ambient fill so the frame always feels full (fills the side bars behind a
//     vertical photo, or the letterbox behind a wide photo).
//  2. Foreground — the same preview, resizeMode="contain", so the whole photo is
//     visible with no crop and no distortion, at its natural preview resolution
//     (we never force a width/height that would stretch or pixelate it).
//
// Both layers reference the SAME downloaded local file:// URI — one download.
//
// SMOOTH TRANSITIONS (two-slot stage): the currently `shown` slide stays fully
// visible while the `incoming` slide decodes offscreen; the swap happens the
// moment the incoming FOREGROUND has decoded. So photo changes never flash
// "Caricamento…" over an already-visible photo — the loading state appears only
// before the FIRST image (or after a failure, when there is nothing usable on
// screen). The slides are KEYED by uri, so on promotion React moves the already-
// decoded element between slots instead of remounting it (no re-decode).
//
// The foreground is never blocked by the blurred background: within a slide the
// background starts transparent and FADES IN when its (slower) blur decode
// completes — the blurred side-fill effect is preserved, it just arrives a beat
// after the photo instead of holding it hostage.

// One decoded slide (background + dim + foreground from one local file).
function SlideLayers({
  uri,
  visible,
  onFgReady,
  onFgError,
}: {
  uri: string;
  visible: boolean;
  onFgReady?: () => void;
  onFgError: () => void;
}) {
  const bgOpacity = useRef(new Animated.Value(0)).current;
  const fadeInBg = useCallback(() => {
    Animated.timing(bgOpacity, { toValue: 1, duration: 250, useNativeDriver: true }).start();
  }, [bgOpacity]);
  return (
    <View style={[StyleSheet.absoluteFill, !visible && styles.hidden]}>
      {/* Ambient blurred fill (same image, same local file). Fades in on its own
          decode; a background failure is non-fatal (the stage is already dark —
          the foreground drives the failed state). */}
      <Animated.Image
        source={{ uri }}
        style={[StyleSheet.absoluteFill, { opacity: bgOpacity }]}
        resizeMode="cover"
        blurRadius={28}
        onLoad={fadeInBg}
      />
      <View style={styles.dim} />
      {/* Aspect-preserving foreground. */}
      <Image
        source={{ uri }}
        style={StyleSheet.absoluteFill}
        resizeMode="contain"
        onLoad={onFgReady}
        onError={onFgError}
      />
    </View>
  );
}

interface Props {
  path: string | null;
  // Personal Gallery media: downloads also carry the unlock grant header.
  personal?: boolean;
}

export function SlideImage({ path, personal = false }: Props) {
  const { t } = useI18n();
  const { uri, state, markFailed } = useTvMedia(path, { personal });
  // Two-slot stage: `shown` is on screen, `incoming` decodes offscreen.
  const [shown, setShown] = useState<string | null>(null);
  const [incoming, setIncoming] = useState<string | null>(null);

  useEffect(() => {
    if (state === 'failed') {
      // Failure replaces the stage with the explicit placeholder; also forget
      // the previous slide so navigating onward doesn't flash a stale photo.
      setShown(null);
      setIncoming(null);
      return;
    }
    if (!uri || state !== 'ready') return;
    if (uri === shown) {
      // Navigated back to the slide that is still on screen: nothing to decode.
      setIncoming(null);
      return;
    }
    setIncoming(uri);
  }, [uri, state, shown]);

  // The incoming foreground finished decoding → swap it in front.
  const promote = useCallback((u: string) => {
    setShown(u);
    setIncoming((cur) => (cur === u ? null : cur));
  }, []);

  const failed = state === 'failed';
  // Loading is visible ONLY while nothing is on screen yet (first slide still
  // downloading or decoding offscreen, or right after a failure cleared the
  // stage) — never over an already-visible photo.
  const loadingFirst = !failed && shown === null;
  return (
    <View style={styles.stage}>
      {!failed && shown !== null && (
        <SlideLayers key={shown} uri={shown} visible onFgError={markFailed} />
      )}
      {!failed && incoming !== null && incoming !== shown && (
        <SlideLayers
          key={incoming}
          uri={incoming}
          visible={false}
          onFgReady={() => promote(incoming)}
          onFgError={markFailed}
        />
      )}
      {failed ? (
        <View style={[StyleSheet.absoluteFill, styles.centered]}>
          <Text style={styles.text}>{t('media.unavailable')}</Text>
        </View>
      ) : loadingFirst ? (
        <View style={[StyleSheet.absoluteFill, styles.centered]}>
          <ActivityIndicator size="large" color={colors.muted} />
          <Text style={styles.text}>{t('media.loading')}</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  stage: { flex: 1, width: '100%', backgroundColor: '#05070b', overflow: 'hidden' },
  centered: { alignItems: 'center', justifyContent: 'center' },
  hidden: { opacity: 0 },
  dim: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: 'rgba(0,0,0,0.45)',
  },
  text: {
    color: colors.text,
    fontSize: font.body,
    marginTop: spacing.md,
    textAlign: 'center',
  },
});
