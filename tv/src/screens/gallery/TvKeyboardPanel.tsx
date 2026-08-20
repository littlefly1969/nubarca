import { useCallback, useState } from 'react';
import { StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import { isValidDateInput } from '../../lib/dateInput';
import { useI18n } from '../../i18n';
import { fixedEditorLayout } from '../../lib/panelLayout';

// Remote-friendly on-screen keyboard: a DPAD character grid (no dependency on
// the flaky Fire TV system keyboard, no tiny text input). Two modes:
//  - 'text': search entry — letters/digits/space, explicit OK submit;
//  - 'date': digits-only 'YYYY-MM-DD' entry (8 digits, progressive format,
//    OK enabled only for a valid calendar date or an EMPTY value = clear).
// BACK deletes the last character first; with nothing left it cancels — the
// spec's "BACK deletes input before closing".
//
// IT DOES NOT SCROLL. A key grid has a known number of rows, so it is SIZED to
// fit the viewport (lib/panelLayout.ts) rather than made scrollable to hide
// that it does not. Inside a scroll the bottom row simply left the screen: a
// focusable the remote could reach and the viewer could not see. PanelShell is
// therefore in 'fixed' body mode here.
interface Props {
  title: string;
  mode: 'text' | 'date';
  initialValue: string; // date mode: 'YYYY-MM-DD' or ''
  onSubmit: (value: string) => void;
  onCancel: () => void;
}

const TEXT_ROWS = [
  'abcdefghij'.split(''),
  'klmnopqrst'.split(''),
  'uvwxyz0123'.split(''),
  '456789.-_!'.split(''),
];
const DATE_ROWS = [
  '123'.split(''),
  '456'.split(''),
  '789'.split(''),
  ['0'],
];
const MAX_TEXT = 64;

function formatDateDigits(digits: string): string {
  const y = digits.slice(0, 4);
  const m = digits.slice(4, 6);
  const d = digits.slice(6, 8);
  let out = y;
  if (digits.length > 4) out += `-${m}`;
  if (digits.length > 6) out += `-${d}`;
  return out;
}

export function TvKeyboardPanel({ title, mode, initialValue, onSubmit, onCancel }: Props) {
  const { t } = useI18n();
  const viewport = useWindowDimensions();
  // Internal value: raw text, or raw digits (date mode).
  const [value, setValue] = useState(
    mode === 'date' ? initialValue.replaceAll('-', '') : initialValue,
  );

  const append = useCallback((ch: string) => {
    setValue((cur) => {
      const max = mode === 'date' ? 8 : MAX_TEXT;
      return cur.length >= max ? cur : cur + ch;
    });
  }, [mode]);

  const deleteLast = useCallback(() => setValue((cur) => cur.slice(0, -1)), []);

  // BACK: delete before closing.
  const onBackOverride = useCallback(() => {
    if (value.length > 0) {
      deleteLast();
      return true;
    }
    return false;
  }, [value.length, deleteLast]);

  const display = mode === 'date' ? formatDateDigits(value) : value;
  const dateComplete = mode === 'date' && value.length === 8;
  const dateValid = dateComplete && isValidDateInput(formatDateDigits(value));
  const canSubmit = mode === 'text'
    || value.length === 0 // empty date = clear the bound
    || dateValid;

  const submit = useCallback(() => {
    if (mode === 'date') {
      onSubmit(value.length === 0 ? '' : formatDateDigits(value));
    } else {
      onSubmit(value.trim());
    }
  }, [mode, value, onSubmit]);

  const rows = mode === 'date' ? DATE_ROWS : TEXT_ROWS;
  // +1 row for the action row below the character grid; the value readout and
  // the (conditional) error line are the header.
  const layout = fixedEditorLayout(viewport, {
    rows: rows.length + 1,
    columns: Math.max(...rows.map((row) => row.length)),
    headerLines: 2,
    actionRows: 0,
  });

  return (
    <PanelShell title={title} onBack={onCancel} onBackOverride={onBackOverride} body="fixed">
      <View style={[styles.valueBox, { minHeight: layout.headerHeight / 2 }]}>
        <Text style={[styles.value, { fontSize: layout.fontSize + 6 }]} numberOfLines={1}>
          {display.length > 0 ? display : mode === 'date' ? t('gallery.dateHint') : ' '}
        </Text>
      </View>
      {mode === 'date' && dateComplete && !dateValid && (
        <Text style={styles.error}>{t('gallery.dateInvalid')}</Text>
      )}
      <View style={[styles.keys, { gap: layout.gap }]}>
        {rows.map((row, rowIndex) => (
          <View key={rowIndex} style={[styles.keyRow, { gap: layout.gap }]}>
            {row.map((ch, colIndex) => (
              <FocusableButton
                key={ch}
                label={ch}
                onPress={() => append(ch)}
                hasTVPreferredFocus={rowIndex === 0 && colIndex === 0}
              />
            ))}
          </View>
        ))}
        <View style={[styles.keyRow, { gap: layout.gap }]}>
          {mode === 'text' && (
            <FocusableButton label={t('gallery.kbSpace')} onPress={() => append(' ')} />
          )}
          <FocusableButton label={t('gallery.kbDelete')} onPress={deleteLast} />
          <FocusableButton label={t('gallery.kbClear')} onPress={() => setValue('')} />
          <FocusableButton label={t('gallery.kbOk')} onPress={submit} disabled={!canSubmit} />
          <FocusableButton label={t('gallery.cancel')} onPress={onCancel} />
        </View>
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  valueBox: {
    alignSelf: 'center',
    minWidth: 420,
    borderRadius: 12,
    backgroundColor: colors.panel,
    paddingVertical: spacing.sm,
    paddingHorizontal: spacing.lg,
    marginBottom: spacing.md,
  },
  value: {
    color: colors.text,
    fontSize: font.heading,
    fontWeight: '700',
    textAlign: 'center',
  },
  error: {
    color: colors.danger,
    fontSize: font.body,
    textAlign: 'center',
    marginBottom: spacing.sm,
  },
  keys: { alignItems: 'center', gap: spacing.sm },
  keyRow: { flexDirection: 'row', gap: spacing.sm, justifyContent: 'center' },
});
