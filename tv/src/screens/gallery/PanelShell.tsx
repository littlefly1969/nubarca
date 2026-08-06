import { useEffect, type ReactNode } from 'react';
import { BackHandler, ScrollView, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';

// Full-screen panel container for the Personal Gallery MENU actions (filters /
// sort / search / people / albums / details). Opaque, so the grid behind it is
// fully covered; the grid's tiles are made non-focusable by the parent while a
// panel is open, so DPAD focus cannot escape underneath. Registers its OWN
// hardware-Back handler on mount — handlers are LIFO, so the deepest open panel
// always wins BACK, closing itself first (spec: BACK closes the deepest panel).
interface Props {
  title: string;
  onBack: () => void;
  children: ReactNode;
  // Custom BACK behavior (e.g. the keyboard deletes a character first). Return
  // true when handled; false falls through to onBack.
  onBackOverride?: () => boolean;
}

export function PanelShell({ title, onBack, children, onBackOverride }: Props) {
  useEffect(() => {
    const onBackPress = () => {
      if (onBackOverride?.()) return true;
      onBack();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onBack, onBackOverride]);

  return (
    <View style={styles.container}>
      <Text style={styles.title}>{title}</Text>
      <ScrollView
        style={styles.scroll}
        contentContainerStyle={styles.content}
        // The DPAD moves between focusables; the ScrollView follows the focused
        // child automatically on TV. No nested horizontal scrolling.
        showsVerticalScrollIndicator={false}
      >
        {children}
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: colors.bg,
    paddingVertical: spacing.xl,
    paddingHorizontal: spacing.xl * 2,
  },
  title: {
    color: colors.text,
    fontSize: font.heading,
    fontWeight: '800',
    marginBottom: spacing.lg,
    textAlign: 'center',
  },
  scroll: { flex: 1 },
  content: { gap: spacing.sm, paddingBottom: spacing.xl },
});
