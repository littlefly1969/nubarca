import { useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from '../gallery/PanelShell';
import { TvKeyboardPanel } from '../gallery/TvKeyboardPanel';
import { FilterRow } from './FilterRow';
import { useI18n } from '../../i18n';
import { dateInputToIso, isoToDateInput } from '../../personal/mediaWorkspaceQuery';

// From/To editor for the date-taken range.
//
// The panel used to fold both bounds into one row that cycled through three
// relative presets, and only ever wrote `dateTakenFrom`. `dateTakenTo` was a
// field of the query model with no editor anywhere on the television — it could
// arrive from nothing and go nowhere — and the preset cycle had to guess which
// preset an instant had come from by measuring how many days ago it was, so a
// range entered anywhere else read back as the nearest preset.
//
// Both bounds are editable here as calendar days through the on-screen date
// keyboard, which already existed, was already tested and was already unused:
// nothing had reached TvKeyboardPanel's 'date' mode since the photo gallery was
// retired. The presets stay as one-press ACTIONS rather than a state, because
// an absolute instant cannot honestly claim afterwards to be "the last 30
// days" — what the rows show is always the actual bound.
interface Props {
  from: string; // ISO-8601 UTC instant, '' = unbounded
  to: string;
  onChange: (from: string, to: string) => void;
  onClose: () => void;
}

type Editing = 'none' | 'from' | 'to';
type FocusKey = 'from' | 'to' | 'preset30' | 'preset365' | 'clear' | 'done';

function daysAgoInput(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

export function LibraryPeriodPanel({ from, to, onChange, onClose }: Props) {
  const { t } = useI18n();
  const [editing, setEditing] = useState<Editing>('none');
  // Where the remote is, so BACK out of the date keyboard returns to the exact
  // row that opened it rather than to the top of the panel.
  const focusRef = useRef<FocusKey>('from');

  const fromInput = isoToDateInput(from);
  const toInput = isoToDateInput(to);
  const none = t('filters.none');

  if (editing !== 'none') {
    const isFrom = editing === 'from';
    return (
      <TvKeyboardPanel
        title={isFrom ? t('filters.periodFrom') : t('filters.periodTo')}
        mode="date"
        initialValue={isFrom ? fromInput : toInput}
        onCancel={() => setEditing('none')}
        onSubmit={(value) => {
          const iso = dateInputToIso(value);
          onChange(isFrom ? iso : from, isFrom ? to : iso);
          setEditing('none');
        }}
      />
    );
  }

  const openEditor = (which: 'from' | 'to') => {
    focusRef.current = which;
    setEditing(which);
  };

  const rowA11y = (label: string, value: string) =>
    t('filters.rowA11y', { label, value });

  // Both bounds set the wrong way round still reaches the server, which is the
  // validator — but say so rather than silently returning nothing.
  const inverted = fromInput.length > 0 && toInput.length > 0 && fromInput > toInput;

  return (
    <PanelShell title={t('filters.periodTitle')} onBack={onClose} body="fixed">
      <FilterRow
        label={t('filters.periodFrom')}
        value={fromInput.length > 0 ? fromInput : none}
        active={fromInput.length > 0}
        opensEditor
        accessibilityLabel={rowA11y(t('filters.periodFrom'), fromInput.length > 0 ? fromInput : none)}
        hasTVPreferredFocus={focusRef.current === 'from'}
        onFocus={() => { focusRef.current = 'from'; }}
        onSelect={() => openEditor('from')}
      />
      <FilterRow
        label={t('filters.periodTo')}
        value={toInput.length > 0 ? toInput : none}
        active={toInput.length > 0}
        opensEditor
        accessibilityLabel={rowA11y(t('filters.periodTo'), toInput.length > 0 ? toInput : none)}
        hasTVPreferredFocus={focusRef.current === 'to'}
        onFocus={() => { focusRef.current = 'to'; }}
        onSelect={() => openEditor('to')}
      />

      {inverted && <Text style={styles.warning}>{t('filters.periodInverted')}</Text>}

      <Text style={styles.section}>{t('filters.periodPresets')}</Text>
      <View style={styles.actions}>
        <FocusableButton
          label={t('filters.periodLast30')}
          onPress={() => onChange(dateInputToIso(daysAgoInput(30)), '')}
          onFocusChange={(f) => { if (f) focusRef.current = 'preset30'; }}
        />
        <FocusableButton
          label={t('filters.periodLast365')}
          onPress={() => onChange(dateInputToIso(daysAgoInput(365)), '')}
          onFocusChange={(f) => { if (f) focusRef.current = 'preset365'; }}
        />
        <FocusableButton
          label={t('filters.periodClear')}
          onPress={() => onChange('', '')}
          onFocusChange={(f) => { if (f) focusRef.current = 'clear'; }}
        />
      </View>

      <View style={styles.actions}>
        <FocusableButton
          label={t('gallery.done')}
          onPress={onClose}
          onFocusChange={(f) => { if (f) focusRef.current = 'done'; }}
        />
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  section: {
    color: colors.text,
    fontSize: font.caption,
    fontWeight: '800',
    letterSpacing: 2,
    marginTop: spacing.md,
  },
  actions: {
    flexDirection: 'row',
    gap: spacing.md,
    marginTop: spacing.sm,
    justifyContent: 'center',
    flexWrap: 'wrap',
  },
  warning: { color: colors.danger, fontSize: font.caption, textAlign: 'center' },
});
