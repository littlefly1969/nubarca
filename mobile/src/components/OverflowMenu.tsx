// An overflow menu for header actions that do not fit.
//
// THE DEFECT THIS ANSWERS, found on a device: the album header carried six
// text buttons in a row. On a phone they ran off the edge, so the Party
// screen — and with it the whole message-moderation surface — was present in
// the app and unreachable. A feature nobody can tap is a feature that does not
// exist.
//
// A header therefore keeps at most a couple of primary actions; everything
// else comes here, where the list is vertical and cannot overflow sideways.
// Destructive entries are marked and sit last, so the two taps that delete an
// album are never adjacent to the two that rename it.

import React, { useState } from 'react';
import { Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useI18n } from '../i18n';
import { colors } from '../ui/tokens';

export interface OverflowAction {
  id: string;
  label: string;
  icon?: React.ComponentProps<typeof Ionicons>['name'];
  destructive?: boolean;
  onPress: () => void;
}

export function OverflowMenu({ actions }: { actions: OverflowAction[] }): React.JSX.Element | null {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  if (actions.length === 0) return null;

  // Destructive last: a mis-tap should not land on the irreversible entry.
  const ordered = [
    ...actions.filter((a) => a.destructive !== true),
    ...actions.filter((a) => a.destructive === true),
  ];

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t('menu.more')}
        onPress={() => setOpen(true)}
        hitSlop={8}
        style={({ pressed }) => [styles.trigger, pressed && styles.pressed]}
      >
        <Ionicons name="ellipsis-horizontal" size={22} color={colors.accent} />
      </Pressable>

      <Modal visible={open} transparent animationType="fade" onRequestClose={() => setOpen(false)}>
        {/* Tapping outside closes: a sheet with no visible dismiss is a trap. */}
        <Pressable
          style={styles.backdrop}
          accessibilityRole="button"
          accessibilityLabel={t('menu.close')}
          onPress={() => setOpen(false)}
        >
          <View style={styles.sheet}>
            {ordered.map((action) => (
              <Pressable
                key={action.id}
                accessibilityRole="button"
                accessibilityLabel={action.label}
                onPress={() => {
                  setOpen(false);
                  action.onPress();
                }}
                style={({ pressed }) => [styles.row, pressed && styles.rowPressed]}
              >
                {action.icon !== undefined && (
                  <Ionicons
                    name={action.icon}
                    size={20}
                    color={action.destructive === true ? '#B4344B' : colors.textSecondary}
                  />
                )}
                <Text
                  style={[styles.rowText, action.destructive === true && styles.destructive]}
                  numberOfLines={1}
                >
                  {action.label}
                </Text>
              </Pressable>
            ))}
          </View>
        </Pressable>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  trigger: { width: 40, height: 40, alignItems: 'center', justifyContent: 'center' },
  backdrop: { flex: 1, backgroundColor: 'rgba(10,15,26,0.35)', justifyContent: 'flex-end' },
  sheet: {
    backgroundColor: '#fff',
    borderTopLeftRadius: 18,
    borderTopRightRadius: 18,
    paddingVertical: 8,
    paddingBottom: 28,
  },
  row: { flexDirection: 'row', alignItems: 'center', gap: 14, paddingHorizontal: 22, paddingVertical: 15 },
  rowPressed: { backgroundColor: '#F1F4F9' },
  rowText: { fontSize: 16, color: colors.textPrimary, flexShrink: 1 },
  destructive: { color: '#B4344B' },
  pressed: { opacity: 0.6 },
});
