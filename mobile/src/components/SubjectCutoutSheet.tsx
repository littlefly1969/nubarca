// "Copy subject" sheet for one of the owner's own photos. The server lifts the
// subject off its background and answers a transparent PNG; the sheet keeps it
// in ONE app-cache file for as long as it is open, so the preview and the
// share sheet read the same bytes, and deletes that file when it closes.
//
// No new native module: sharing goes through expo-sharing (the system share
// sheet also offers "save to gallery"), and "Save to NubArca" files the PNG on
// the server next to the original photo.

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Image,
  Modal,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { File, Paths } from 'expo-file-system';
import * as Sharing from 'expo-sharing';
import { Ionicons } from '@expo/vector-icons';
import {
  fetchSubjectCutout,
  saveSubjectCutout,
  subjectCutoutFailure,
  type SubjectCutoutFailure,
} from '../api/subjectCutout.ts';
import { cutoutFileName } from '../media/subjectCutout.ts';
import { iconSizes, radius, spacing, touch, typography } from '../ui/tokens';
import { media } from '../ui/palette.ts';
import { useI18n } from '../i18n';

export interface SubjectCutoutSheetProps {
  fileId: string;
  displayName: string;
  onClose: () => void;
}

type Phase =
  | { kind: 'working' }
  | { kind: 'ready'; uri: string }
  | { kind: 'failed'; reason: SubjectCutoutFailure };

type SaveState = 'idle' | 'saving' | 'saved' | 'failed';

// Retrying cannot help a photo that is gone or a server without the model.
const FINAL_FAILURES: ReadonlySet<SubjectCutoutFailure> = new Set(['notFound', 'unavailable']);

function deleteQuietly(file: File | null): void {
  if (file === null) return;
  try {
    if (file.exists) file.delete();
  } catch {
    // Cache only: the OS reclaims it anyway.
  }
}

export function SubjectCutoutSheet({
  fileId,
  displayName,
  onClose,
}: SubjectCutoutSheetProps): React.JSX.Element {
  const { t } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'working' });
  const [attempt, setAttempt] = useState(0);
  const [save, setSave] = useState<SaveState>('idle');
  const [shareFailed, setShareFailed] = useState(false);
  const fileRef = useRef<File | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setPhase({ kind: 'working' });
    void fetchSubjectCutout(fileId, controller.signal).then(
      (bytes) => {
        if (controller.signal.aborted) return;
        try {
          // A fresh name per attempt: the preview must never show a stale file.
          const file = new File(
            Paths.cache,
            `nubarca-cutout-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}.png`,
          );
          file.write(new Uint8Array(bytes));
          deleteQuietly(fileRef.current);
          fileRef.current = file;
          setPhase({ kind: 'ready', uri: file.uri });
        } catch {
          setPhase({ kind: 'failed', reason: 'failed' });
        }
      },
      (error: unknown) => {
        if (controller.signal.aborted) return;
        setPhase({ kind: 'failed', reason: subjectCutoutFailure(error) });
      },
    );
    return () => controller.abort();
  }, [fileId, attempt]);

  useEffect(() => () => deleteQuietly(fileRef.current), []);

  const share = useCallback(async () => {
    if (phase.kind !== 'ready') return;
    setShareFailed(false);
    try {
      await Sharing.shareAsync(phase.uri, {
        mimeType: 'image/png',
        UTI: 'public.png',
        dialogTitle: cutoutFileName(displayName),
      });
    } catch {
      setShareFailed(true);
    }
  }, [displayName, phase]);

  const saveToNubArca = useCallback(async () => {
    if (save === 'saving' || save === 'saved') return;
    setSave('saving');
    try {
      await saveSubjectCutout(fileId);
      setSave('saved');
    } catch {
      setSave('failed');
    }
  }, [fileId, save]);

  return (
    <Modal visible animationType="slide" presentationStyle="fullScreen" onRequestClose={onClose}>
      <View style={styles.root}>
        <View style={styles.header}>
          <Text style={styles.title} numberOfLines={1} ellipsizeMode="middle">
            {t('subjectCutout.action')}
          </Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('subjectCutout.close')}
            onPress={onClose}
            style={({ pressed }) => [styles.iconBtn, pressed && styles.pressed]}
            hitSlop={8}
          >
            <Ionicons name="close" size={iconSizes.m} color={media.text} />
          </Pressable>
        </View>

        <View style={styles.stage}>
          {phase.kind === 'working' && (
            <View style={styles.centered}>
              <ActivityIndicator color={media.text} />
              <Text style={styles.message}>{t('subjectCutout.working')}</Text>
            </View>
          )}
          {phase.kind === 'ready' && (
            <Image
              source={{ uri: phase.uri }}
              style={styles.preview}
              resizeMode="contain"
              accessibilityLabel={t('subjectCutout.previewAlt')}
            />
          )}
          {phase.kind === 'failed' && (
            <View style={styles.centered}>
              <Text style={styles.message}>{t(`subjectCutout.error.${phase.reason}`)}</Text>
              {!FINAL_FAILURES.has(phase.reason) && (
                <Pressable
                  accessibilityRole="button"
                  onPress={() => setAttempt((n) => n + 1)}
                  style={({ pressed }) => [styles.button, pressed && styles.pressed]}
                >
                  <Text style={styles.buttonText}>{t('common.retry')}</Text>
                </Pressable>
              )}
            </View>
          )}
        </View>

        {phase.kind === 'ready' && (
          <View style={styles.actions}>
            {shareFailed && <Text style={styles.note}>{t('subjectCutout.shareFailed')}</Text>}
            {save === 'saved' && <Text style={styles.note}>{t('subjectCutout.saved')}</Text>}
            {save === 'failed' && <Text style={styles.note}>{t('subjectCutout.saveFailed')}</Text>}
            <View style={styles.actionRow}>
              <Pressable
                accessibilityRole="button"
                onPress={() => void share()}
                style={({ pressed }) => [styles.button, styles.grow, pressed && styles.pressed]}
              >
                <Ionicons name="share-outline" size={iconSizes.s} color={media.text} />
                <Text style={styles.buttonText}>{t('subjectCutout.share')}</Text>
              </Pressable>
              <Pressable
                accessibilityRole="button"
                accessibilityState={{ disabled: save === 'saving' || save === 'saved' }}
                disabled={save === 'saving' || save === 'saved'}
                onPress={() => void saveToNubArca()}
                style={({ pressed }) => [
                  styles.button,
                  styles.grow,
                  (pressed || save === 'saved') && styles.pressed,
                ]}
              >
                {save === 'saving' ? (
                  <ActivityIndicator color={media.text} />
                ) : (
                  <Ionicons
                    name={save === 'saved' ? 'checkmark' : 'cloud-upload-outline'}
                    size={iconSizes.s}
                    color={media.text}
                  />
                )}
                <Text style={styles.buttonText}>
                  {save === 'saving' ? t('subjectCutout.saving') : t('subjectCutout.save')}
                </Text>
              </Pressable>
            </View>
          </View>
        )}
      </View>
    </Modal>
  );
}

// NOT themed: like the viewer it opens from, the sheet is dark in both themes.
const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: media.background, paddingTop: spacing.xl },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.m,
    paddingHorizontal: spacing.m,
    paddingBottom: spacing.s,
  },
  title: { ...typography.label, flex: 1, color: media.text },
  iconBtn: {
    width: touch.minSize,
    height: touch.minSize,
    borderRadius: radius.pill,
    backgroundColor: media.chromeButton,
    alignItems: 'center',
    justifyContent: 'center',
  },
  // A light, neutral stage so a dark subject stays visible on its transparency.
  stage: {
    flex: 1,
    margin: spacing.m,
    borderRadius: radius.card,
    backgroundColor: media.chromeButton,
    alignItems: 'center',
    justifyContent: 'center',
    overflow: 'hidden',
  },
  preview: { width: '100%', height: '100%' },
  centered: { alignItems: 'center', gap: spacing.m, padding: spacing.l },
  message: { ...typography.secondary, color: media.text, textAlign: 'center' },
  actions: { paddingHorizontal: spacing.m, paddingBottom: spacing.xl, gap: spacing.s },
  actionRow: { flexDirection: 'row', gap: spacing.m },
  note: { ...typography.secondary, color: media.textSecondary, textAlign: 'center' },
  button: {
    minHeight: touch.minSize,
    flexDirection: 'row',
    gap: spacing.s,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: spacing.l,
    borderRadius: radius.pill,
    backgroundColor: media.chromeButton,
  },
  grow: { flex: 1 },
  buttonText: { ...typography.button, color: media.text },
  pressed: { opacity: 0.7 },
});
