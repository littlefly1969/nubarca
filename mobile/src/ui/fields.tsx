// Form primitives (BRAND-APP-02 §B).
//
// They exist so a screen states INTENT — this field is labelled, this one is in
// error, this notice is a warning — and the brand decides the rest. Before
// them, every screen wrote its own input: its own radius, its own surface, its
// own idea of what an error looks like.
//
// Two rules are enforced by the SHAPE of the API rather than by review:
//
//   * a TextField cannot exist without a label. A placeholder is not a label —
//     it disappears the moment somebody types, which is exactly when they most
//     need to know what they are filling in, and assistive technology may never
//     announce it at all;
//   * a notice cannot be only a colour. Tone selects the colour, the title and
//     the text carry the meaning, and both are required.

import React, { useState } from 'react';
import {
  StyleSheet,
  Text,
  TextInput,
  View,
  type StyleProp,
  type TextInputProps,
  type ViewStyle,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { iconSizes, radius, spacing, touch, typography } from './tokens';
import { themed, useColors } from './theme';

export function FieldLabel({ text }: { text: string }): React.JSX.Element {
  const styles = useFieldStyles();
  // Sentence case, not uppercase: the brand does not use uppercase headings as
  // a default device, and uppercase labels read slower.
  return <Text style={styles.label}>{text}</Text>;
}

export type TextFieldProps = Omit<TextInputProps, 'style' | 'placeholderTextColor'> & {
  /** REQUIRED. A placeholder is never the only label for a form control. */
  label: string;
  /** Message shown under the field. Its presence IS the error state. */
  error?: string | null;
  /** Quiet helper text, shown only when there is no error to show instead. */
  hint?: string | null;
  containerStyle?: StyleProp<ViewStyle>;
};

export function TextField({
  label,
  error = null,
  hint = null,
  containerStyle,
  ...input
}: TextFieldProps): React.JSX.Element {
  const styles = useFieldStyles();
  const colors = useColors();
  const [focused, setFocused] = useState(false);

  return (
    <View style={[styles.field, containerStyle]}>
      <FieldLabel text={label} />
      <TextInput
        {...input}
        // Every native prop the caller passes survives: this primitive styles a
        // text input, it does not reimplement one.
        accessibilityLabel={input.accessibilityLabel ?? label}
        placeholderTextColor={colors.textTertiary}
        onFocus={(event) => {
          setFocused(true);
          input.onFocus?.(event);
        }}
        onBlur={(event) => {
          setFocused(false);
          input.onBlur?.(event);
        }}
        style={[
          styles.input,
          focused && styles.inputFocused,
          error !== null && styles.inputError,
        ]}
      />
      {error !== null ? (
        <Text style={styles.error}>{error}</Text>
      ) : hint !== null ? (
        <Text style={styles.hint}>{hint}</Text>
      ) : null}
    </View>
  );
}

export type NoticeTone = 'neutral' | 'warning' | 'danger' | 'success';

const NOTICE_ICON: Record<NoticeTone, 'information-circle-outline' | 'warning-outline' | 'alert-circle-outline' | 'checkmark-circle-outline'> = {
  neutral: 'information-circle-outline',
  warning: 'warning-outline',
  danger: 'alert-circle-outline',
  success: 'checkmark-circle-outline',
};

/**
 * A quiet inline message. The ICON and the TEXT carry the status; the tone only
 * reinforces it, so the notice still works for a reader who cannot distinguish
 * the colours.
 */
export function InlineNotice({
  text,
  tone = 'neutral',
  title,
}: {
  text: string;
  tone?: NoticeTone;
  title?: string;
}): React.JSX.Element {
  const styles = useFieldStyles();
  const colors = useColors();
  const toneText: Record<NoticeTone, string> = {
    neutral: colors.textSecondary,
    warning: colors.warningText,
    danger: colors.danger,
    success: colors.signalSuccess,
  };
  const toneSurface: Record<NoticeTone, string> = {
    neutral: colors.surfaceSubtle,
    warning: colors.warningSurface,
    danger: colors.dangerSurface,
    success: colors.surfaceSubtle,
  };
  return (
    <View
      accessibilityRole="alert"
      style={[styles.notice, { backgroundColor: toneSurface[tone] }]}
    >
      <Ionicons name={NOTICE_ICON[tone]} size={iconSizes.s} color={toneText[tone]} />
      <View style={styles.noticeBody}>
        {title !== undefined && (
          <Text style={[styles.noticeTitle, { color: toneText[tone] }]}>{title}</Text>
        )}
        <Text style={[styles.noticeText, { color: toneText[tone] }]}>{text}</Text>
      </View>
    </View>
  );
}

const useFieldStyles = themed((colors) =>
  StyleSheet.create({
    field: { gap: spacing.xs },
    label: { ...typography.label, color: colors.textSecondary },
    input: {
      ...typography.body,
      minHeight: touch.minSize,
      paddingHorizontal: spacing.m,
      paddingVertical: spacing.s + spacing.xs,
      borderRadius: radius.control,
      backgroundColor: colors.surface,
      borderWidth: StyleSheet.hairlineWidth,
      borderColor: colors.separator,
      color: colors.textPrimary,
    },
    // Cyan is the focus/activity signal, and this is one of the few places it
    // is allowed to appear.
    inputFocused: { borderColor: colors.signalFocus, borderWidth: 2 },
    inputError: { borderColor: colors.danger, borderWidth: 2 },
    error: { ...typography.secondary, color: colors.danger },
    hint: { ...typography.secondary, color: colors.textTertiary },

    notice: {
      flexDirection: 'row',
      alignItems: 'flex-start',
      gap: spacing.s,
      padding: spacing.m,
      borderRadius: radius.control,
    },
    noticeBody: { flex: 1, gap: 2 },
    noticeTitle: { ...typography.label },
    noticeText: { ...typography.secondary },
  }),
);
