// The branded boot state (BRAND-BOOT-01).
//
// It exists because a cold launch used to be three unrelated screens: a white
// system splash, then a bare spinner on the app canvas, then the UI. The middle
// one said nothing about what was starting.
//
// This continues the NATIVE splash exactly — same Midnight Navy, same identity
// — so the handover is invisible, and then it owns the part that can actually
// take time: restoring the session. The native splash must never wait for that.
//
// It is the one ordinary mobile surface deliberately allowed to stay brand-dark
// before a saved Light preference reaches the main UI. Guessing the preference
// natively is not possible: it lives in secure storage, read after the process
// is already running.

import React, { useEffect, useState } from 'react';
import { AccessibilityInfo, ActivityIndicator, Image, StyleSheet, Text, View } from 'react-native';
import { identity } from './palette.ts';
import { spacing, typography } from './tokens.ts';
import { useI18n } from '../i18n';

// The approved on-dark wordmark, 480×135, shown at the contract's 180–220 px
// class. Byte-identical to the brand package; nothing here recolours or crops.
const WORDMARK = require('../../assets/brand/nubarca-wordmark-on-dark-480w.png');
const WORDMARK_WIDTH = 200;
const WORDMARK_HEIGHT = Math.round((WORDMARK_WIDTH * 135) / 480);

export function BrandBootState(): React.JSX.Element {
  const { t } = useI18n();
  const [reduceMotion, setReduceMotion] = useState(false);

  // Reduced motion removes the ANIMATION, never the information: the status
  // line below stays either way, so somebody who has asked the system to stop
  // moving things still knows the app is working rather than stuck.
  useEffect(() => {
    let cancelled = false;
    void AccessibilityInfo.isReduceMotionEnabled().then(
      (enabled) => {
        if (!cancelled) setReduceMotion(enabled);
      },
      () => {
        /* an unavailable preference is not a reason to animate against it */
      },
    );
    const sub = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduceMotion);
    return () => {
      cancelled = true;
      sub.remove();
    };
  }, []);

  return (
    <View style={styles.root} accessibilityRole="progressbar" accessibilityLabel={t('app.restoring')}>
      <Image
        source={WORDMARK}
        style={styles.wordmark}
        resizeMode="contain"
        accessible={false}
      />
      {!reduceMotion && (
        <ActivityIndicator
          size="small"
          color={identity.bootActivity}
          style={styles.activity}
        />
      )}
      <Text style={styles.status} accessible={false}>
        {t('app.restoring')}
      </Text>
    </View>
  );
}

// No card, no border, no header: the identity is the whole surface.
const styles = StyleSheet.create({
  root: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: identity.bootBackground,
  },
  wordmark: { width: WORDMARK_WIDTH, height: WORDMARK_HEIGHT },
  activity: { marginTop: spacing.xl },
  status: {
    ...typography.secondary,
    color: identity.bootForeground,
    marginTop: spacing.m,
    opacity: 0.8,
  },
});
