import { useCallback, useEffect, useRef, useState } from 'react';
import { BackHandler, Pressable, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../theme';
import { ApiError } from '../api/client';
import { getTvPersonalHome, getTvPersonalStatus, unlockTvPersonal } from '../api/personal';
import { useI18n } from '../i18n';
import type { PersonalHomeInfo } from '../personal/flow';

const PIN_LENGTH = 6;

// Keypad rows (DPAD grid): 1-2-3 / 4-5-6 / 7-8-9 / ⌫-0-←. LEFT/RIGHT move
// within a row, UP/DOWN between rows (native spatial focus handles this — the
// grid is regular, with no hidden or decorative focusables). SELECT enters the
// focused digit; focus STAYS on the pressed key after entry, after delete, and
// after a failed unlock (the keypad never remounts), so it is always exactly
// where the user left it. Initial focus is EXPLICITLY on "1".
const KEYS: ReadonlyArray<ReadonlyArray<string>> = [
  ['1', '2', '3'],
  ['4', '5', '6'],
  ['7', '8', '9'],
  ['del', '0', 'back'],
];

interface Props {
  onCancel: () => void;
  onUnlocked: (home: PersonalHomeInfo) => void;
  onSessionInvalid: () => void;
  // Paired session whose owner has no PIN (legacy/corrupted state — the atomic
  // pairing flow cannot produce it): the app tears down to the pairing screen
  // instead of showing a keypad that can never succeed.
  onAssociationIncomplete: () => void;
}

// 6-digit PIN entry. The digits live only in local state: cleared on failure,
// discarded on unmount, never logged, never persisted, never in navigation
// params. Submission is automatic at exactly 6 digits. BACK deletes the last
// digit; BACK with no digits returns to mode selection. MENU is unused here —
// there is no overlay to conflict with and it can never bypass entry.
export function PinEntryScreen({
  onCancel, onUnlocked, onSessionInvalid, onAssociationIncomplete,
}: Props) {
  const { t } = useI18n();
  const [digits, setDigits] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<'invalid' | 'throttled' | null>(null);
  const digitsRef = useRef(digits);
  digitsRef.current = digits;
  const busyRef = useRef(busy);
  busyRef.current = busy;

  // Invariant check: atomic pairing guarantees the owner has a PIN, so
  // pinConfigured=false means legacy/corrupted state → incomplete-association
  // recovery, never a keypad that can never succeed. Best-effort — on a
  // transient error the keypad works (unlock re-checks server-side anyway).
  useEffect(() => {
    let cancelled = false;
    getTvPersonalStatus()
      .then((status) => {
        if (cancelled) return;
        if (!status.pinConfigured) onAssociationIncomplete();
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onSessionInvalid();
        }
      });
    return () => {
      cancelled = true;
    };
  }, [onSessionInvalid, onAssociationIncomplete]);

  const submit = useCallback((pin: string) => {
    setBusy(true);
    unlockTvPersonal(pin)
      .then(() => getTvPersonalHome())
      .then((home) => onUnlocked(home))
      .catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) {
          onSessionInvalid();
          return;
        }
        // Generic failure: clear the digits, stay locked. 429 = cooldown.
        setDigits('');
        setError(err instanceof ApiError && err.status === 429 ? 'throttled' : 'invalid');
        setBusy(false);
      });
  }, [onUnlocked, onSessionInvalid]);

  const addDigit = useCallback((d: string) => {
    if (busyRef.current) return;
    setError(null);
    setDigits((cur) => {
      const next = (cur + d).slice(0, PIN_LENGTH);
      if (next.length === PIN_LENGTH) submit(next);
      return next;
    });
  }, [submit]);

  const deleteDigit = useCallback(() => {
    if (busyRef.current) return;
    setDigits((cur) => cur.slice(0, -1));
  }, []);

  // BACK precedence: delete the last digit when any digits are present,
  // otherwise return to mode selection. Never falls through to the OS default
  // (which would exit the app) and can never enter personal mode.
  useEffect(() => {
    const onBackPress = () => {
      if (busyRef.current) return true;
      if (digitsRef.current.length > 0) deleteDigit();
      else onCancel();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [deleteDigit, onCancel]);

  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('pin.title')}</Text>
      <View style={styles.dots} accessible accessibilityLabel={t('pin.title')}>
        {Array.from({ length: PIN_LENGTH }, (_, i) => (
          <View key={i} style={[styles.dot, i < digits.length && styles.dotFilled]} />
        ))}
      </View>
      <View style={styles.keypad}>
        {KEYS.map((row) => (
          <View key={row.join('')} style={styles.keypadRow}>
            {row.map((key) => (key === 'del' ? (
              <KeypadKey
                key={key}
                label="⌫"
                accessibilityLabel={t('pin.delete')}
                disabled={busy}
                onPress={deleteDigit}
              />
            ) : key === 'back' ? (
              <KeypadKey
                key={key}
                label="←"
                accessibilityLabel={t('pin.back')}
                disabled={busy}
                onPress={onCancel}
              />
            ) : (
              <KeypadKey
                key={key}
                label={key}
                disabled={busy}
                preferFocus={key === '1'}
                onPress={() => addDigit(key)}
              />
            )))}
          </View>
        ))}
      </View>
      <Text style={styles.hint}>{t('pin.hint')}</Text>
      {error !== null && (
        <Text style={styles.error} accessibilityRole="alert">
          {error === 'throttled' ? t('pin.throttled') : t('pin.error')}
        </Text>
      )}
    </View>
  );
}

// One keypad key with the app's standard non-color-only focus treatment
// (double ring + scale, matching FocusableButton) in a square TV-sized hit box.
function KeypadKey({
  label,
  accessibilityLabel,
  onPress,
  disabled = false,
  preferFocus = false,
}: {
  label: string;
  accessibilityLabel?: string;
  onPress: () => void;
  disabled?: boolean;
  preferFocus?: boolean;
}) {
  const [focused, setFocused] = useState(false);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      focusable={!disabled}
      disabled={disabled}
      hasTVPreferredFocus={preferFocus}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      onPress={onPress}
      style={[
        keyStyles.outer,
        disabled && keyStyles.disabled,
        focused && keyStyles.outerFocused,
      ]}
    >
      <View style={[keyStyles.inner, focused && keyStyles.innerFocused]}>
        <Text style={[keyStyles.label, focused && keyStyles.labelFocused]}>{label}</Text>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg,
    padding: spacing.xl,
    gap: spacing.md,
  },
  title: { color: colors.text, fontSize: font.heading, fontWeight: '700', textAlign: 'center' },
  dots: { flexDirection: 'row', gap: spacing.sm, marginVertical: spacing.sm },
  dot: {
    width: 22,
    height: 22,
    borderRadius: 11,
    borderWidth: 2,
    borderColor: colors.muted,
    backgroundColor: 'transparent',
  },
  dotFilled: { backgroundColor: colors.text, borderColor: colors.text },
  keypad: { gap: spacing.sm },
  keypadRow: { flexDirection: 'row', gap: spacing.sm, justifyContent: 'center' },
  hint: { color: colors.muted, fontSize: font.caption, marginTop: spacing.xs },
  error: { color: colors.danger, fontSize: font.body, marginTop: spacing.xs },
});

const keyStyles = StyleSheet.create({
  outer: {
    width: 96,
    height: 76,
    borderRadius: 12,
    borderWidth: 3,
    borderColor: 'transparent',
    backgroundColor: colors.panel,
  },
  outerFocused: {
    borderColor: '#ffffff',
    backgroundColor: colors.panelFocused,
    transform: [{ scale: 1.08 }],
  },
  inner: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 8,
    borderWidth: 2,
    borderColor: 'transparent',
  },
  innerFocused: { borderColor: colors.accent },
  disabled: { opacity: 0.35 },
  label: { color: colors.text, fontSize: 30, fontWeight: '600' },
  labelFocused: { fontWeight: '800' },
});
