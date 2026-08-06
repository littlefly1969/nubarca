import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';

// One filter row: a static (non-focusable) label + ONE focusable button showing
// the current value; SELECT advances to the next value (cycling). This keeps
// the whole filter panel a simple vertical focus list — no grids to hunt
// through, no hidden controls.
interface Props {
  label: string;
  value: string;
  onCycle: () => void;
  hasTVPreferredFocus?: boolean;
}

export function CycleRow({ label, value, onCycle, hasTVPreferredFocus = false }: Props) {
  return (
    <View style={styles.row}>
      <Text style={styles.label} numberOfLines={1}>{label}</Text>
      <View style={styles.value}>
        <FocusableButton
          label={value}
          onPress={onCycle}
          hasTVPreferredFocus={hasTVPreferredFocus}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.md,
  },
  label: {
    color: colors.muted,
    fontSize: font.body,
    width: 320,
    textAlign: 'right',
  },
  value: { flex: 1, alignItems: 'flex-start' },
});
