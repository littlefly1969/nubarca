import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';

// One filter in the TV panel: a single full-width focusable row carrying its
// own label, its current value in words, whether it is doing something, and
// whether SELECT opens an editor.
//
// It replaces the earlier CycleRow, whose focusable part was the VALUE button
// with the label as separate static text beside it. Two things followed from
// that split and both are fixed here. Focus landed on a control announcing only
// "Qualsiasi", with the word "Persone" living in a view the remote never
// visited — so the accessibility label was the value alone, with no filter name
// and no state. And a row could only ever mean "cycle me", which is why the row
// that needed to OPEN something was written as a readout instead.
//
// State is never carried by color alone: an active filter gets a ● marker and a
// bold label, and focus gets a caret, a white outer border, an accent inner
// ring and a brighter background — all legible on a washed-out television and
// to a viewer who cannot separate the two blues. The row does not scale on
// focus the way FocusableButton does: at full width that would push the edges
// past the safe area rather than draw attention.
interface Props {
  label: string;
  // Human summary of the current value ("Marco, Giulia · Qualsiasi", "—").
  value: string;
  active: boolean;
  // True when SELECT opens a sub-editor rather than advancing in place; drives
  // the disclosure caret so the two behaviours are distinguishable before
  // pressing.
  opensEditor: boolean;
  // Fully-formed sentence for screen readers — label, value AND state, built by
  // the panel because only it has the dictionary.
  accessibilityLabel: string;
  hasTVPreferredFocus?: boolean;
  onSelect: () => void;
  onFocus?: () => void;
}

export function FilterRow({
  label, value, active, opensEditor, accessibilityLabel,
  hasTVPreferredFocus = false, onSelect, onFocus,
}: Props) {
  const [focused, setFocused] = useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel}
      focusable
      hasTVPreferredFocus={hasTVPreferredFocus}
      onFocus={() => { setFocused(true); onFocus?.(); }}
      onBlur={() => setFocused(false)}
      onPress={onSelect}
      style={[styles.outer, focused && styles.outerFocused]}
    >
      <View style={[styles.inner, focused && styles.innerFocused]}>
        <Text style={[styles.label, active && styles.labelActive]} numberOfLines={1}>
          {focused ? '▸ ' : ''}{active ? '● ' : ''}{label}
        </Text>
        <Text style={[styles.value, active && styles.valueActive]} numberOfLines={1}>
          {value}
        </Text>
        <Text style={styles.disclosure}>{opensEditor ? '›' : ''}</Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  outer: {
    borderRadius: 12,
    // Reserved in both states so taking focus never moves the row.
    borderWidth: 3,
    borderColor: 'transparent',
    backgroundColor: colors.panel,
  },
  outerFocused: {
    borderColor: '#ffffff',
    backgroundColor: colors.panelFocused,
  },
  inner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.md,
    borderRadius: 8,
    borderWidth: 2,
    borderColor: 'transparent',
    paddingVertical: spacing.sm,
    paddingHorizontal: spacing.md,
  },
  innerFocused: { borderColor: colors.accent },
  // Fixed shares so the values line up in a readable column. The value gets the
  // larger one: labels are short nouns, values carry composed summaries, and at
  // 720p the row has roughly 650dp of text width to divide between them.
  label: { flex: 3, color: colors.muted, fontSize: font.body },
  labelActive: { color: colors.text, fontWeight: '800' },
  value: { flex: 6, color: colors.muted, fontSize: font.body, textAlign: 'right' },
  valueActive: { color: colors.text, fontWeight: '700' },
  disclosure: { width: 24, color: colors.muted, fontSize: font.body, textAlign: 'right' },
});
