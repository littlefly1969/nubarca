// Add-to-album sheet: choose an existing album or create one, then ONE bulk
// request. Presents only the safe counts from BulkAlbumItemsResult.

import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  FlatList,
  Modal,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { listAlbums, createAlbum, bulkAddAlbumItems } from '../api/albums.ts';
import type { AlbumSummary, BulkAlbumItemsResult } from '../api/albums.ts';
import { radii, spacing, touch, type as typeRoles } from '../ui/tokens';
import { useI18n } from '../i18n';
import { themed, useColors } from '../ui/theme.ts';

export interface AddToAlbumSheetProps {
  visible: boolean;
  onClose: () => void;
  onCompleted: (result: BulkAlbumItemsResult | null) => void;
  /** Ids to add. */
  fileItemIds: string[];
}

export function AddToAlbumSheet({
  visible,
  onClose,
  onCompleted,
  fileItemIds,
}: AddToAlbumSheetProps): React.JSX.Element {
  const styles = useStyles();
  const colors = useColors();
  const { t } = useI18n();
  const [albums, setAlbums] = useState<AlbumSummary[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);

  useEffect(() => {
    if (!visible) return;
    let cancelled = false;
    setAlbums(null);
    setCreating(false);
    setNewName('');
    void listAlbums().then(
      (list) => {
        if (!cancelled) setAlbums(list);
      },
      () => {
        if (!cancelled) setAlbums([]);
      },
    );
    return () => {
      cancelled = true;
    };
  }, [visible]);

  const add = useCallback(
    async (albumId: string, albumName: string) => {
      if (busyId !== null) return;
      setBusyId(albumId);
      try {
        const result = await bulkAddAlbumItems(albumId, fileItemIds);
        onCompleted(result);
        Alert.alert(
          albumName,
          t('selection.addedNotice', {
            succeeded: result.succeeded,
            skipped: result.skipped,
          }),
        );
        onClose();
      } catch {
        Alert.alert(albumName, t('gallery.loadErrorNetwork', { what: t('selection.addToAlbum') }));
      } finally {
        setBusyId(null);
      }
    },
    [busyId, fileItemIds, onClose, onCompleted, t],
  );

  const createAndAdd = useCallback(async () => {
    const name = newName.trim();
    if (name.length === 0 || busyId !== null) return;
    setBusyId('__create__');
    try {
      const album = await createAlbum(name);
      await add(album.id, album.name);
    } catch {
      Alert.alert(name, t('gallery.loadErrorNetwork', { what: t('albums.create') }));
    } finally {
      setBusyId(null);
    }
  }, [add, busyId, newName, t]);

  return (
    <Modal
      visible={visible}
      animationType="slide"
      presentationStyle="pageSheet"
      onRequestClose={onClose}
    >
      <View style={styles.sheet}>
        <View style={styles.header}>
          <Text style={styles.title}>{t('selection.chooseAlbum')}</Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('albums.cancel')}
            onPress={onClose}
            hitSlop={8}
          >
            <Ionicons name="close" size={24} color={colors.textSecondary} />
          </Pressable>
        </View>

        {fileItemIds.length > 0 && (
          <Text style={styles.count}>
            {t('selection.selectedCount', { count: fileItemIds.length })}
          </Text>
        )}

        {albums === null ? (
          <ActivityIndicator style={styles.loading} color={colors.accent} />
        ) : (
          <FlatList
            data={albums}
            keyExtractor={(a) => a.id}
            renderItem={({ item }) => (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t('selection.addToAlbum')}
                onPress={() => {
                  void add(item.id, item.name);
                }}
                disabled={busyId !== null}
                style={({ pressed }) => [styles.row, pressed && styles.pressed]}
              >
                <Ionicons name="albums-outline" size={20} color={colors.accent} />
                <Text style={styles.rowName} numberOfLines={1} ellipsizeMode="tail">
                  {item.name}
                </Text>
                {busyId === item.id ? (
                  <ActivityIndicator color={colors.accent} />
                ) : (
                  <Ionicons name="add" size={18} color={colors.textTertiary} />
                )}
              </Pressable>
            )}
            ListEmptyComponent={
              <Text style={styles.empty}>{t('albums.emptyHint')}</Text>
            }
          />
        )}

        <View style={styles.createArea}>
          {creating ? (
            <>
              <TextInput
                style={styles.input}
                value={newName}
                onChangeText={setNewName}
                placeholder={t('albums.nameLabel')}
                placeholderTextColor={colors.textTertiary}
                autoFocus
                editable={busyId === null}
              />
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={t('albums.save')}
                onPress={() => {
                  void createAndAdd();
                }}
                disabled={newName.trim().length === 0 || busyId !== null}
                style={({ pressed }) => [
                  styles.createBtn,
                  pressed && styles.pressed,
                  (newName.trim().length === 0 || busyId !== null) && styles.disabledBtn,
                ]}
              >
                {busyId === '__create__' ? (
                  <ActivityIndicator color={colors.textOnAccent} />
                ) : (
                  <Ionicons name="checkmark" size={20} color={colors.textOnAccent} />
                )}
              </Pressable>
            </>
          ) : (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('selection.createNew')}
              onPress={() => setCreating(true)}
              style={({ pressed }) => [styles.newRow, pressed && styles.pressed]}
            >
              <Ionicons name="add-circle-outline" size={22} color={colors.accent} />
              <Text style={styles.newRowText}>{t('selection.createNew')}</Text>
            </Pressable>
          )}
        </View>
      </View>
    </Modal>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    sheet: {
      flex: 1,
      backgroundColor: colors.surface,
      paddingTop: spacing.l,
      paddingHorizontal: spacing.l,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      minHeight: touch.minSize - 6,
    },
    title: { fontSize: 17, fontWeight: '700', color: colors.textPrimary },
    count: { ...typeRoles.secondary, color: colors.textSecondary, marginTop: spacing.s },
    loading: { flex: 1 },
    row: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.m,
      paddingVertical: spacing.m,
      minHeight: touch.minSize - 4,
    },
    rowName: {
      flex: 1,
      fontSize: 15,
      color: colors.textPrimary,
    },
    empty: {
      fontSize: 13,
      color: colors.textTertiary,
      textAlign: 'center',
      marginTop: spacing.xl,
    },
    createArea: {
      borderTopWidth: StyleSheet.hairlineWidth,
      borderTopColor: colors.separator,
      paddingTop: spacing.s,
      paddingBottom: spacing.xl,
    },
    newRow: {
      flexDirection: 'row',
      alignItems: 'center',
      gap: spacing.m,
      minHeight: touch.minSize,
    },
    newRowText: { color: colors.accent, fontWeight: '600', fontSize: 15 },
    input: {
      flex: 1,
      borderWidth: 1,
      borderColor: colors.separator,
      borderRadius: radii.m,
      paddingHorizontal: spacing.m,
      minHeight: touch.minSize - 6,
      backgroundColor: colors.canvas,
      color: colors.textPrimary,
    },
    createBtn: {
      width: touch.minSize - 4,
      height: touch.minSize - 4,
      borderRadius: radii.round,
      backgroundColor: colors.accent,
      alignItems: 'center',
      justifyContent: 'center',
    },
    disabledBtn: { backgroundColor: colors.accentDisabled },
    pressed: { opacity: 0.7 },
  }),
);
