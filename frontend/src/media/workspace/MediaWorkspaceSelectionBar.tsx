import { Icon } from '../../components/icons/Icon';
import { useI18n } from '../../i18n';
import { actionTestId, MediaSelectionMenu } from './MediaSelectionMenu';
import type { MediaSelectionActionId, MediaSelectionActionModel } from './mediaSelectionActions';

// The contextual command dock: what you can do with the media you just picked.
//
// Not a toolbar. It floats over the wall, states how much is selected, and
// offers TWO grouped commands organised by what they mean —
//
//   Move to …  the media changes state or place (Personal, Excluded, Trash)
//   Add to  …  the media stays put and gains an association (Album, Plates,
//              Aesthetics)
//
// — plus whatever the current context makes a first-class action (Restore out of
// Excluded, Remove from THIS album). Every entry comes from the pure action
// model, so a mixed selection never sees a photo-only destination and a user
// without private-vault.access never sees Personal.
//
// Explicit selection mode keeps the dock available even at zero selected items.

interface Props {
  active?: boolean;
  count: number;
  busy: boolean;
  actions: MediaSelectionActionModel;
  restoreBusy?: boolean;
  onAction(id: MediaSelectionActionId): void;
  onClear(): void;
  // How many items the current wall has LOADED, for the explicit "select the
  // loaded items" command (Ctrl/Cmd+A). Absent → the command is not offered.
  loadedCount?: number;
  onSelectLoaded?(): void;
  // Opens the tray listing what is selected. Absent → the count is plain text.
  onOpenTray?(): void;
}

export function MediaWorkspaceSelectionBar({
  count, busy, actions, restoreBusy = false, onAction, onClear, active = count > 0,
  loadedCount, onSelectLoaded, onOpenTray,
}: Props) {
  const { t, tn } = useI18n();
  if (!active) return null;

  const label = tn(count, 'gallerySel.itemsSelected');

  return (
    <div
      className="ws-dock"
      role="region"
      aria-label={label}
      data-testid="media-selection-bar"
    >
      {onOpenTray ? (
        <button type="button" className="ws-dock-count is-button" data-testid="media-selection-count"
          aria-label={`${label} — ${t('gallerySel.trayOpen')}`} aria-haspopup="dialog" onClick={onOpenTray}>
          <Icon name="check" size={15} />
          <span className="ws-dock-count-number" aria-hidden="true">{count}</span>
          <span className="ws-dock-count-text">{label}</span>
        </button>
      ) : (
        <span className="ws-dock-count" data-testid="media-selection-count">
          <Icon name="check" size={15} />
          <span className="ws-dock-count-number" aria-hidden="true">{count}</span>
          <span className="ws-dock-count-text">{label}</span>
        </span>
      )}

      {onSelectLoaded && loadedCount !== undefined && loadedCount > 0 && (
        <button type="button" className="ws-dock-button is-contextual" data-testid="media-sel-select-loaded"
          title={t('gallerySel.selectLoadedHint')} disabled={busy} onClick={onSelectLoaded}>
          <Icon name="check" size={16} />
          <span className="ws-dock-label">{tn(loadedCount, 'gallerySel.selectLoaded')}</span>
        </button>
      )}

      <div className="ws-dock-commands">
        {actions.contextual.map((a) => {
          // Restore is the one contextual action with its own in-flight state:
          // it runs straight from the dock rather than through a dialog.
          const pending = a.id === 'restore' && restoreBusy;
          return (
            <button
              key={a.id}
              type="button"
              className="ws-dock-button is-contextual"
              data-testid={actionTestId(a.id)}
              disabled={busy || pending}
              onClick={() => onAction(a.id)}
            >
              <Icon name={a.icon} size={16} />
              <span className="ws-dock-label">
                {pending ? t('moveToExcluded.restoring') : t(a.labelKey)}
              </span>
            </button>
          );
        })}

        <MediaSelectionMenu
          label={t('gallery.ws.moveTo')}
          ariaLabel={t('gallery.ws.moveToAria')}
          icon="move"
          actions={actions.moveTo}
          disabled={busy}
          testId="media-sel-move-to"
          onSelect={onAction}
        />

        <MediaSelectionMenu
          label={t('gallery.ws.addTo')}
          ariaLabel={t('gallery.ws.addToAria')}
          icon="add"
          actions={actions.addTo}
          disabled={busy}
          testId="media-sel-add-to"
          onSelect={onAction}
        />
      </div>

      <button
        type="button"
        className="ws-dock-close"
        data-testid="media-sel-clear"
        aria-label={t('gallerySel.clear')}
        disabled={busy}
        onClick={onClear}
      >
        <Icon name="close" size={16} />
      </button>
    </div>
  );
}
