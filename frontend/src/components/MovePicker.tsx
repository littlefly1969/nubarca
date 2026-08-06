import { useCallback, useEffect, useState } from 'react';
import {
  getFolderChildren,
  getRootChildren,
  moveFile,
  moveFolder,
  type FolderSummary,
} from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';

interface MovePickerProps {
  itemId: string;
  itemName: string;
  itemKind: 'file' | 'folder';
  // The parent of the item before the user opened the picker. Used so
  // "Move here" is disabled on the current location (no-op) and so the
  // confirmation copy can name it.
  currentParentId: string | null;
  onMoved(message: { tone: 'info' | 'error'; text: string }): void;
  // The item vanished server-side (404 on the move) — the parent row should
  // close the picker and bubble a "no longer exists" banner.
  onItemMissing(): void;
  onCancel(): void;
}

type PickerStatus =
  | { kind: 'loading' }
  | { kind: 'ready'; folders: FolderSummary[] }
  | { kind: 'error'; message: string };

interface PickerEntry {
  id: string | null;
  name: string;
}

const ROOT_ENTRY: PickerEntry = { id: null, name: 'Home' };

// `MovePicker` is an inline panel mounted in a row-spanning slot beneath a
// FileRow / FolderRow. It owns its own breadcrumb stack so navigation inside
// the picker never disturbs the main folder browser. Only folders are shown.
//
// When moving a folder, the folder being moved is filtered out of the
// destination listing — so the user cannot navigate INTO it from the picker
// and therefore cannot select any descendant either. The backend still
// rejects "move into self / descendant" as a defence-in-depth, surfaced as a
// 400 with the backend's error string.
export function MovePicker({
  itemId,
  itemName,
  itemKind,
  currentParentId,
  onMoved,
  onItemMissing,
  onCancel,
}: MovePickerProps) {
  const { invalidateAuth } = useAuth();
  const [trail, setTrail] = useState<PickerEntry[]>([ROOT_ENTRY]);
  const [status, setStatus] = useState<PickerStatus>({ kind: 'loading' });
  const [busy, setBusy] = useState(false);
  const [inlineError, setInlineError] = useState<string | null>(null);

  const current = trail[trail.length - 1];

  const load = useCallback(
    (folderId: string | null, signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      setInlineError(null);
      const promise =
        folderId === null
          ? getRootChildren(signal)
          : getFolderChildren(folderId, signal);
      return promise
        .then((data) => {
          const folders =
            itemKind === 'folder'
              ? data.folders.filter((f) => f.id !== itemId)
              : data.folders;
          setStatus({ kind: 'ready', folders });
        })
        .catch((err: unknown) => {
          if (err instanceof DOMException && err.name === 'AbortError') return;
          if (err instanceof ApiError && err.status === 401) {
            invalidateAuth();
            return;
          }
          if (err instanceof ApiError && err.status === 404) {
            setStatus({
              kind: 'error',
              message: 'This folder no longer exists.',
            });
            return;
          }
          setStatus({
            kind: 'error',
            message: 'Could not load this folder. Please try again.',
          });
        });
    },
    [itemId, itemKind, invalidateAuth],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(current.id, controller.signal);
    return () => controller.abort();
  }, [current.id, load]);

  function openFolder(folder: FolderSummary) {
    setInlineError(null);
    setTrail((prev) => [...prev, { id: folder.id, name: folder.name }]);
  }

  function navigateToIndex(index: number) {
    setInlineError(null);
    setTrail((prev) => prev.slice(0, index + 1));
  }

  async function onMoveHere() {
    if (busy) return;
    const destinationId = current.id;
    setBusy(true);
    setInlineError(null);
    try {
      if (itemKind === 'file') {
        await moveFile(itemId, destinationId);
      } else {
        await moveFolder(itemId, destinationId);
      }
      onMoved({
        tone: 'info',
        text: `Moved ${itemKind} “${itemName}” to ${current.name}.`,
      });
    } catch (err) {
      handleMoveFailure(err);
    } finally {
      setBusy(false);
    }
  }

  function handleMoveFailure(err: unknown) {
    if (err instanceof ApiError) {
      if (err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err.status === 404) {
        // Could be the item itself or the destination — both fold to the same
        // "list out of date" outcome from the user's perspective.
        onItemMissing();
        return;
      }
      if (err.status === 409) {
        setInlineError(
          `A ${itemKind} with this name already exists in the destination.`,
        );
        return;
      }
      if (err.status === 400) {
        const fromBody =
          typeof err.body === 'object' && err.body !== null && 'error' in err.body
            ? (err.body as { error?: unknown }).error
            : undefined;
        setInlineError(
          typeof fromBody === 'string' && fromBody.length > 0
            ? fromBody
            : `This ${itemKind} cannot be moved there.`,
        );
        return;
      }
    }
    setInlineError(`Could not move this ${itemKind}. Please try again.`);
  }

  const isNoOp = currentParentId === current.id;
  const moveDisabled = busy || isNoOp || status.kind !== 'ready';

  return (
    <div className="move-panel" role="group" aria-label={`Move ${itemName}`}>
      <p className="move-panel-title">
        Move {itemKind} <strong>{itemName}</strong> to:
      </p>

      <div className="move-panel-breadcrumb" aria-label="Destination">
        {trail.map((entry, idx) => {
          const last = idx === trail.length - 1;
          const key = `${entry.id ?? 'root'}-${idx}`;
          return (
            <span key={key} className="move-breadcrumb-item">
              {last ? (
                <span className="move-breadcrumb-current" aria-current="page">
                  {entry.name}
                </span>
              ) : (
                <button
                  type="button"
                  className="move-breadcrumb-link"
                  onClick={() => navigateToIndex(idx)}
                  disabled={busy || status.kind === 'loading'}
                >
                  {entry.name}
                </button>
              )}
              {!last && (
                <span className="move-breadcrumb-sep" aria-hidden="true">
                  /
                </span>
              )}
            </span>
          );
        })}
      </div>

      <div className="move-panel-list-wrap">
        {status.kind === 'loading' && (
          <p className="muted" role="status">
            Loading…
          </p>
        )}
        {status.kind === 'error' && (
          <div className="move-panel-error" role="alert">
            <span>{status.message}</span>
            <button
              type="button"
              className="row-action"
              onClick={() => void load(current.id)}
            >
              Try again
            </button>
          </div>
        )}
        {status.kind === 'ready' && status.folders.length === 0 && (
          <p className="muted">No subfolders here.</p>
        )}
        {status.kind === 'ready' && status.folders.length > 0 && (
          <ul className="move-folder-list">
            {status.folders.map((f) => (
              <li key={f.id}>
                <button
                  type="button"
                  className="move-folder-link"
                  onClick={() => openFolder(f)}
                  disabled={busy}
                >
                  <span className="row-icon" aria-hidden="true">
                    📁
                  </span>
                  <span className="row-name">{f.name}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {inlineError !== null && (
        <p className="row-inline-error" role="alert">
          {inlineError}
        </p>
      )}

      <div className="move-panel-actions">
        <button
          type="button"
          className="row-action-primary"
          onClick={() => void onMoveHere()}
          disabled={moveDisabled}
          aria-label={`Move ${itemKind} ${itemName} to ${current.name}`}
        >
          {busy ? 'Moving…' : 'Move here'}
        </button>
        <button
          type="button"
          className="row-action"
          onClick={onCancel}
          disabled={busy}
        >
          Cancel
        </button>
        {isNoOp && (
          <span className="muted move-panel-hint">
            Already in {current.name}.
          </span>
        )}
      </div>
    </div>
  );
}
