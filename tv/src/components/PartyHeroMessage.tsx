import { useEffect, useRef } from 'react';
import { Animated, StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { colors, font, overscan, spacing } from '../theme';
import type { TvPartyMessage } from '../api/tv';
import { useI18n } from '../i18n';

// The Hero card: one guest message at full size, between two media in an
// autoplaying party slideshow.
//
// A Hero is the SAME message as the one in the ribbon, shown large because the
// owner or their delegate chose it — never because the guest asked, and never
// as a second copy of the content. It fades in, holds, and fades out; there is
// no marquee, no confetti, and nothing to interact with, because a party wall
// has no one sitting in front of it with a remote.

const FADE_MS = 420;

export function PartyHeroMessage({
  message,
}: {
  message: TvPartyMessage | null;
}) {
  const { t } = useI18n();
  const { width, height } = useWindowDimensions();
  const inset = overscan(width, height);
  const opacity = useRef(new Animated.Value(0)).current;

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
    <Animated.View style={[styles.scrim, { opacity }]} pointerEvents="none">
      <View style={[styles.card, { marginHorizontal: inset.x * 2, marginVertical: inset.y }]}>
        {/* A neutral graphic mark rather than an avatar: the guest is anonymous
            and inventing a face for them would be a fiction. */}
        <Text style={styles.mark}>✉</Text>
        <Text style={styles.body} numberOfLines={4} ellipsizeMode="tail">
          {message.text}
        </Text>
        <Text style={styles.author} numberOfLines={1}>
          {message.displayName ?? t('partyMessages.anonymous')}
        </Text>
      </View>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  scrim: {
    position: 'absolute',
    top: 0, left: 0, right: 0, bottom: 0,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(4,6,10,0.92)',
  },
  card: {
    alignItems: 'center',
    justifyContent: 'center',
    gap: spacing.lg,
    paddingHorizontal: spacing.xl,
    paddingVertical: spacing.xl,
    borderRadius: 28,
    backgroundColor: colors.panel,
    maxWidth: '86%',
  },
  mark: {
    color: colors.accent,
    fontSize: font.title,
  },
  body: {
    color: colors.text,
    // Deliberately the largest type in the app: a Hero exists to be read from
    // the far side of a room.
    fontSize: font.title + 16,
    fontWeight: '800',
    lineHeight: (font.title + 16) * 1.22,
    textAlign: 'center',
  },
  author: {
    color: colors.accent,
    fontSize: font.heading,
    fontWeight: '900',
    textAlign: 'center',
  },
});
