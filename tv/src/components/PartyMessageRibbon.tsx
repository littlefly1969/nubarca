import { useEffect, useRef } from 'react';
import { Animated, StyleSheet, Text, useWindowDimensions } from 'react-native';
import { colors, font, overscan, spacing } from '../theme';
import type { TvPartyMessage } from '../api/tv';
import { useI18n } from '../i18n';

// The Elegant Ribbon: a quiet band across the bottom of the party wall holding
// ONE guest message at a time.
//
// Deliberately NOT a ticker. Scrolling text at ten feet is unreadable and
// draws the eye away from the photographs, which are still the point of the
// wall; the 120-character limit exists precisely so a message fits standing
// still. Messages are swapped with a short crossfade, and the band is never
// focusable — the remote's job is unchanged by this feature.
//
// The text is rendered as TEXT. It is whatever a stranger at the party typed,
// and the server has already flattened it to a single normalised line.

const FADE_MS = 320;

export function PartyMessageRibbon({
  message,
}: {
  message: TvPartyMessage | null;
}) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const opacity = useRef(new Animated.Value(0)).current;

  // Crossfade on the message ID, not on every render: re-fading the same
  // message would make the band pulse whenever the parent re-rendered.
  const id = message?.id ?? null;
  useEffect(() => {
    if (id === null) return;
    opacity.setValue(0);
    const animation = Animated.timing(opacity, {
      toValue: 1,
      duration: FADE_MS,
      useNativeDriver: true,
    });
    animation.start();
    return () => animation.stop();
  }, [id, opacity]);

  if (message === null) return null;

  return (
    <Animated.View
      // Inside the overscan safe area on both axes, so nothing is clipped on a
      // 720p panel that eats its edges.
      style={[
        styles.band,
        { left: inset.x, right: inset.x, bottom: inset.y, opacity },
      ]}
      pointerEvents="none"
    >
      <Text style={styles.author} numberOfLines={1}>
        {message.displayName ?? t('partyMessages.anonymous')}
      </Text>
      {/* Two lines at most, then an ellipsis. The font does NOT shrink to fit:
          text too small to read from a sofa is worse than text that is cut. */}
      <Text style={styles.body} numberOfLines={2} ellipsizeMode="tail">
        {message.text}
      </Text>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  band: {
    position: 'absolute',
    // Roughly a tenth of the screen, which is what the layout asks for and what
    // leaves the photograph essentially uncovered.
    minHeight: '10%',
    justifyContent: 'center',
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
    borderRadius: 18,
    // High contrast against ANY photograph behind it, which is why this is a
    // near-opaque dark plate rather than a light scrim.
    backgroundColor: 'rgba(0,0,0,0.82)',
  },
  author: {
    color: colors.accent,
    fontSize: font.body,
    fontWeight: '900',
    marginBottom: spacing.xs / 2,
  },
  body: {
    color: colors.text,
    fontSize: font.heading,
    fontWeight: '700',
    lineHeight: font.heading * 1.25,
  },
});
