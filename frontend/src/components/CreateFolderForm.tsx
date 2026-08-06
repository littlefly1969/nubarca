import { useId, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError } from '@nubarca/api-client';
import { createFolderInFolder, createRootFolder } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';

interface CreateFolderFormProps {
  // `null` means root.
  parentFolderId: string | null;
  // True while the parent folder listing is loading; keeps the form quiet
  // during navigation refreshes.
  disabled?: boolean;
  // Called once a folder is created so the FolderBrowser can reload.
  onCreated: () => void;
}

type Status =
  | { kind: 'idle' }
  | { kind: 'submitting' }
  | { kind: 'error'; message: string };

// Inline single-input form. Chosen over a modal: a folder name is one piece
// of data, the failure modes (empty, duplicate, invalid characters) are all
// short messages, and the inline form keeps keyboard flow simple — Enter
// submits, Escape clears.
export function CreateFolderForm({
  parentFolderId,
  disabled,
  onCreated,
}: CreateFolderFormProps) {
  const { invalidateAuth } = useAuth();
  const inputId = useId();
  const [name, setName] = useState('');
  const [status, setStatus] = useState<Status>({ kind: 'idle' });
  const inputRef = useRef<HTMLInputElement>(null);

  const controlsDisabled = disabled === true || status.kind === 'submitting';

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (controlsDisabled) return;

    // Trim once on the client so a name like " " is rejected with the same
    // empty-name path the backend would also take. The backend remains
    // authoritative for the actual `Trim() + length / character` checks.
    const trimmed = name.trim();
    if (trimmed.length === 0) {
      setStatus({ kind: 'error', message: 'Please enter a folder name.' });
      inputRef.current?.focus();
      return;
    }

    setStatus({ kind: 'submitting' });
    try {
      if (parentFolderId === null) {
        await createRootFolder(trimmed);
      } else {
        await createFolderInFolder(parentFolderId, trimmed);
      }
      setName('');
      setStatus({ kind: 'idle' });
      onCreated();
    } catch (err) {
      const failure = classifyCreateFolderError(err);
      if (failure.invalidate) {
        invalidateAuth();
      }
      setStatus({ kind: 'error', message: failure.message });
      inputRef.current?.focus();
    }
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      event.preventDefault();
      setName('');
      setStatus({ kind: 'idle' });
    }
  }

  return (
    <form className="create-folder-form" onSubmit={onSubmit} aria-label="Create a new folder">
      <div className="create-folder-row">
        <label htmlFor={inputId} className="create-folder-label">
          New folder
        </label>
        <input
          id={inputId}
          ref={inputRef}
          type="text"
          autoComplete="off"
          spellCheck={false}
          maxLength={255}
          placeholder="Folder name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={onKeyDown}
          disabled={controlsDisabled}
          className="create-folder-input"
        />
        <button
          type="submit"
          className="create-folder-button"
          disabled={controlsDisabled}
        >
          {status.kind === 'submitting' ? 'Creating…' : 'Create'}
        </button>
      </div>
      {status.kind === 'error' && (
        <p className="create-folder-error" role="alert">
          {status.message}
        </p>
      )}
    </form>
  );
}

interface ClassifiedFailure {
  message: string;
  invalidate: boolean;
}

function classifyCreateFolderError(err: unknown): ClassifiedFailure {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return { message: 'Session expired.', invalidate: true };
    }
    if (err.status === 409) {
      return {
        message: 'A folder with this name already exists here.',
        invalidate: false,
      };
    }
    if (err.status === 404) {
      // Parent folder vanished mid-flight (e.g., concurrent soft-delete).
      return {
        message: 'This folder no longer exists. Refresh and try again.',
        invalidate: false,
      };
    }
    if (err.status === 400) {
      const fromBody =
        typeof err.body === 'object' && err.body !== null && 'error' in err.body
          ? (err.body as { error?: unknown }).error
          : undefined;
      return {
        message:
          typeof fromBody === 'string' && fromBody.length > 0
            ? fromBody
            : 'That folder name is not allowed.',
        invalidate: false,
      };
    }
  }
  return {
    message: 'Could not create the folder. Please try again.',
    invalidate: false,
  };
}
