import { Breadcrumb, type BreadcrumbEntry } from '../Breadcrumb';
import type { DirectorySortField, SortDirection, ViewMode } from './types';

interface FilesToolbarProps {
  trail: BreadcrumbEntry[];
  onNavigate(index: number): void;
  viewMode: ViewMode;
  onViewModeChange(mode: ViewMode): void;
  sort: DirectorySortField;
  direction: SortDirection;
  onSortChange(sort: DirectorySortField, direction: SortDirection): void;
  newFolderOpen: boolean;
  onToggleNewFolder(): void;
  uploadOpen: boolean;
  onToggleUpload(): void;
  onOrganize(): void;
  onRefresh(): void;
  busy: boolean;
}

const SORT_OPTIONS: { value: DirectorySortField; label: string }[] = [
  { value: 'name', label: 'Name' },
  { value: 'created', label: 'Date added' },
  { value: 'size', label: 'Size' },
  { value: 'type', label: 'Type' },
];

// Top toolbar: breadcrumb on the left, actions on the right. The sort control
// is a native <select> + a direction toggle so it works with keyboard and
// touch without a custom popover. The view toggle is a two-button group with
// aria-pressed so screen readers announce the active mode.
export function FilesToolbar({
  trail,
  onNavigate,
  viewMode,
  onViewModeChange,
  sort,
  direction,
  onSortChange,
  newFolderOpen,
  onToggleNewFolder,
  uploadOpen,
  onToggleUpload,
  onOrganize,
  onRefresh,
  busy,
}: FilesToolbarProps) {
  return (
    <div className="files-toolbar">
      <div className="files-toolbar-path">
        <Breadcrumb trail={trail} onNavigate={onNavigate} disabled={busy} />
      </div>

      <div className="files-toolbar-actions">
        <button
          type="button"
          className={`files-action${newFolderOpen ? ' is-active' : ''}`}
          onClick={onToggleNewFolder}
          aria-expanded={newFolderOpen}
          aria-label="New folder"
        >
          <span aria-hidden="true">📁＋</span>
          <span className="files-action-label">New folder</span>
        </button>

        <button
          type="button"
          className={`files-action${uploadOpen ? ' is-active' : ''}`}
          onClick={onToggleUpload}
          aria-expanded={uploadOpen}
          aria-label="Upload"
        >
          <span aria-hidden="true">⬆</span>
          <span className="files-action-label">Upload</span>
        </button>

        <button
          type="button"
          className="files-action"
          onClick={onOrganize}
          aria-label="Organize photos by date"
        >
          <span aria-hidden="true">🗓</span>
          <span className="files-action-label">Organize by date</span>
        </button>

        <span className="files-sort">
          <select
            className="files-sort-select"
            value={sort}
            disabled={busy}
            onChange={(e) => onSortChange(e.target.value as DirectorySortField, direction)}
            aria-label="Sort by"
          >
            {SORT_OPTIONS.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </span>

        <button
          type="button"
          className="files-action files-sort-direction"
          onClick={() => onSortChange(sort, direction === 'asc' ? 'desc' : 'asc')}
          disabled={busy}
          aria-label={direction === 'asc' ? 'Sort ascending (toggle to descending)' : 'Sort descending (toggle to ascending)'}
          title={direction === 'asc' ? 'Ascending' : 'Descending'}
        >
          <span aria-hidden="true">{direction === 'asc' ? '↑' : '↓'}</span>
        </button>

        <div className="files-view-toggle" role="group" aria-label="View mode">
          <button
            type="button"
            className={`files-view-button${viewMode === 'grid' ? ' is-active' : ''}`}
            aria-pressed={viewMode === 'grid'}
            aria-label="Grid view"
            onClick={() => onViewModeChange('grid')}
          >
            <span aria-hidden="true">▦</span>
          </button>
          <button
            type="button"
            className={`files-view-button${viewMode === 'list' ? ' is-active' : ''}`}
            aria-pressed={viewMode === 'list'}
            aria-label="List view"
            onClick={() => onViewModeChange('list')}
          >
            <span aria-hidden="true">☰</span>
          </button>
        </div>

        <button
          type="button"
          className="files-action"
          onClick={onRefresh}
          disabled={busy}
          aria-label="Refresh"
        >
          <span aria-hidden="true">⟳</span>
        </button>
      </div>
    </div>
  );
}
