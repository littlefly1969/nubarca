import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Image,
  Pressable,
  StyleSheet,
  Text,
  View,
  type ImageStyle,
  type StyleProp,
} from 'react-native';
import { getCachedImage, loadImage } from '../api/imageLoader';

// Authenticated image, rendered via the centralized loader (imageLoader.ts):
// the loader fetches the bytes with the session cookie and returns a base64
// data URI (the explicit primary path on Expo Go Android, where <Image> does
// not forward a Cookie header). This component is a thin, unmount-safe consumer:
//
//   * Seeds synchronously from the cache, so a remount (e.g. after a rotation
//     re-keys the FlatList) shows a cached image immediately — no refetch, no
//     placeholder flash.
//   * loading / ok / error states. An error is ALWAYS retryable (tap to retry);
//     a transient failure never permanently latches a placeholder.
//   * Guards against setState after unmount while a fetch is in flight.
//
// Used only for small thumbnails and medium previews — never originals.
type Phase = 'loading' | 'ok' | 'error';

export default function AuthedImage({
  path,
  style,
  resizeMode = 'cover',
  onLoaded,
}: {
  path: string;
  style: StyleProp<ImageStyle>;
  resizeMode?: 'cover' | 'contain';
  onLoaded?: () => void;
}): React.JSX.Element {
  const cached = getCachedImage(path);
  const [uri, setUri] = useState<string | null>(cached ?? null);
  const [phase, setPhase] = useState<Phase>(cached !== undefined ? 'ok' : 'loading');
  // Bumped by the retry button to re-trigger the load effect.
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const hit = getCachedImage(path);
    if (hit !== undefined) {
      setUri(hit);
      setPhase('ok');
      return;
    }
    setUri(null);
    setPhase('loading');
    loadImage(path)
      .then((dataUri) => {
        if (cancelled) return;
        setUri(dataUri);
        setPhase('ok');
      })
      .catch(() => {
        if (cancelled) return;
        setPhase('error');
      });
    return () => {
      cancelled = true;
    };
  }, [path, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  if (phase === 'error') {
    return (
      <Pressable style={[style, styles.center]} onPress={retry}>
        <Text style={styles.retryGlyph}>⟳</Text>
        <Text style={styles.retryText}>Retry</Text>
      </Pressable>
    );
  }

  if (phase === 'loading' || uri === null) {
    return (
      <View style={[style, styles.center]}>
        <ActivityIndicator size="small" color="#9bb4dd" />
      </View>
    );
  }

  return (
    <Image
      style={style}
      resizeMode={resizeMode}
      source={{ uri }}
      onLoad={() => onLoaded?.()}
      // A data URI that fails to decode is rare; make it retryable too.
      onError={() => setPhase('error')}
    />
  );
}

const styles = StyleSheet.create({
  center: {
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#eef0f3',
  },
  retryGlyph: { fontSize: 22, color: '#1a73e8' },
  retryText: { fontSize: 11, color: '#1a73e8', marginTop: 2 },
});
