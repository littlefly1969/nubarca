import { useState } from 'react';
import type { KeyboardEvent } from 'react';
import {
  deleteFile,
  deleteFolder,
  deleteFolderRecursive,
  getFolderDeletePreview,
  moveFile,
  moveFolder,
  renameFile,
  renameFolder,
} from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { formatDate, formatSize } from '../format';
import { ShareLinkPanel } from '../ShareLinkPanel';
import { GallerySettingsPanel } from '../GallerySettingsPanel';
import { DestinationPicker } from './DestinationPicker';
import { downloadUrl, typeLabel, type Entry } from './types';

export interface DetailsOutcome {
  tone: 'info' | 'error';
  text: string;
}

interface DetailsPanelProps {
  entry: Entry;
  currentParentId: string | null;
  onClose(): void;
  // Bubbles a banner message and asks the orchestrator to reload the listing.
  onOutcome(message: DetailsOutcome): void;
  // Open the folder (navigate) or open the media viewer for a file.
  onOpen(entry: Entry): void;
}

type Mode = 'props' | 'rename' | 'move' | 'share' | 'gallery';

// Side panel (bottom sheet on mobile) showing one item's properties and the
// single-item actions. Centralises rename / move / share / delete / gallery so
// the grid + list rows stay simple. Never renders any storage internals — only
// the no-leak fields already on the listing DTOs.
export function DetailsPanel({ entry, currentParentId, onClose, onOutcome, onOpen }: DetailsPanelProps) {
  const { invalidateAuth } = useAuth();
  const [mode, setMode] = useState<Mode>('props');
  const [busy, setBusy] = useState(false);
  const [inlineError, setInlineError] = useState<string | null>(null);

  const isFolder = entry.kind === 'folder';
  const name = isFolder ? entry.folder.name : entry.file.name;
  const [draft, setDraft] = useState(name);

  function classifyAndSet(err: unknown, fallback: string): boolean {
    if (err instanceof ApiError) {
      if (err.status === 401) {
        invalidateAuth();
        return true;
      }
      if (err.status === 404) {
        onOutcome({ tone: 'error', text: `This ${entry.kind} no longer exists. The list was refreshed.` });
        onClose();
        return true;
      }
      if (err.status === 409) {
        setInlineError(`Another ${entry.kind} with this name already exists here.`);
        return false;
      }
      if (err.status === 400) {
        const fromBody =
          typeof err.body === 'object' && err.body !== null && 'error' in err.body
            ? (err.body as { error?: unknown }).error
            : undefined;
        setInlineError(typeof fromBody === 'string' && fromBody.length > 0 ? fromBody : fallback);
        return false;
      }
    }
    setInlineError(fallback);
    return false;
  }

  async function onSaveRename() {
    const trimmed = draft.trim();
    if (trimmed.length === 0) {
      setInlineError(`Please enter a ${entry.kind} name.`);
      return;
    }
    if (trimmed === name) {
      setMode('props');
      setInlineError(null);
      return;
    }
    setBusy(true);
    setInlineError(null);
    try {
      if (isFolder) await renameFolder(entry.id, trimmed);
      else await renameFile(entry.id, trimmed);
      onOutcome({ tone: 'info', text: `Renamed ${entry.kind} to “${trimmed}”.` });
      onClose();
    } catch (err) {
      classifyAndSet(err, 'Could not rename. Please try again.');
    } finally {
      setBusy(false);
    }
  }

  async function onChooseDestination(destinationId: string | null) {
    setBusy(true);
    setInlineError(null);
    try {
      if (isFolder) await moveFolder(entry.id, destinationId);
      else await moveFile(entry.id, destinationId);
      onOutcome({ tone: 'info', text: `Moved ${entry.kind} “${name}”.` });
      onClose();
    } catch (err) {
      classifyAndSet(err, `This ${entry.kind} cannot be moved there.`);
    } finally {
      setBusy(false);
    }
  }

  async function onDelete() {
    if (isFolder) {
      await onDeleteFolder();
      return;
    }
    const ok = window.confirm(`Move file “${name}” to Trash? You can restore it later from Trash.`);
    if (!ok) return;
    setBusy(true);
    try {
      await deleteFile(entry.id);
      onOutcome({ tone: 'info', text: `Moved file “${name}” to Trash.` });
      onClose();
    } catch (err) {
      classifyAndSet(err, 'Could not delete this file. Please try again.');
    } finally {
      setBusy(false);
    }
  }

  async function onDeleteFolder() {
    setBusy(true);
    try {
      let preview: { fileCount: number; folderCount: number } | null = null;
      try {
        preview = await getFolderDeletePreview(entry.id);
      } catch {
        // Falls through to a simple confirm if the preview is unavailable.
      }
      const isEmpty = preview !== null && preview.fileCount === 0 && preview.folderCount === 0;
      let confirmed: boolean;
      let recursive = false;
      if (preview === null || isEmpty) {
        confirmed = window.confirm(`Move folder “${name}” to Trash? You can restore it later from Trash.`);
      } else {
        const parts: string[] = [];
        if (preview.fileCount > 0) parts.push(`${preview.fileCount} file${preview.fileCount !== 1 ? 's' : ''}`);
        if (preview.folderCount > 0) parts.push(`${preview.folderCount} sub-folder${preview.folderCount !== 1 ? 's' : ''}`);
        confirmed = window.confirm(
          `Move “${name}” and all its contents (${parts.join(' and ')}) to Trash?\n\n` +
            `Everything inside will be moved to Trash and can be restored from there.`,
        );
        recursive = confirmed;
      }
      if (!confirmed) {
        setBusy(false);
        return;
      }
      if (recursive) await deleteFolderRecursive(entry.id);
      else await deleteFolder(entry.id);
      onOutcome({ tone: 'info', text: `Moved folder “${name}” to Trash.` });
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        onOutcome({ tone: 'error', text: 'This folder is not empty. Open it and remove its contents before deleting.' });
      } else {
        classifyAndSet(err, 'Could not delete this folder. Please try again.');
      }
    } finally {
      setBusy(false);
    }
  }

  function onRenameKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter') {
      e.preventDefault();
      void onSaveRename();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      setDraft(name);
      setInlineError(null);
      setMode('props');
    }
  }

  return (
    <aside className="details-panel" aria-label={`Details: ${name}`}>
      <div className="details-panel-head">
        <h2 className="details-panel-title" title={name}>{name}</h2>
        <button type="button" className="details-panel-close" aria-label="Close details" onClick={onClose}>
          ✕
        </button>
      </div>

      {mode === 'props' && (
        <>
          <dl className="details-props">
            <dt>Type</dt>
            <dd>{isFolder ? 'Folder' : typeLabel(entry.file.mimeType)}</dd>
            {!isFolder && (
              <>
                <dt>Size</dt>
                <dd>{formatSize(entry.file.sizeBytes)}</dd>
                {entry.file.width != null && entry.file.height != null && (
                  <>
                    <dt>Dimensions</dt>
                    <dd>{entry.file.width} × {entry.file.height}</dd>
                  </>
                )}
              </>
            )}
            <dt>Added</dt>
            <dd>{formatDate(isFolder ? entry.folder.createdAt : entry.file.createdAt)}</dd>
          </dl>

          <div className="details-actions">
            <button type="button" className="row-action-primary" onClick={() => onOpen(entry)}>
              Open
            </button>
            {!isFolder && (
              <a className="files-action" href={downloadUrl(entry.id)} rel="noopener noreferrer">
                Download
              </a>
            )}
            <button type="button" className="files-action" onClick={() => { setDraft(name); setInlineError(null); setMode('rename'); }} disabled={busy}>
              Rename
            </button>
            <button type="button" className="files-action" onClick={() => { setInlineError(null); setMode('move'); }} disabled={busy}>
              Move
            </button>
            {!isFolder && (
              <button type="button" className="files-action" onClick={() => setMode('share')} disabled={busy}>
                Share
              </button>
            )}
            {isFolder && (
              <button type="button" className="files-action" onClick={() => setMode('gallery')} disabled={busy}>
                Gallery
              </button>
            )}
            <button type="button" className="files-action files-action-destructive" onClick={() => void onDelete()} disabled={busy}>
              Delete
            </button>
          </div>
          {inlineError !== null && <p className="row-inline-error" role="alert">{inlineError}</p>}
        </>
      )}

      {mode === 'rename' && (
        <div className="details-rename">
          <label className="visually-hidden" htmlFor="details-rename-input">Rename {entry.kind}</label>
          <input
            id="details-rename-input"
            type="text"
            autoFocus
            spellCheck={false}
            maxLength={255}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={onRenameKeyDown}
            disabled={busy}
            className="row-edit-input"
            aria-label={`Rename ${entry.kind} ${name}`}
          />
          <div className="details-actions">
            <button type="button" className="row-action-primary" onClick={() => void onSaveRename()} disabled={busy}>
              {busy ? 'Saving…' : 'Save'}
            </button>
            <button type="button" className="files-action" onClick={() => { setDraft(name); setInlineError(null); setMode('props'); }} disabled={busy}>
              Cancel
            </button>
          </div>
          {inlineError !== null && <p className="row-inline-error" role="alert">{inlineError}</p>}
        </div>
      )}

      {mode === 'move' && (
        <DestinationPicker
          title={`Move ${entry.kind} “${name}” to:`}
          excludeFolderIds={isFolder ? [entry.id] : []}
          currentParentId={currentParentId}
          busy={busy}
          error={inlineError}
          onChoose={(dest) => void onChooseDestination(dest)}
          onCancel={() => { setInlineError(null); setMode('props'); }}
        />
      )}

      {mode === 'share' && !isFolder && (
        <ShareLinkPanel
          fileId={entry.id}
          fileName={name}
          onClose={() => setMode('props')}
          onFileMissing={() => {
            onOutcome({ tone: 'error', text: `File “${name}” no longer exists. The list was refreshed.` });
            onClose();
          }}
        />
      )}

      {mode === 'gallery' && isFolder && (
        <GallerySettingsPanel
          folderId={entry.id}
          folderName={name}
          onSaved={(text) => { onOutcome({ tone: 'info', text }); setMode('props'); }}
          onCancel={() => setMode('props')}
        />
      )}
    </aside>
  );
}
