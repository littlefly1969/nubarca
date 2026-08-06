import { useCallback, useEffect, useState } from 'react';
import { getFolderChildren, getRootChildren, type FolderSummary } from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';

interface DestinationPickerProps {
  title: string;
  // Folders that must not be navigable destinations (the folders being moved —
  // a folder can't be moved into itself or a descendant). The backend rejects
  // it too; hiding them keeps the UI honest.
  excludeFolderIds: readonly string[];
  // Parent the items currently live in, so "Move here" is a no-op there.
  currentParentId: string | null;
  busy: boolean;
  // Inline error from the parent's move attempt (e.g. 409 name conflict).
  error: string | null;
  onChoose(destinationId: string | null): void;
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

// A choose-only folder navigator. Unlike the legacy MovePicker it does not
// perform the move itself — it returns the chosen destination so the caller can
// move one item or a whole multi-selection in a single place. Only folders are
// shown; excluded folders are filtered from the listing.
export function DestinationPicker({
  title,
  excludeFolderIds,
  currentParentId,
  busy,
  error,
  onChoose,
  onCancel,
}: DestinationPickerProps) {
  const { invalidateAuth } = useAuth();
  const [trail, setTrail] = useState<PickerEntry[]>([ROOT_ENTRY]);
  const [status, setStatus] = useState<PickerStatus>({ kind: 'loading' });

  const current = trail[trail.length - 1];
  const excluded = new Set(excludeFolderIds);

  const load = useCallback(
    (folderId: string | null, signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      const promise = folderId === null ? getRootChildren(signal) : getFolderChildren(folderId, signal);
      return promise
        .then((data) => {
          setStatus({ kind: 'ready', folders: data.folders.filter((f) => !excluded.has(f.id)) });
        })
        .catch((err: unknown) => {
          if (err instanceof DOMException && err.name === 'AbortError') return;
          if (err instanceof ApiError && err.status === 401) {
            invalidateAuth();
            return;
          }
          setStatus({ kind: 'error', message: 'Could not load this folder. Please try again.' });
        });
    },
    // `excluded` is rebuilt each render from the same prop; folding it into the
    // deps via excludeFolderIds keeps the closure correct without thrashing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [invalidateAuth, excludeFolderIds.join(',')],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(current.id, controller.signal);
    return () => controller.abort();
  }, [current.id, load]);

  const isNoOp = currentParentId === current.id;

  return (
    <div className="destination-picker" role="group" aria-label={title}>
      <p className="destination-picker-title">{title}</p>

      <div className="destination-breadcrumb" aria-label="Destination">
        {trail.map((entry, idx) => {
          const last = idx === trail.length - 1;
          return (
            <span key={`${entry.id ?? 'root'}-${idx}`} className="destination-breadcrumb-item">
              {last ? (
                <span aria-current="page">{entry.name}</span>
              ) : (
                <button
                  type="button"
                  className="destination-breadcrumb-link"
                  onClick={() => setTrail((prev) => prev.slice(0, idx + 1))}
                  disabled={busy}
                >
                  {entry.name}
                </button>
              )}
              {!last && <span aria-hidden="true"> / </span>}
            </span>
          );
        })}
      </div>

      <div className="destination-list-wrap">
        {status.kind === 'loading' && <p className="muted" role="status">Loading…</p>}
        {status.kind === 'error' && (
          <div className="destination-error" role="alert">
            <span>{status.message}</span>
            <button type="button" className="files-action" onClick={() => void load(current.id)}>
              Try again
            </button>
          </div>
        )}
        {status.kind === 'ready' && status.folders.length === 0 && (
          <p className="muted">No subfolders here.</p>
        )}
        {status.kind === 'ready' && status.folders.length > 0 && (
          <ul className="destination-folder-list">
            {status.folders.map((f) => (
              <li key={f.id}>
                <button
                  type="button"
                  className="destination-folder-link"
                  onClick={() => setTrail((prev) => [...prev, { id: f.id, name: f.name }])}
                  disabled={busy}
                >
                  <span aria-hidden="true">📁</span>
                  <span className="row-name">{f.name}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {error !== null && <p className="row-inline-error" role="alert">{error}</p>}

      <div className="destination-actions">
        <button
          type="button"
          className="row-action-primary"
          onClick={() => onChoose(current.id)}
          disabled={busy || isNoOp || status.kind !== 'ready'}
          aria-label={`Move here to ${current.name}`}
        >
          {busy ? 'Moving…' : `Move to ${current.name}`}
        </button>
        <button type="button" className="files-action" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
        {isNoOp && <span className="muted">Already here.</span>}
      </div>
    </div>
  );
}
