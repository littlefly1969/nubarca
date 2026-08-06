interface SelectionBarProps {
  count: number;
  // Number of selected *files* — bulk download only applies to files (there is
  // no archive endpoint for folders), so the button is disabled at 0.
  fileCount: number;
  busy: boolean;
  onDownload(): void;
  onMove(): void;
  onMoveToVault(): void;
  onDelete(): void;
  onClear(): void;
}

// Appears (fixed at the bottom on mobile, inline below the toolbar on desktop)
// whenever at least one item is selected. Bulk actions are intentionally
// limited to the safe set the API already supports per-item: download, move,
// and soft-delete (to Trash). No bulk hard-delete here.
export function SelectionBar({
  count,
  fileCount,
  busy,
  onDownload,
  onMove,
  onMoveToVault,
  onDelete,
  onClear,
}: SelectionBarProps) {
  if (count === 0) return null;
  return (
    <div className="selection-bar" role="region" aria-label="Selection actions">
      <span className="selection-count" aria-live="polite">
        {count} selected
      </span>
      <div className="selection-actions">
        <button
          type="button"
          className="selection-action"
          onClick={onDownload}
          disabled={busy || fileCount === 0}
        >
          Download
        </button>
        <button type="button" className="selection-action" onClick={onMove} disabled={busy}>
          Move
        </button>
        <button
          type="button"
          className="selection-action"
          onClick={onMoveToVault}
          disabled={busy}
          data-testid="selection-move-to-vault"
        >
          Move to Private Vault
        </button>
        <button
          type="button"
          className="selection-action selection-action-destructive"
          onClick={onDelete}
          disabled={busy}
        >
          Delete
        </button>
      </div>
      <button
        type="button"
        className="selection-clear"
        onClick={onClear}
        disabled={busy}
        aria-label="Clear selection"
      >
        ✕
      </button>
    </div>
  );
}
