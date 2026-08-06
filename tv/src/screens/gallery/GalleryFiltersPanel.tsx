import { useCallback, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import { CycleRow } from './CycleRow';
import { TvKeyboardPanel } from './TvKeyboardPanel';
import { GalleryPeoplePanel } from './GalleryPeoplePanel';
import {
  emptyFilters,
  peopleIds,
  type GalleryFilters,
} from '../../personal/galleryQuery';
import { useI18n } from '../../i18n';

// Filters panel: every filter the authenticated web gallery has (favorite,
// min rating, GPS presence, capture-date range, duplicate collapsing, people),
// adapted to TV as a vertical list of cycle rows + sub-panels. Edits a DRAFT —
// nothing hits the query until the explicit Apply — and Clear all resets the
// draft to the unfiltered state (Apply then restores the full gallery).
// BACK closes the deepest open sub-panel first (each mounts its own handler),
// then this panel WITHOUT applying (documented: BACK = cancel).
//
// Search (q) is deliberately NOT here: it has its own MENU command + keyboard
// panel, mirroring the web gallery's separate search box.
interface Props {
  filters: GalleryFilters;
  onApply: (next: GalleryFilters) => void;
  onCancel: () => void;
  onAuthError: (err: unknown) => boolean;
}

type SubPanel = 'none' | 'people' | 'dateFrom' | 'dateTo';

export function GalleryFiltersPanel({ filters, onApply, onCancel, onAuthError }: Props) {
  const { t } = useI18n();
  const [draft, setDraft] = useState<GalleryFilters>(filters);
  const [sub, setSub] = useState<SubPanel>('none');

  const cycleFavorite = useCallback(() => setDraft((d) => ({
    ...d,
    favorite: d.favorite === null ? true : d.favorite === true ? false : null,
  })), []);

  const cycleRating = useCallback(() => setDraft((d) => ({
    ...d,
    minRating: d.minRating === null ? 1 : d.minRating >= 5 ? null : d.minRating + 1,
  })), []);

  const cycleGps = useCallback(() => setDraft((d) => ({
    ...d,
    hasGps: d.hasGps === null ? true : d.hasGps === true ? false : null,
  })), []);

  const cycleDuplicates = useCallback(() => setDraft((d) => ({
    ...d,
    collapseDuplicates: !d.collapseDuplicates,
  })), []);

  if (sub === 'people') {
    return (
      <GalleryPeoplePanel
        people={draft.people}
        mode={draft.includePeopleMode}
        onChange={(people, mode) => setDraft((d) => ({ ...d, people, includePeopleMode: mode }))}
        onClose={() => setSub('none')}
        onAuthError={onAuthError}
      />
    );
  }

  if (sub === 'dateFrom' || sub === 'dateTo') {
    const isFrom = sub === 'dateFrom';
    return (
      <TvKeyboardPanel
        title={isFrom ? t('gallery.dateFrom') : t('gallery.dateTo')}
        mode="date"
        initialValue={isFrom ? draft.dateFrom : draft.dateTo}
        onSubmit={(value) => {
          setDraft((d) => (isFrom ? { ...d, dateFrom: value } : { ...d, dateTo: value }));
          setSub('none');
        }}
        onCancel={() => setSub('none')}
      />
    );
  }

  const favoriteLabel = draft.favorite === null
    ? t('gallery.any')
    : draft.favorite ? t('gallery.favOnly') : t('gallery.favNot');
  const ratingLabel = draft.minRating === null ? t('gallery.any') : `★ ${draft.minRating}+`;
  const gpsLabel = draft.hasGps === null
    ? t('gallery.any')
    : draft.hasGps ? t('gallery.gpsWith') : t('gallery.gpsWithout');
  const personCount = peopleIds(draft, 'include').length + peopleIds(draft, 'exclude').length;
  const peopleLabel = personCount === 0
    ? t('gallery.peopleNone')
    : t('gallery.peopleCount', { count: String(personCount) });

  return (
    <PanelShell title={t('gallery.filters')} onBack={onCancel}>
      <CycleRow
        label={t('gallery.favorite')}
        value={favoriteLabel}
        onCycle={cycleFavorite}
        hasTVPreferredFocus
      />
      <CycleRow label={t('gallery.minRating')} value={ratingLabel} onCycle={cycleRating} />
      <CycleRow label={t('gallery.gps')} value={gpsLabel} onCycle={cycleGps} />
      <CycleRow
        label={t('gallery.dateFrom')}
        value={draft.dateFrom !== '' ? draft.dateFrom : t('gallery.dateNone')}
        onCycle={() => setSub('dateFrom')}
      />
      <CycleRow
        label={t('gallery.dateTo')}
        value={draft.dateTo !== '' ? draft.dateTo : t('gallery.dateNone')}
        onCycle={() => setSub('dateTo')}
      />
      <CycleRow
        label={t('gallery.hideDuplicates')}
        value={draft.collapseDuplicates ? t('gallery.on') : t('gallery.off')}
        onCycle={cycleDuplicates}
      />
      <CycleRow label={t('gallery.people')} value={peopleLabel} onCycle={() => setSub('people')} />
      <View style={styles.actions}>
        <FocusableButton label={t('gallery.apply')} onPress={() => onApply(draft)} />
        <FocusableButton
          label={t('gallery.clearAll')}
          // Preserve the SUBMITTED search: Clear filters on the web clears the
          // compact filters, not the search box. q is owned by the Search panel.
          onPress={() => setDraft((d) => ({ ...emptyFilters, q: d.q }))}
        />
        <FocusableButton label={t('gallery.cancel')} onPress={onCancel} />
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  actions: {
    flexDirection: 'row',
    justifyContent: 'center',
    gap: spacing.md,
    marginTop: spacing.lg,
  },
});
