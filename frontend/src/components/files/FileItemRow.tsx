import { formatDate, formatSize } from '../format';
import { FileThumb } from './FileThumb';
import { useLongPress, type ItemViewProps } from './itemView';
import { typeLabel } from './types';

// Denser list row for one entry. Same affordances as the grid card (select,
// activate, details) laid out in columns: icon, name, type, size, date.
export function FileItemRow({
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
  const createdAt = entry.kind === 'folder' ? entry.folder.createdAt : entry.file.createdAt;

  return (
    <li
      className={`file-list-row${selected ? ' is-selected' : ''}${selectionActive ? ' selection-active' : ''}`}
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
        className="file-list-main"
        onClick={(e) => onActivate(entry, e)}
        aria-label={entry.kind === 'folder' ? `Open folder ${name}` : `Open ${name}`}
      >
        <span className="file-list-thumb">
          {entry.kind === 'folder' ? (
            <span className="file-thumb file-thumb-list file-thumb-folder" aria-hidden="true">📁</span>
          ) : (
            <FileThumb file={entry.file} variant="list" />
          )}
        </span>
        <span className="file-list-name" title={name}>{name}</span>
      </button>

      <span className="file-list-type" aria-hidden={entry.kind === 'folder'}>
        {entry.kind === 'folder' ? '' : typeLabel(entry.file.mimeType)}
      </span>
      <span className="file-list-size">
        {entry.kind === 'folder' ? '—' : formatSize(entry.file.sizeBytes)}
      </span>
      <span className="file-list-date">{formatDate(createdAt)}</span>

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
