import { formatSize } from '../format';
import { FileThumb } from './FileThumb';
import { useLongPress, type ItemViewProps } from './itemView';
import { typeLabel } from './types';

// Grid tile for one entry. A folder shows a folder glyph + name; a file shows
// its small thumbnail/glyph + name + size. The whole tile is the primary
// activation target; a selection checkbox sits in the corner and a details
// button reveals the side panel. Large tap targets, keyboard-activatable.
export function FileItemCard({
  entry,
  selected,
  selectionActive,
  onActivate,
  onToggleSelect,
  onDetails,
  onLongPress,
}: ItemViewProps) {
  const longPress = useLongPress(() => onLongPress(entry));
  const name = entry.kind === 'folder' ? entry.folder.name : entry.file.name;

  return (
    <li
      className={`file-card${selected ? ' is-selected' : ''}${selectionActive ? ' selection-active' : ''}`}
      {...longPress}
    >
      <button
        type="button"
        className={`file-card-select${selected ? ' is-checked' : ''}`}
        role="checkbox"
        aria-checked={selected}
        aria-label={`Select ${name}`}
        onClick={(e) => {
          e.stopPropagation();
          onToggleSelect(entry, e);
        }}
      >
        <span aria-hidden="true">{selected ? '✓' : ''}</span>
      </button>

      <button
        type="button"
        className="file-card-body"
        onClick={(e) => onActivate(entry, e)}
        aria-label={entry.kind === 'folder' ? `Open folder ${name}` : `Open ${name}`}
      >
        <span className="file-card-thumb">
          {entry.kind === 'folder' ? (
            <span className="file-thumb file-thumb-grid file-thumb-folder" aria-hidden="true">📁</span>
          ) : (
            <FileThumb file={entry.file} variant="grid" />
          )}
        </span>
        <span className="file-card-name" title={name}>{name}</span>
        <span className="file-card-meta">
          {entry.kind === 'folder' ? 'Folder' : `${typeLabel(entry.file.mimeType)} · ${formatSize(entry.file.sizeBytes)}`}
        </span>
      </button>

      <button
        type="button"
        className="file-card-details"
        onClick={(e) => {
          e.stopPropagation();
          onDetails(entry);
        }}
        aria-label={`Details for ${name}`}
      >
        <span aria-hidden="true">⋯</span>
      </button>
    </li>
  );
}
