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
import { useI18n } from '../i18n';
import { colors } from '../ui/tokens';

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
  count,
  capabilities,
  onAddToAlbum,
  onTrash,
  onRestore,
  onRemoveFromAlbum,
  onCancel,
}: {
  count: number;
  capabilities: MediaSelectionCapabilities;
  onAddToAlbum: () => void;
  onTrash: () => void;
  onRestore: () => void;
  onRemoveFromAlbum?: () => void;
  onCancel: () => void;
}): React.JSX.Element | null {
  const { t } = useI18n();
  if (count === 0) return null;

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
    <View style={styles.bar}>
      <View style={styles.countRow}>
        <Text style={styles.count}>{count}</Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t('albumDetail.cancelSelection')}
          onPress={onCancel}
          hitSlop={8}
        >
          <Ionicons name="close" size={22} color={colors.textSecondary} />
        </Pressable>
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
              size={20}
              color={action.destructive === true ? '#B4344B' : colors.accent}
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

const styles = StyleSheet.create({
  bar: {
    position: 'absolute', left: 0, right: 0, bottom: 0,
    backgroundColor: '#fff', paddingBottom: 20, paddingTop: 8,
    borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: '#E2E7EF',
  },
  countRow: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    paddingHorizontal: 16, paddingBottom: 6,
  },
  count: { fontSize: 15, fontWeight: '600', color: colors.textPrimary },
  actions: { paddingHorizontal: 12, gap: 8 },
  action: {
    flexDirection: 'row', alignItems: 'center', gap: 8,
    paddingHorizontal: 14, paddingVertical: 10,
    borderRadius: 12, backgroundColor: '#F1F4F9',
  },
  actionLabel: { fontSize: 14, color: colors.accent },
  destructive: { color: '#B4344B' },
  pressed: { opacity: 0.7 },
});
