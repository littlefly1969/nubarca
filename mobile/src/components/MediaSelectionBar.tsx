// The selection action bar (§20-§22, §38).
//
// WHICH ACTIONS EXIST IS NOT A DEVICE DECISION. The offer comes from the
// shared capability matrix — the same function the browser asks — and this
// component only draws the answer as a phone would. There is no `if (isMobile)`
// anywhere in it, and no mobile catalogue of destinations: an action the
// library does not currently support cannot be invented here.
//
// The confirmations say what actually happens (§24). Removing items from an
// album and moving them to the Trash are different things, and the wording
// makes that unmistakable: album removal explicitly says the files stay in the
// library.

import React from 'react';
import { Alert, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import type { MediaSelectionCapabilities } from '@nubarca/contracts';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useI18n } from '../i18n';
import { IconButton } from '../ui/components';
import { iconSizes, radius, spacing, touch, typography } from '../ui/tokens';
import { themed, useColors } from '../ui/theme';

export interface SelectionAction {
  id: 'add-to-album' | 'trash' | 'restore' | 'remove-from-album';
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  destructive?: boolean;
  /** A confirmation is REQUIRED for anything that removes something. */
  confirm?: { title: string; body: string };
  run: () => void;
}

export function MediaSelectionBar({
  selecting,
  count,
  capabilities,
  onAddToAlbum,
  onTrash,
  onRestore,
  onRemoveFromAlbum,
  onCancel,
}: {
  /** Whether the mode is OPEN, which is not the same as having picked
   * something. A bar that appeared only once an item was selected made the
   * header's select button look broken: it turned the mode on and nothing
   * visible happened. */
  selecting: boolean;
  count: number;
  capabilities: MediaSelectionCapabilities;
  onAddToAlbum: () => void;
  onTrash: () => void;
  onRestore: () => void;
  onRemoveFromAlbum?: () => void;
  onCancel: () => void;
}): React.JSX.Element | null {
  const styles = useStyles();
  const colors = useColors();
  const insets = useSafeAreaInsets();
  const { t } = useI18n();
  if (!selecting) return null;

  const actions: SelectionAction[] = [];
  if (capabilities.canAddToAlbum) {
    actions.push({
      id: 'add-to-album',
      label: t('selection.addToAlbum'),
      icon: 'albums-outline',
      run: onAddToAlbum,
    });
  }
  if (capabilities.canRemoveFromCurrentAlbum && onRemoveFromAlbum !== undefined) {
    actions.push({
      id: 'remove-from-album',
      label: t('selection.removeFromAlbum'),
      icon: 'remove-circle-outline',
      confirm: {
        title: t('selection.removeFromAlbumConfirmTitle'),
        // Says in so many words that the media survives.
        body: t('selection.removeFromAlbumConfirmBody', { n: String(count) }),
      },
      run: onRemoveFromAlbum,
    });
  }
  if (capabilities.canRestore) {
    actions.push({
      id: 'restore',
      label: t('selection.restore'),
      icon: 'arrow-undo-outline',
      run: onRestore,
    });
  }
  if (capabilities.canTrash) {
    actions.push({
      id: 'trash',
      label: t('selection.trash'),
      icon: 'trash-outline',
      destructive: true,
      confirm: {
        title: t('selection.trashConfirmTitle'),
        body: t('selection.trashConfirmBody', { n: String(count) }),
      },
      run: onTrash,
    });
  }

  const invoke = (action: SelectionAction): void => {
    if (action.confirm === undefined) return action.run();
    Alert.alert(action.confirm.title, action.confirm.body, [
      { text: t('albums.cancel'), style: 'cancel' },
      {
        text: action.label,
        style: action.destructive === true ? 'destructive' : 'default',
        onPress: action.run,
      },
    ]);
  };

  return (
    <View style={[styles.bar, { paddingBottom: spacing.s + insets.bottom }]}>
      <View style={styles.countRow}>
        {/* With nothing picked yet, say what to do rather than show a bare 0. */}
        <Text style={count === 0 ? styles.hint : styles.count}>
          {count === 0 ? t('selection.hint') : String(count)}
        </Text>
        <IconButton
          accessibilityLabel={t('albumDetail.cancelSelection')}
          onPress={onCancel}
        >
          <Ionicons name="close" size={iconSizes.m} color={colors.textSecondary} />
        </IconButton>
      </View>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.actions}>
        {actions.map((action) => (
          <Pressable
            key={action.id}
            accessibilityRole="button"
            accessibilityLabel={action.label}
            onPress={() => invoke(action)}
            style={({ pressed }) => [styles.action, pressed && styles.pressed]}
          >
            <Ionicons
              name={action.icon}
              size={iconSizes.m}
              color={action.destructive === true ? colors.danger : colors.accent}
            />
            <Text
              style={[styles.actionLabel, action.destructive === true && styles.destructive]}
              numberOfLines={1}
            >
              {action.label}
            </Text>
          </Pressable>
        ))}
      </ScrollView>
    </View>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    // A temporary operating mode, not a second bottom navigation: one surface,
    // one hairline, and the real inset instead of a 20 px guess at a home bar.
    bar: {
      position: 'absolute',
      left: 0,
      right: 0,
      bottom: 0,
      backgroundColor: colors.surface,
      paddingTop: spacing.s,
      borderTopWidth: StyleSheet.hairlineWidth,
      borderTopColor: colors.separator,
    },
    countRow: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      paddingLeft: spacing.l,
      paddingRight: spacing.s,
      paddingBottom: spacing.xs + 2,
    },
    count: { ...typography.label, color: colors.textPrimary },
    hint: { ...typography.secondary, color: colors.textTertiary, flexShrink: 1 },
    actions: { paddingHorizontal: spacing.m, gap: spacing.s },
    // Accent TEXT and icon on a quiet surface. No filled blue call to action:
    // the capability matrix may offer several of these at once, and a row of
    // primary buttons would say they are all the dominant one.
    action: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.s,
      minHeight: touch.minSize,
      paddingHorizontal: spacing.m + 2,
      paddingVertical: spacing.s + 2,
      borderRadius: radius.control,
      backgroundColor: colors.surfaceSubtle,
    },
    actionLabel: { ...typography.label, color: colors.accent },
    destructive: { color: colors.danger },
    pressed: { opacity: 0.7 },
  }),
);
