import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from '../gallery/PanelShell';
import { FilterRow } from './FilterRow';
import { listPersonalPeople, type TvPersonalPerson } from '../../api/personalPeople';
import { useI18n } from '../../i18n';
import type { PeopleMode } from '../../personal/mediaWorkspaceQuery';

// Person picker for the library filter panel — the editor the people row had
// been missing.
//
// The row above it could previously only CLEAR a selection, so on a television
// that started with no people filter the whole filter was unreachable: SELECT
// on "Persone · Qualsiasi" wrote the empty selection back over itself and
// nothing happened. The ids had to come from somewhere the remote could not go.
//
// One focusable row per person, SELECT cycling — → include → exclude → —, which
// is the same tri-state the web people filter offers and the same one the
// retired photo gallery's picker offered before the unified library replaced
// it. The ANY/ALL row appears once two people are included, because that is
// exactly when the distinction changes the result; the clear row appears once
// anything is selected. Both feed the query model's existing
// includePeople / excludePeople / includePeopleMode fields — no new contract.
//
// Loading, empty and failure are real navigable states, never a blank panel:
// each one keeps a focusable control so the remote always has somewhere to be,
// and a structural row that disappears hands focus on before it goes.
interface Props {
  include: readonly string[];
  exclude: readonly string[];
  mode: PeopleMode;
  onChange: (include: string[], exclude: string[], mode: PeopleMode) => void;
  onClose: () => void;
  // 401/403 bubble up to the screen's shared pairing/lock handling.
  onAuthError: (err: unknown) => boolean;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; people: TvPersonalPerson[] }
  | { kind: 'error' };

type PersonState = 'off' | 'include' | 'exclude';

// A person id, or one of the three fixed keys below. Person ids are server
// GUIDs, so they cannot collide with them. 'mode' and 'clear' are the two rows
// whose EXISTENCE depends on the current selection, which is why they need
// naming at all: they are the ones that can vanish under the remote.
type FocusKey = string;

export function LibraryPeoplePanel({
  include, exclude, mode, onChange, onClose, onAuthError,
}: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [attempt, setAttempt] = useState(0);
  // Bumped only to force a remount when the focused row is about to vanish, so
  // the preferred-focus request below is honoured on the way back in.
  const [remount, setRemount] = useState(0);

  // Where the remote is. A ref, not state: this list can be long and moving
  // between people must not re-render the panel.
  const focusRef = useRef<FocusKey | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoad({ kind: 'loading' });
    listPersonalPeople()
      .then((people) => {
        if (cancelled) return;
        // A fresh list may not contain the person the remote was on; the render
        // below falls back to the first row rather than to nothing.
        setLoad({ kind: 'ready', people });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (onAuthError(err)) return;
        setLoad({ kind: 'error' });
      });
    return () => { cancelled = true; };
  }, [attempt, onAuthError]);

  const stateOf = useCallback((personId: string): PersonState => {
    if (include.includes(personId)) return 'include';
    if (exclude.includes(personId)) return 'exclude';
    return 'off';
  }, [include, exclude]);

  // Apply a change, transferring focus FIRST when it removes the focused row.
  const commit = useCallback((
    nextInclude: string[], nextExclude: string[], nextMode: PeopleMode, firstPersonId: string | null,
  ) => {
    const focused = focusRef.current;
    const keepsMode = nextInclude.length >= 2;
    const keepsClear = nextInclude.length + nextExclude.length > 0;
    if ((focused === 'mode' && !keepsMode) || (focused === 'clear' && !keepsClear)) {
      focusRef.current = firstPersonId ?? 'done';
      setRemount((k) => k + 1);
    }
    onChange(nextInclude, nextExclude, nextMode);
  }, [onChange]);

  const cyclePerson = useCallback((personId: string, firstPersonId: string | null) => {
    const current = stateOf(personId);
    const nextInclude = include.filter((id) => id !== personId);
    const nextExclude = exclude.filter((id) => id !== personId);
    if (current === 'off') nextInclude.push(personId);
    else if (current === 'include') nextExclude.push(personId);
    commit(nextInclude, nextExclude, mode, firstPersonId);
  }, [stateOf, include, exclude, mode, commit]);

  const personStateLabel = (state: PersonState): string => {
    if (state === 'include') return t('gallery.personInclude');
    if (state === 'exclude') return t('gallery.personExclude');
    return t('gallery.personOff');
  };

  const title = t('gallery.peopleTitle');

  if (load.kind === 'loading') {
    return (
      <PanelShell title={title} onBack={onClose}>
        <View style={styles.stateBox}>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.muted}>{t('filters.peopleLoading')}</Text>
          <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
        </View>
      </PanelShell>
    );
  }

  if (load.kind === 'error') {
    return (
      <PanelShell title={title} onBack={onClose}>
        <View style={styles.stateBox}>
          <Text style={styles.muted}>{t('gallery.peopleLoadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => { focusRef.current = null; setAttempt((a) => a + 1); }}
            hasTVPreferredFocus
          />
          <FocusableButton label={t('filters.back')} onPress={onClose} />
        </View>
      </PanelShell>
    );
  }

  const people = load.people;
  if (people.length === 0) {
    return (
      <PanelShell title={title} onBack={onClose}>
        <View style={styles.stateBox}>
          <Text style={styles.muted}>{t('gallery.peopleEmpty')}</Text>
          <FocusableButton label={t('filters.back')} onPress={onClose} hasTVPreferredFocus />
        </View>
      </PanelShell>
    );
  }

  const firstPersonId = people[0].id;
  const includeCount = include.length;
  const selectedCount = includeCount + exclude.length;
  const showMode = includeCount >= 2;
  const showClear = selectedCount > 0;

  // Deterministic landing spot: the row the remote was on when it still exists,
  // otherwise the first person. Never "no row".
  const wanted = focusRef.current;
  const focusKey: FocusKey =
    wanted === 'mode' && showMode ? 'mode'
      : wanted === 'clear' && showClear ? 'clear'
        : wanted === 'done' ? 'done'
          : wanted !== null && people.some((p) => p.id === wanted) ? wanted
            : firstPersonId;

  return (
    <PanelShell title={title} onBack={onClose}>
      <View key={remount} style={styles.list}>
        <Text style={styles.hint}>
          {selectedCount === 0
            ? t('filters.peopleNone')
            : t('filters.peopleSelected', { count: String(selectedCount) })}
        </Text>

        {showMode && (
          <FilterRow
            label={t('filters.peopleMode')}
            value={mode === 'all' ? t('gallery.peopleModeAll') : t('gallery.peopleModeAny')}
            // 'all' is the default, so the marker means "changed from default",
            // the same thing it means on every other row.
            active={mode === 'any'}
            opensEditor={false}
            accessibilityLabel={t('filters.rowA11y', {
              label: t('filters.peopleMode'),
              value: mode === 'all' ? t('gallery.peopleModeAll') : t('gallery.peopleModeAny'),
            })}
            hasTVPreferredFocus={focusKey === 'mode'}
            onFocus={() => { focusRef.current = 'mode'; }}
            onSelect={() => commit(
              [...include], [...exclude], mode === 'all' ? 'any' : 'all', firstPersonId,
            )}
          />
        )}

        {showClear && (
          <FilterRow
            label={t('filters.peopleClear')}
            value=""
            active={false}
            opensEditor={false}
            accessibilityLabel={t('filters.peopleClear')}
            hasTVPreferredFocus={focusKey === 'clear'}
            onFocus={() => { focusRef.current = 'clear'; }}
            onSelect={() => commit([], [], mode, firstPersonId)}
          />
        )}

        {people.map((person) => {
          const state = stateOf(person.id);
          const name = person.name ?? t('gallery.unnamedPerson');
          const stateText = personStateLabel(state);
          return (
            <FilterRow
              key={person.id}
              label={`${name} (${person.faceCount})`}
              value={stateText}
              active={state !== 'off'}
              opensEditor={false}
              accessibilityLabel={t('filters.rowA11y', { label: name, value: stateText })}
              hasTVPreferredFocus={focusKey === person.id}
              onFocus={() => { focusRef.current = person.id; }}
              onSelect={() => cyclePerson(person.id, firstPersonId)}
            />
          );
        })}

        <View style={styles.actions}>
          <FocusableButton
            label={t('gallery.done')}
            onPress={onClose}
            hasTVPreferredFocus={focusKey === 'done'}
            onFocusChange={(f) => { if (f) focusRef.current = 'done'; }}
          />
        </View>
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  list: { gap: spacing.sm },
  stateBox: { alignItems: 'center', gap: spacing.md, marginTop: spacing.lg },
  muted: { color: colors.muted, fontSize: font.body, textAlign: 'center' },
  hint: { color: colors.muted, fontSize: font.caption, marginBottom: spacing.xs },
  actions: { flexDirection: 'row', justifyContent: 'center', marginTop: spacing.md },
});
