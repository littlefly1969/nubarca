// "Copy subject" sheet for one of the owner's own photos. The server lifts the
// subject off its background and answers a transparent PNG; the sheet keeps it
// in ONE app-cache file for as long as it is open, so the preview and the
// share sheet read the same bytes, and deletes that file when it closes.
//
// No new native module: sharing goes through expo-sharing, and "Save to
// NubArca" files the PNG on the server next to the original photo. When the
// server offers it, tapping a subject in the preview keeps only that one (the
// tap is mapped back onto the photo through the cutout's region).

import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  type GestureResponderEvent,
  Image,
  type LayoutChangeEvent,
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
  subjectPointOnPhoto,
  type SubjectCutoutFailure,
  type SubjectCutoutRegion,
  type SubjectPoint,
} from '../api/subjectCutout.ts';
import { containFraction, cutoutFileName, pngSize } from '../media/subjectCutout.ts';
import { iconSizes, radius, spacing, touch, typography } from '../ui/tokens';
import { media } from '../ui/palette.ts';
import { useI18n } from '../i18n';

export interface SubjectCutoutSheetProps {
  fileId: string;
  displayName: string;
  /** The server can keep only a tapped subject. */
  pointSelection: boolean;
  onClose: () => void;
}

type Phase =
  | { kind: 'working' }
  | {
      kind: 'ready';
      uri: string;
      region: SubjectCutoutRegion | null;
      size: { width: number; height: number } | null;
    }
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
  pointSelection,
  onClose,
}: SubjectCutoutSheetProps): React.JSX.Element {
  const { t } = useI18n();
  const [phase, setPhase] = useState<Phase>({ kind: 'working' });
  const [attempt, setAttempt] = useState(0);
  // The touched point on the photo, or null for every subject.
  const [point, setPoint] = useState<SubjectPoint | null>(null);
  const [stage, setStage] = useState<{ width: number; height: number } | null>(null);
  const [save, setSave] = useState<SaveState>('idle');
  const [shareFailed, setShareFailed] = useState(false);
  const fileRef = useRef<File | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setPhase({ kind: 'working' });
    setSave('idle');
    setShareFailed(false);
    void fetchSubjectCutout(fileId, { point, signal: controller.signal }).then(
      ({ bytes, region }) => {
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
          setPhase({ kind: 'ready', uri: file.uri, region, size: pngSize(bytes) });
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
  }, [fileId, attempt, point]);

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
      await saveSubjectCutout(fileId, point);
      setSave('saved');
    } catch {
      setSave('failed');
    }
  }, [fileId, point, save]);

  const canTap = pointSelection && phase.kind === 'ready' && phase.region !== null && phase.size !== null;

  const onStageLayout = useCallback((event: LayoutChangeEvent) => {
    const { width, height } = event.nativeEvent.layout;
    setStage({ width, height });
  }, []);

  const tap = useCallback(
    (event: GestureResponderEvent) => {
      if (!canTap || phase.kind !== 'ready' || phase.region === null || phase.size === null || stage === null) {
        return;
      }
      const hit = containFraction(event.nativeEvent.locationX, event.nativeEvent.locationY, stage, phase.size);
      if (hit !== null) setPoint(subjectPointOnPhoto(phase.region, hit.fx, hit.fy));
    },
    [canTap, phase, stage],
  );

  const failureText = (reason: SubjectCutoutFailure): string =>
    point !== null && reason === 'noSubject'
      ? t('subjectCutout.error.noSubjectHere')
      : t(`subjectCutout.error.${reason}`);

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

        <View style={styles.stage} onLayout={onStageLayout}>
          {phase.kind === 'working' && (
            <View style={styles.centered}>
              <ActivityIndicator color={media.text} />
              <Text style={styles.message}>{t('subjectCutout.working')}</Text>
            </View>
          )}
          {phase.kind === 'ready' && (
            <Pressable style={styles.preview} onPress={canTap ? tap : undefined} disabled={!canTap}>
              <Image
                source={{ uri: phase.uri }}
                style={styles.preview}
                resizeMode="contain"
                accessibilityLabel={t('subjectCutout.previewAlt')}
              />
            </Pressable>
          )}
          {phase.kind === 'failed' && (
            <View style={styles.centered}>
              <Text style={styles.message}>{failureText(phase.reason)}</Text>
              {!FINAL_FAILURES.has(phase.reason) && !(point !== null && phase.reason === 'noSubject') && (
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

        {canTap && (
          <Text style={styles.hint}>
            {point === null ? t('subjectCutout.tapHint') : t('subjectCutout.tapAgainHint')}
          </Text>
        )}

        {point !== null && phase.kind !== 'working' && (
          <View style={styles.backRow}>
            <Pressable
              accessibilityRole="button"
              onPress={() => setPoint(null)}
              style={({ pressed }) => [styles.button, pressed && styles.pressed]}
            >
              <Ionicons name="people-outline" size={iconSizes.s} color={media.text} />
              <Text style={styles.buttonText}>{t('subjectCutout.allSubjects')}</Text>
            </Pressable>
          </View>
        )}

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
  hint: {
    ...typography.secondary,
    color: media.textSecondary,
    textAlign: 'center',
    paddingHorizontal: spacing.m,
    paddingBottom: spacing.s,
  },
  backRow: { alignItems: 'center', paddingBottom: spacing.s },
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
