import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import {
  BackHandler,
  StyleSheet,
  Text,
  View,
  useTVEventHandler,
  type HWEvent,
} from 'react-native';
import { colors, font, spacing } from '../theme';
import { ApiError } from '../api/client';
import { getTvPersonalHome, getTvPersonalStatus, unlockTvPersonal } from '../api/personal';
import { useI18n } from '../i18n';
import type { PersonalHomeInfo } from '../personal/flow';
import {
  DPAD_CODE_LENGTH,
  dpadCodeReducer,
  dpadSymbolForKey,
  EMPTY_DPAD_ENTRY,
  isComplete,
} from '../personal/dpadCode';

interface Props {
  onCancel: () => void;
  onUnlocked: (home: PersonalHomeInfo) => void;
  onSessionInvalid: () => void;
  // Paired session whose owner has no credential at all (legacy/corrupted state
  // — the atomic pairing flow cannot produce it): the app tears down to the
  // pairing screen instead of showing an entry surface that can never succeed.
  onAssociationIncomplete: () => void;
}

// BLIND directional unlock for the Personal Area.
//
// The visible numeric keypad this replaces was the reported security defect:
// masking the entered digits did nothing, because the FOCUS RING travelled from
// key to key and anyone in the room could read the PIN off the television. The
// remedy is structural — this screen has no focusable secret controls at all,
// so there is no focus for a bystander to follow.
//
// What is on screen: a title, a prompt, neutral progress dots, and a STATIC
// remote diagram. The diagram is instructional and never reacts: no arrow
// lights up, nothing scales, nothing changes colour when a direction is
// pressed. Rendering is driven by `entry.code.length` alone — the symbols
// themselves reach no style, no accessibility label and no debug line.
//
// INPUT OWNERSHIP. This screen owns the whole D-pad through useTVEventHandler
// and contains no focusable views, so the native focus engine has nothing to
// move and cannot compete for the same event (VIEWER-style explicit ownership;
// see the mode rules in lib/tvFixedGridFocus.ts).
//
// BACK removes one symbol; BACK on an empty code returns to mode selection.
// Submission is automatic at exactly DPAD_CODE_LENGTH symbols and happens once.
// The code lives only in reducer state: cleared on failure, discarded on
// unmount, never logged, never persisted, never in navigation params.
export function PinEntryScreen({
  onCancel, onUnlocked, onSessionInvalid, onAssociationIncomplete,
}: Props) {
  const { t } = useI18n();
  const [entry, dispatch] = useReducer(dpadCodeReducer, EMPTY_DPAD_ENTRY);
  const [error, setError] = useState<'invalid' | 'throttled' | null>(null);
  // The owner still holds the retired numeric PIN: this app has no numeric
  // entry surface, so it asks them to configure the new code rather than
  // showing a field that can only fail.
  const [upgradeRequired, setUpgradeRequired] = useState(false);

  const entryRef = useRef(entry);
  entryRef.current = entry;
  const upgradeRef = useRef(upgradeRequired);
  upgradeRef.current = upgradeRequired;

  // Invariant check. Two DIFFERENT conditions, deliberately not conflated:
  //   * no credential row at all → the pairing is incomplete (legacy/corrupted
  //     state the atomic pairing flow can no longer produce) → tear down;
  //   * a legacy numeric row → the pairing is FINE and only the credential
  //     needs upgrading → show the notice, never a teardown.
  // Best-effort: on a transient error entry still works (unlock re-checks
  // server-side anyway).
  useEffect(() => {
    let cancelled = false;
    getTvPersonalStatus()
      .then((status) => {
        if (cancelled) return;
        if (!status.pinConfigured) {
          onAssociationIncomplete();
          return;
        }
        setUpgradeRequired(status.scheme === 'pin-v1');
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

  const submit = useCallback((code: string) => {
    dispatch({ type: 'SUBMITTED' });
    unlockTvPersonal(code)
      .then(() => getTvPersonalHome())
      .then((home) => onUnlocked(home))
      .catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) {
          onSessionInvalid();
          return;
        }
        // Generic failure: clear the code, stay locked. 429 = cooldown.
        dispatch({ type: 'RESET' });
        setError(err instanceof ApiError && err.status === 429 ? 'throttled' : 'invalid');
      });
  }, [onUnlocked, onSessionInvalid]);

  // The single D-pad owner on this screen. Only key-DOWN is acted on
  // (eventKeyAction 0 is the press; RN reports the release too), so one press is
  // one symbol. Auto-repeat from a held button is valid input and appends
  // normally — it is never debounced.
  const onTVEvent = useCallback((evt: HWEvent) => {
    if (!evt || evt.eventKeyAction !== 0) return;
    if (upgradeRef.current) return;
    const symbol = dpadSymbolForKey(evt.eventType);
    if (symbol === null) return;
    setError(null);
    const next = dpadCodeReducer(entryRef.current, { type: 'SYMBOL', symbol });
    // Nothing about `symbol` is logged here or anywhere below: a debug line
    // naming the direction would reproduce the exact leak this screen exists to
    // close, in logcat instead of on the screen.
    if (next === entryRef.current) return;
    entryRef.current = next;
    dispatch({ type: 'SYMBOL', symbol });
    if (isComplete(next) && !next.submitting) submit(next.code);
  }, [submit]);
  useTVEventHandler(onTVEvent);

  // BACK precedence: remove the last symbol when any are present, otherwise
  // return to mode selection. Never falls through to the OS default and can
  // never enter the Personal Area.
  useEffect(() => {
    const onBackPress = () => {
      if (entryRef.current.submitting) return true;
      if (!upgradeRef.current && entryRef.current.code.length > 0) {
        entryRef.current = dpadCodeReducer(entryRef.current, { type: 'ERASE' });
        dispatch({ type: 'ERASE' });
      } else {
        onCancel();
      }
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [onCancel]);

  if (upgradeRequired) {
    return (
      <View style={styles.container}>
        <Text style={styles.title}>{t('pin.title')}</Text>
        <Text style={styles.upgrade} accessibilityRole="alert">{t('pin.upgradeRequired')}</Text>
        <Text style={styles.hint}>{t('pin.upgradeBack')}</Text>
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <Text style={styles.title}>{t('pin.title')}</Text>
      <Text style={styles.prompt}>{t('pin.prompt')}</Text>

      {/* Progress dots. The accessibility label is a COUNT, never a symbol: a
          screen reader in the same room must not narrate the secret either. */}
      <View
        style={styles.dots}
        accessible
        accessibilityLabel={t('pin.progress', {
          count: String(entry.code.length),
          total: String(DPAD_CODE_LENGTH),
        })}
      >
        {Array.from({ length: DPAD_CODE_LENGTH }, (_, i) => (
          <View key={i} style={[styles.dot, i < entry.code.length && styles.dotFilled]} />
        ))}
      </View>

      {/* Instructional remote ring. STATIC by contract — it takes no props from
          the entry state, so no press can change any pixel of it. */}
      <View style={styles.ring} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
        <Text style={[styles.ringGlyph, styles.ringUp]}>↑</Text>
        <Text style={[styles.ringGlyph, styles.ringLeft]}>←</Text>
        <Text style={[styles.ringGlyph, styles.ringCenter]}>●</Text>
        <Text style={[styles.ringGlyph, styles.ringRight]}>→</Text>
        <Text style={[styles.ringGlyph, styles.ringDown]}>↓</Text>
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

const RING_CELL = 64;

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
  prompt: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
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
  ring: { width: RING_CELL * 3, height: RING_CELL * 3, marginVertical: spacing.sm },
  ringGlyph: {
    position: 'absolute',
    width: RING_CELL,
    height: RING_CELL,
    lineHeight: RING_CELL,
    textAlign: 'center',
    color: colors.muted,
    fontSize: 34,
  },
  ringUp: { top: 0, left: RING_CELL },
  ringLeft: { top: RING_CELL, left: 0 },
  ringCenter: { top: RING_CELL, left: RING_CELL, color: colors.text },
  ringRight: { top: RING_CELL, left: RING_CELL * 2 },
  ringDown: { top: RING_CELL * 2, left: RING_CELL },
  hint: { color: colors.muted, fontSize: font.caption, marginTop: spacing.xs },
  upgrade: {
    color: colors.text,
    fontSize: font.body,
    textAlign: 'center',
    maxWidth: 900,
    lineHeight: 34,
  },
  error: { color: colors.danger, fontSize: font.body, marginTop: spacing.xs },
});
