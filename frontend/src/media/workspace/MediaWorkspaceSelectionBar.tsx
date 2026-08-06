import { useI18n } from '../../i18n';
import { DestinationMenu, type GalleryDestinationAction } from '../../gallery/workspace/DestinationMenu';
import type { MediaSelectionCapabilities } from './mediaSelectionCapabilities';

// Sticky selection bar for the unified workspace. Which actions appear is driven
// entirely by the capability matrix, so a mixed or all-video selection never
// shows photo-only destinations, and Active/Excluded/album context each offer
// exactly the right actions. Renders nothing when the selection is empty.

interface Props {
  count: number;
  busy: boolean;
  capabilities: MediaSelectionCapabilities;
  restoreBusy?: boolean;
  photoDestinations?: GalleryDestinationAction[];
  onAddToAlbum(): void;
  onRemoveFromAlbum(): void;
  onMoveToPersonal(): void;
  onMoveToExcluded(): void;
  onRestore(): void;
  onMoveToTrash(): void;
  onClear(): void;
}

export function MediaWorkspaceSelectionBar({
  count,
  busy,
  capabilities: c,
  restoreBusy = false,
  photoDestinations = [],
  onAddToAlbum,
  onRemoveFromAlbum,
  onMoveToPersonal,
  onMoveToExcluded,
  onRestore,
  onMoveToTrash,
  onClear,
}: Props) {
  const { t, tn } = useI18n();
  if (count === 0) return null;

  return (
    <div
      className="ws-selbar"
      role="region"
      aria-label={tn(count, 'gallerySel.itemsSelected')}
      data-testid="media-selection-bar"
    >
      <span className="ws-selbar-count" data-testid="media-selection-count">
        {tn(count, 'gallerySel.itemsSelected')}
      </span>
      <div className="ws-selbar-actions">
        {c.canRestore && (
          <button
            type="button"
            className="row-action-primary"
            data-testid="media-sel-restore"
            disabled={busy || restoreBusy}
            onClick={onRestore}
          >
            {restoreBusy ? t('moveToExcluded.restoring') : t('moveToExcluded.restore')}
          </button>
        )}
        {c.canAddToAlbum && (
          <button type="button" className="row-action" data-testid="media-sel-album" disabled={busy} onClick={onAddToAlbum}>
            {t('gallerySel.addToAlbum')}
          </button>
        )}
        {c.canRemoveFromCurrentAlbum && (
          <button
            type="button"
            className="row-action"
            data-testid="media-sel-remove-album"
            disabled={busy}
            onClick={onRemoveFromAlbum}
          >
            {t('mediaWs.removeFromAlbum')}
          </button>
        )}
        {c.canMoveToPersonal && (
          <button type="button" className="row-action" data-testid="media-sel-personal" disabled={busy} onClick={onMoveToPersonal}>
            {t('gallery.ws.destPersonal')}
          </button>
        )}
        {c.canMoveToExcluded && (
          <button type="button" className="row-action" data-testid="media-sel-excluded" disabled={busy} onClick={onMoveToExcluded}>
            {t('gallery.ws.destExcluded')}
          </button>
        )}
        {c.canUsePhotoOnlyDestinations && photoDestinations.length > 0 && (
          <DestinationMenu actions={photoDestinations} disabled={busy} menuTestId="media-photo-destinations" />
        )}
        {c.canTrash && (
          <button
            type="button"
            className="row-action-destructive"
            data-testid="media-sel-trash"
            disabled={busy}
            onClick={onMoveToTrash}
          >
            {t('gallerySel.moveToTrash')}
          </button>
        )}
        <button type="button" className="row-action" data-testid="media-sel-clear" disabled={busy} onClick={onClear}>
          {t('gallerySel.clear')}
        </button>
      </div>
    </div>
  );
}
