import { useEffect } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { useI18n } from '../i18n';
import { FocusableButton } from './FocusableButton';
import { MenuCommandRail } from './MenuCommandRail';
import type { ViewerBarAction } from '../video/remoteMap';

// The viewer's action bar: opened with UP, walked with the D-pad, closed with
// BACK (or by choosing a command that leaves nothing else to do).
//
// It is a real focus scope (MenuCommandRail traps every direction), so while it
// is open LEFT/RIGHT move between its buttons and never change photo — the
// viewer stops interpreting the D-pad until the bar is gone. The order comes
// from viewerBarActions(): Play/Pause is always first, which is what makes the
// slideshow reachable on a remote with no transport key.
interface Props {
  actions: readonly ViewerBarAction[];
  isVideo: boolean;
  slideshowRunning: boolean;
  selected: boolean;
  selectedCount: number;
  onAction: (action: ViewerBarAction) => void;
  // Any focus movement inside the bar is activity for the idle chrome.
  onActivity?: () => void;
  top: number;
}

export function ViewerActionBar({
  actions, isVideo, slideshowRunning, selected, selectedCount, onAction, onActivity, top,
}: Props) {
  const { t, tn } = useI18n();
  const label = (action: ViewerBarAction): string => {
    switch (action) {
      case 'play-pause':
        // The player owns its own state; the bar offers the toggle.
        if (isVideo) return t('viewer.bar.toggleVideo');
        return slideshowRunning ? t('viewer.bar.pause') : t('viewer.bar.play');
      case 'select': return selected ? t('viewer.bar.deselect') : t('viewer.bar.select');
      case 'prev': return t('viewer.bar.prev');
      case 'next': return t('viewer.bar.next');
      case 'clear-selection': return t('viewer.bar.clear');
    }
  };
  return (
    <View style={[styles.wrap, { top }]}>
      <MenuCommandRail style={styles.rail}>
        {actions.map((action, i) => (
          <FocusableButton
            key={action}
            label={label(action)}
            selected={action === 'select' && selected}
            hasTVPreferredFocus={i === 0}
            onPress={() => onAction(action)}
            onFocusChange={(f) => { if (f) onActivity?.(); }}
          />
        ))}
      </MenuCommandRail>
      {selectedCount > 0 && (
        <Text style={styles.count} accessibilityLiveRegion="polite">
          {tn(selectedCount, 'viewer.bar.count')}
        </Text>
      )}
    </View>
  );
}

// The discreet confirmation of what OK did ("✓ Selected"), gone on its own.
export function ViewerFlash({ text, bottom, onDone }: { text: string; bottom: number; onDone: () => void }) {
  useEffect(() => {
    const timer = setTimeout(onDone, 1400);
    return () => clearTimeout(timer);
  }, [onDone]);
  return (
    <View style={[styles.flash, { bottom }]} pointerEvents="none" accessibilityLiveRegion="polite">
      <Text style={styles.count}>{text}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  flash: {
    position: 'absolute',
    alignSelf: 'center',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  wrap: {
    position: 'absolute',
    left: 0,
    right: 0,
    alignItems: 'center',
  },
  rail: {
    flexDirection: 'row',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.82)',
  },
  count: {
    marginTop: spacing.xs,
    color: colors.text,
    fontSize: font.caption,
    fontWeight: '700',
  },
});
