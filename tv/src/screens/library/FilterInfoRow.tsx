import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';

// A statement inside the filter panel, deliberately NOT a disabled button.
// Disabled/no-op Pressables are misleading on a remote: they can still receive
// a focus ring, SELECT appears to work, and the viewer cannot tell whether the
// command was ignored or is unavailable. Information has its own visual and
// accessibility contract and never enters the native focus graph.
interface Props {
  label: string;
  value: string;
  description: string;
  accessibilityLabel: string;
}

export function FilterInfoRow({ label, value, description, accessibilityLabel }: Props) {
  return (
    <View
      style={styles.outer}
      accessible
      accessibilityRole="text"
      accessibilityLabel={accessibilityLabel}
      focusable={false}
    >
      <View style={styles.heading}>
        <Text style={styles.label}>{label}</Text>
        <Text style={styles.value}>{value}</Text>
      </View>
      <Text style={styles.description}>{description}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  outer: {
    gap: spacing.xs,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: colors.panelFocused,
    backgroundColor: colors.panel,
    paddingVertical: spacing.sm,
    paddingHorizontal: spacing.md,
  },
  heading: { flexDirection: 'row', alignItems: 'center', gap: spacing.md },
  label: { flex: 3, color: colors.muted, fontSize: font.body },
  value: { flex: 6, color: colors.text, fontSize: font.body, fontWeight: '800', textAlign: 'right' },
  description: { color: colors.muted, fontSize: font.caption, textAlign: 'right' },
});
