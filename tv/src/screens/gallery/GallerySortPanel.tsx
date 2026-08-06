import { StyleSheet, View } from 'react-native';
import { spacing } from '../../theme';
import { FocusableButton } from '../../components/FocusableButton';
import { PanelShell } from './PanelShell';
import type { GallerySort } from '../../personal/galleryQuery';
import { useI18n, type TvMessageKey } from '../../i18n';

// Sort panel: the web gallery's four sort fields × two directions as one
// explicit list (server-driven ordering, deterministic id tie-break). SELECT
// applies immediately and closes; changing the sort never touches the filters.
interface Props {
  sort: GallerySort;
  onSelect: (sort: GallerySort) => void;
  onCancel: () => void;
}

const OPTIONS: ReadonlyArray<{ sort: GallerySort; labelKey: TvMessageKey }> = [
  { sort: { field: 'created', direction: 'desc' }, labelKey: 'gallery.sortCreatedDesc' },
  { sort: { field: 'created', direction: 'asc' }, labelKey: 'gallery.sortCreatedAsc' },
  { sort: { field: 'datetaken', direction: 'desc' }, labelKey: 'gallery.sortDateTakenDesc' },
  { sort: { field: 'datetaken', direction: 'asc' }, labelKey: 'gallery.sortDateTakenAsc' },
  { sort: { field: 'name', direction: 'asc' }, labelKey: 'gallery.sortNameAsc' },
  { sort: { field: 'name', direction: 'desc' }, labelKey: 'gallery.sortNameDesc' },
  { sort: { field: 'size', direction: 'desc' }, labelKey: 'gallery.sortSizeDesc' },
  { sort: { field: 'size', direction: 'asc' }, labelKey: 'gallery.sortSizeAsc' },
];

export function GallerySortPanel({ sort, onSelect, onCancel }: Props) {
  const { t } = useI18n();
  return (
    <PanelShell title={t('gallery.sortTitle')} onBack={onCancel}>
      <View style={styles.list}>
        {OPTIONS.map((option) => {
          const active = option.sort.field === sort.field
            && option.sort.direction === sort.direction;
          return (
            <FocusableButton
              key={`${option.sort.field}-${option.sort.direction}`}
              label={`${active ? '✓ ' : ''}${t(option.labelKey)}`}
              onPress={() => onSelect(option.sort)}
              hasTVPreferredFocus={active}
            />
          );
        })}
        <FocusableButton label={t('gallery.cancel')} onPress={onCancel} />
      </View>
    </PanelShell>
  );
}

const styles = StyleSheet.create({
  list: { alignItems: 'center', gap: spacing.sm },
});
