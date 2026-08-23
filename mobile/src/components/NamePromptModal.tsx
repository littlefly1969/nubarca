// NamePromptModal: one modal for album create / rename / description edit.
// Used by the Albums tab (create) and Album detail (rename).

import React, { useEffect, useState } from 'react';
import {
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { colors, radii, spacing, touch } from '../ui/tokens';
import { useI18n } from '../i18n';

export interface NamePromptModalProps {
  visible: boolean;
  title: string;
  initialName?: string;
  initialDescription?: string | null;
  /** When false the description field is hidden (plain create). */
  withDescription?: boolean;
  requireDescription?: boolean;
  onCancel: () => void;
  onSubmit: (name: string, description: string | null) => Promise<void> | void;
}

export function NamePromptModal({
  visible,
  title,
  initialName = '',
  initialDescription = null,
  withDescription = false,
  requireDescription = true,
  onCancel,
  onSubmit,
}: NamePromptModalProps): React.JSX.Element {
  const { t } = useI18n();
  const [name, setName] = useState(initialName);
  const [description, setDescription] = useState(initialDescription ?? '');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (visible) {
      setName(initialName);
      setDescription(initialDescription ?? '');
      setBusy(false);
    }
  }, [visible, initialName, initialDescription]);

  const nameValid = name.trim().length > 0;
  const descriptionValid =
    !requireDescription || !withDescription || description.trim().length > 0;

  async function submit(): Promise<void> {
    if (!nameValid || !descriptionValid || busy) return;
    setBusy(true);
    try {
      await onSubmit(name.trim(), withDescription ? description.trim() || null : null);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      visible={visible}
      transparent
      animationType="fade"
      onRequestClose={onCancel}
    >
      <KeyboardAvoidingView
        style={styles.backdrop}
        behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      >
        <View style={styles.card}>
          <Text style={styles.title}>{title}</Text>

          <TextInput
            style={styles.input}
            value={name}
            onChangeText={setName}
            placeholder={t('albums.nameLabel')}
            placeholderTextColor={colors.textTertiary}
            autoFocus
            editable={!busy}
            accessibilityLabel={t('albums.nameLabel')}
          />

          {withDescription && (
            <TextInput
              style={[styles.input, styles.description]}
              value={description}
              onChangeText={setDescription}
              placeholder={t('albums.descriptionLabel')}
              placeholderTextColor={colors.textTertiary}
              multiline
              editable={!busy}
              accessibilityLabel={t('albums.descriptionLabel')}
            />
          )}

          <View style={styles.actions}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('albums.cancel')}
              onPress={onCancel}
              disabled={busy}
              style={({ pressed }) => [styles.btn, pressed && styles.pressed]}
            >
              <Text style={styles.cancelText}>{t('albums.cancel')}</Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('albums.save')}
              onPress={() => {
                void submit();
              }}
              disabled={!nameValid || !descriptionValid || busy}
              style={({ pressed }) => [
                styles.btn,
                styles.primaryBtn,
                pressed && styles.pressed,
                (!nameValid || !descriptionValid || busy) && styles.disabled,
              ]}
            >
              <Text style={styles.saveText}>{t('albums.save')}</Text>
            </Pressable>
          </View>
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    backgroundColor: 'rgba(10, 15, 26, 0.5)',
    alignItems: 'center',
    justifyContent: 'center',
    padding: spacing.xl,
  },
  card: {
    width: '100%',
    maxWidth: 420,
    backgroundColor: colors.surface,
    borderRadius: radii.l,
    padding: spacing.xl,
  },
  title: {
    fontSize: 17,
    fontWeight: '700',
    color: colors.textPrimary,
    marginBottom: spacing.l,
  },
  input: {
    borderWidth: 1,
    borderColor: colors.separator,
    borderRadius: radii.m,
    paddingHorizontal: spacing.m,
    minHeight: touch.minSize - 6,
    backgroundColor: colors.canvas,
    color: colors.textPrimary,
    marginBottom: spacing.m,
  },
  description: {
    minHeight: touch.minSize + 12,
    textAlignVertical: 'top',
    paddingTop: spacing.m,
  },
  actions: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    gap: spacing.s,
    marginTop: spacing.s,
  },
  btn: {
    minWidth: 88,
    minHeight: touch.minSize - 8,
    borderRadius: radii.m,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.l,
  },
  primaryBtn: { backgroundColor: colors.accent },
  disabled: { backgroundColor: colors.accentDisabled },
  cancelText: { color: colors.textSecondary, fontWeight: '600' },
  saveText: { color: colors.textOnAccent, fontWeight: '600' },
  pressed: { opacity: 0.75 },
});
