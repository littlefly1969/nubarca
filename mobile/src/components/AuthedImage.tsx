// Authenticated image rendering.
//
// Renders one NubArca derivative (small thumbnail / medium preview / poster)
// through the authenticated-fetch image loader. The loader returns a data URI
// so <Image> needs NO headers (the RN Android header path is unreliable) and
// benefits from dedup, bounded concurrency, retry and logout generation
// guards — see media/imageLoader.ts.

import React, { useEffect, useState } from 'react';
import {
  Image,
  StyleSheet,
  Text,
  View,
  type DimensionValue,
  type ImageLoadEventData,
  type ImageResizeMode,
  type NativeSyntheticEvent,
} from 'react-native';
import { loadImage } from '../media/imageLoader';
import { themed } from '../ui/theme';

export function AuthedImage({
  path,
  style,
  accessibilityLabel,
  resizeMode,
  onNaturalSize,
}: {
  path: string;
  style: { width?: DimensionValue; height?: DimensionValue; [k: string]: unknown };
  accessibilityLabel?: string;
  resizeMode?: ImageResizeMode;
  // The decoded source dimensions, reported once the bitmap is available.
  // The zooming viewer needs them: under `contain` the pan bounds are the
  // overflow of the ASPECT-FITTED box, which cannot be derived from the
  // viewport alone. Absent for every call site that only displays an image.
  onNaturalSize?: (size: { width: number; height: number }) => void;
}): React.JSX.Element {
  const styles = useStyles();
  const [uri, setUri] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);
    setUri(null);
    loadImage(path).then(
      (next) => {
        if (!cancelled) setUri(next);
      },
      () => {
        if (!cancelled) setFailed(true);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [path]);

  if (failed || uri === null) {
    return (
      <View style={[styles.placeholder, style]}>
        <Text style={styles.placeholderGlyph}>{failed ? '⚠' : ''}</Text>
      </View>
    );
  }
  return (
    <Image
      source={{ uri }}
      style={style as never}
      resizeMode={resizeMode}
      accessibilityLabel={accessibilityLabel}
      accessibilityIgnoresInvertColors
      onLoad={
        onNaturalSize === undefined
          ? undefined
          : (event: NativeSyntheticEvent<ImageLoadEventData>) => {
              const { width, height } = event.nativeEvent.source;
              if (width > 0 && height > 0) onNaturalSize({ width, height });
            }
      }
    />
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    placeholder: {
      backgroundColor: colors.tilePlaceholder,
      alignItems: 'center',
      justifyContent: 'center',
    },
    placeholderGlyph: {
      fontSize: 22,
      color: colors.textTertiary,
    },
  }),
);
