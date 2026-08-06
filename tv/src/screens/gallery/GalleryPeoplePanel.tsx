import { useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, Text } from 'react-native';
import { colors, font, spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import { listPersonalPeople, type TvPersonalPerson } from '../../api/personalGallery';
import type { PeopleFilterState } from '../../personal/galleryQuery';
import { useI18n } from '../../i18n';

// People filter editor (opened from the Filters panel; edits the DRAFT).
// Same underlying person identities and include/exclude + all/any semantics as
// the web people filter, adapted to one focusable row per person: SELECT cycles
// — → Con questa persona → Senza questa persona → —. The include-mode row
// (all/any) appears once two or more people are included, mirroring when the
// distinction matters. TV-safe data only: names + face counts, no face crops,
// boxes, or identifiers beyond the person id.
interface Props {
  people: Record<string, PeopleFilterState>;
  mode: 'all' | 'any';
  onChange: (people: Record<string, PeopleFilterState>, mode: 'all' | 'any') => void;
  onClose: () => void;
  // 401/403 bubble up to the screen's shared auth handling.
  onAuthError: (err: unknown) => boolean;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; list: TvPersonalPerson[] }
  | { kind: 'error' };

export function GalleryPeoplePanel({ people, mode, onChange, onClose, onAuthError }: Props) {
  const { t } = useI18n();
  const [load, setLoad] = useState<LoadState>({ kind: 'loading' });
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoad({ kind: 'loading' });
    listPersonalPeople()
      .then((list) => {
        if (!cancelled) setLoad({ kind: 'ready', list });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        if (onAuthError(err)) return;
        setLoad({ kind: 'error' });
      });
    return () => {
      cancelled = true;
    };
  }, [reloadKey, onAuthError]);

  const cycle = (personId: string) => {
    const current = people[personId];
    const next = { ...people };
    if (current === undefined) next[personId] = 'include';
    else if (current === 'include') next[personId] = 'exclude';
    else delete next[personId];
    onChange(next, mode);
  };

  const includeCount = Object.values(people).filter((s) => s === 'include').length;

  const stateLabel = (personId: string): string => {
    const state = people[personId];
    if (state === 'include') return t('gallery.personInclude');
    if (state === 'exclude') return t('gallery.personExclude');
    return t('gallery.personOff');
  };

  return (
    <PanelShell title={t('gallery.peopleTitle')} onBack={onClose}>
      {load.kind === 'loading' && <ActivityIndicator color={colors.accent} />}
      {load.kind === 'error' && (
        <>
          <Text style={styles.muted}>{t('gallery.peopleLoadError')}</Text>
          <FocusableButton
            label={t('common.tryAgain')}
            onPress={() => setReloadKey((k) => k + 1)}
            hasTVPreferredFocus
          />
        </>
      )}
      {load.kind === 'ready' && load.list.length === 0 && (
        <Text style={styles.muted}>{t('gallery.peopleEmpty')}</Text>
      )}
      {load.kind === 'ready' && includeCount >= 2 && (
        <FocusableButton
          label={mode === 'all' ? t('gallery.peopleModeAll') : t('gallery.peopleModeAny')}
          onPress={() => onChange(people, mode === 'all' ? 'any' : 'all')}
        />
      )}
      {load.kind === 'ready' && load.list.map((person, index) => (
        <FocusableButton
          key={person.id}
          label={`${person.name ?? t('gallery.unnamedPerson')} (${person.faceCount}) · ${stateLabel(person.id)}`}
          onPress={() => cycle(person.id)}
          hasTVPreferredFocus={index === 0}
        />
      ))}
      <FocusableButton label={t('gallery.done')} onPress={onClose} />
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  muted: {
    color: colors.muted,
    fontSize: font.body,
    textAlign: 'center',
    marginVertical: spacing.md,
  },
});
