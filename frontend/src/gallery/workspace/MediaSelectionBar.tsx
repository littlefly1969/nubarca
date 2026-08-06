import { useI18n } from '../../i18n';
import { DestinationMenu, type GalleryDestinationAction } from './DestinationMenu';

// Sticky selection bar shared by the photo and video galleries.
//
// Formerly GallerySelectionBar: the behaviour is unchanged, but the wording is
// now medium-neutral ("3 items selected", never "3 photos selected") because
// the video gallery renders the exact same bar. Renders nothing when the
// selection is empty.
//
// `destinations` is the existing extensible "Add to…" menu. `moveDestinations`
// is a second, semantically distinct "Move to…" menu — Personal now, Excluded
// in a later slice — plugged in the same way as ordinary actions; nothing
// beyond that generalisation is built ahead of the need.
interface Props {
  count: number;
  busy: boolean;
  destinations: GalleryDestinationAction[];
  moveDestinations?: GalleryDestinationAction[];
  onAddToAlbum(): void;
  onMoveToTrash(): void;
  onClear(): void;
  // Slice 3: the "Esclusi" tab passes onRestore to show a prominent
  // "Restore to library" primary action (absent on the normal Active tab).
  onRestore?(): void;
  restoreBusy?: boolean;
}

export function MediaSelectionBar({
  count,
  busy,
  destinations,
  moveDestinations = [],
  onAddToAlbum,
  onMoveToTrash,
  onClear,
  onRestore,
  restoreBusy = false,
}: Props) {
  const { t, tn } = useI18n();
  if (count === 0) return null;

  return (
    <div
      className="ws-selbar"
      role="region"
      aria-label={tn(count, 'gallerySel.itemsSelected')}
      data-testid="ws-selection-bar"
    >
      <span className="ws-selbar-count" data-testid="ws-selection-count">
        {tn(count, 'gallerySel.itemsSelected')}
      </span>
      <div className="ws-selbar-actions">
        {onRestore && (
          <button
            type="button"
            className="row-action-primary"
            data-testid="ws-sel-restore"
            disabled={busy || restoreBusy}
            onClick={onRestore}
          >
            {restoreBusy ? t('moveToExcluded.restoring') : t('moveToExcluded.restore')}
          </button>
        )}
        <button
          type="button"
          className="row-action"
          data-testid="ws-sel-album"
          disabled={busy}
          onClick={onAddToAlbum}
        >
          {t('gallerySel.addToAlbum')}
        </button>
        <DestinationMenu actions={destinations} disabled={busy} />
        <DestinationMenu
          actions={moveDestinations}
          disabled={busy}
          label={t('gallery.ws.moveTo')}
          ariaLabel={t('gallery.ws.moveToAria')}
          menuTestId="ws-move-to"
          itemTestIdPrefix="ws-move-dest-"
        />
        <button
          type="button"
          className="row-action-destructive"
          data-testid="ws-sel-trash"
          disabled={busy}
          onClick={onMoveToTrash}
        >
          {t('gallerySel.moveToTrash')}
        </button>
        <button
          type="button"
          className="row-action"
          data-testid="ws-sel-clear"
          disabled={busy}
          onClick={onClear}
        >
          {t('gallerySel.clear')}
        </button>
      </div>
    </div>
  );
}
