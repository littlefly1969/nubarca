import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  browseImport,
  cancelImportRun,
  enqueueImportDerivatives,
  getDestinationFolders,
  getImportRoots,
  getImportRunStatus,
  getImportUsers,
  listImportRunItems,
  listImportRuns,
  previewImport,
  runImport,
  type AdminImportBrowseResponse,
  type AdminImportItem,
  type AdminImportPhaseTimings,
  type AdminImportPreview,
  type AdminImportRoot,
  type AdminImportRunMetrics,
  type AdminImportRunStatus,
  type AdminImportThrottleConfig,
  type AdminImportUser,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { formatSize } from '../components/format';

const EMPTY_METRICS: AdminImportRunMetrics = {
  durationMillis: null, filesPerSecond: null, bytesPerSecond: null,
  conflictPercent: null, skippedPercent: null, failedPercent: null,
  averageImportedFileBytes: null,
};
const EMPTY_TIMINGS: AdminImportPhaseTimings = {
  readMillis: null, hashMillis: null, writeMillis: null, blobDbMillis: null,
  detectMillis: null, metadataMillis: null, fileItemMillis: null,
  thumbnailMillis: null, folderMillis: null, itemDbMillis: null,
};

// Slice 81 — admin-only guided server-side import. Five steps:
//   source root + directory → target user → destination folder → preview → run.
// The page is UX only; the backend gates /api/admin/* independently and is the
// source of truth for enabled/forbidden. No physical paths/internals are shown.

type RootsState =
  | { kind: 'loading' }
  | { kind: 'disabled' }
  | { kind: 'unconfigured' }
  | { kind: 'ready'; roots: AdminImportRoot[] }
  | { kind: 'forbidden' }
  | { kind: 'error'; message: string };

type Step = 'source' | 'user' | 'destination' | 'preview' | 'run';

interface FolderCrumb {
  id: string | null;
  name: string;
}

const TERMINAL = new Set(['succeeded', 'partial', 'failed', 'cancelled']);

export function AdminImportPage() {
  const { state, invalidateAuth } = useAuth();

  const [rootsState, setRootsState] = useState<RootsState>({ kind: 'loading' });
  const [step, setStep] = useState<Step>('source');

  // Selections.
  const [selectedRoot, setSelectedRoot] = useState<AdminImportRoot | null>(null);
  const [sourceRelativePath, setSourceRelativePath] = useState('');
  const [selectedUser, setSelectedUser] = useState<AdminImportUser | null>(null);
  const [destination, setDestination] = useState<FolderCrumb>({ id: null, name: 'Library root' });

  // Source browser.
  const [browse, setBrowse] = useState<AdminImportBrowseResponse | null>(null);
  const [browseError, setBrowseError] = useState<string | null>(null);
  const [browseLoading, setBrowseLoading] = useState(false);

  // Users.
  const [users, setUsers] = useState<AdminImportUser[] | null>(null);
  const [usersError, setUsersError] = useState<string | null>(null);

  // Destination browser.
  const [destFolders, setDestFolders] = useState<AdminImportFolderView | null>(null);
  const [destError, setDestError] = useState<string | null>(null);

  // Preview + run.
  const [preview, setPreview] = useState<AdminImportPreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [runStarting, setRunStarting] = useState(false);
  const [runStatus, setRunStatus] = useState<AdminImportRunStatus | null>(null);

  // Slice 83: configured throttle values (shown so the admin knows how imports
  // are paced).
  const [throttle, setThrottle] = useState<AdminImportThrottleConfig | null>(null);

  const onAuthError = useCallback(
    (err: unknown): boolean => {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return true;
      }
      return false;
    },
    [invalidateAuth],
  );

  // ---- roots (entry) -------------------------------------------------------
  const loadRoots = useCallback(
    async (signal?: AbortSignal) => {
      setRootsState({ kind: 'loading' });
      try {
        const data = await getImportRoots(signal);
        setThrottle(data.throttle ?? null);
        if (!data.enabled) {
          setRootsState({ kind: 'disabled' });
        } else if (!data.configured) {
          setRootsState({ kind: 'unconfigured' });
        } else {
          setRootsState({ kind: 'ready', roots: data.roots });
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (onAuthError(err)) return;
        if (err instanceof ApiError && err.status === 403) {
          setRootsState({ kind: 'forbidden' });
          return;
        }
        setRootsState({ kind: 'error', message: 'Could not load import configuration.' });
      }
    },
    [onAuthError],
  );

  useEffect(() => {
    const controller = new AbortController();
    void loadRoots(controller.signal);
    return () => controller.abort();
  }, [loadRoots]);

  // ---- source browse -------------------------------------------------------
  const doBrowse = useCallback(
    async (root: AdminImportRoot, relativePath: string) => {
      setBrowseLoading(true);
      setBrowseError(null);
      try {
        const data = await browseImport(root.rootId, relativePath);
        setBrowse(data);
      } catch (err) {
        if (onAuthError(err)) return;
        const message =
          err instanceof ApiError && typeof (err.body as { error?: string })?.error === 'string'
            ? (err.body as { error: string }).error
            : 'Could not browse this directory.';
        setBrowseError(message);
      } finally {
        setBrowseLoading(false);
      }
    },
    [onAuthError],
  );

  function chooseRoot(root: AdminImportRoot) {
    setSelectedRoot(root);
    setStep('source');
    void doBrowse(root, '');
  }

  // ---- users ---------------------------------------------------------------
  const loadUsers = useCallback(async () => {
    setUsersError(null);
    try {
      setUsers(await getImportUsers());
    } catch (err) {
      if (onAuthError(err)) return;
      setUsersError('Could not load users.');
    }
  }, [onAuthError]);

  // ---- destination ---------------------------------------------------------
  const loadDestFolders = useCallback(
    async (userId: string, crumbs: FolderCrumb[]) => {
      setDestError(null);
      const parent = crumbs[crumbs.length - 1]?.id ?? null;
      try {
        const data = await getDestinationFolders(userId, parent);
        setDestFolders({ crumbs, folders: data.folders });
      } catch (err) {
        if (onAuthError(err)) return;
        setDestError('Could not load folders.');
      }
    },
    [onAuthError],
  );

  // ---- step transitions ----------------------------------------------------
  function selectSourceDirectory() {
    if (!browse) return;
    setSourceRelativePath(browse.relativePath);
    setStep('user');
    if (users === null) void loadUsers();
  }

  function selectUser(user: AdminImportUser) {
    setSelectedUser(user);
    const rootCrumbs: FolderCrumb[] = [{ id: null, name: 'Library root' }];
    setStep('destination');
    void loadDestFolders(user.id, rootCrumbs);
  }

  function selectDestination() {
    if (!destFolders) return;
    const current = destFolders.crumbs[destFolders.crumbs.length - 1];
    setDestination(current);
    setStep('preview');
    void doPreview(current.id);
  }

  const doPreview = useCallback(
    async (destinationFolderId: string | null) => {
      if (!selectedRoot || !selectedUser) return;
      setPreviewLoading(true);
      setPreviewError(null);
      setPreview(null);
      try {
        const data = await previewImport({
          rootId: selectedRoot.rootId,
          relativePath: sourceRelativePath,
          targetUserId: selectedUser.id,
          destinationFolderId,
        });
        setPreview(data);
      } catch (err) {
        if (onAuthError(err)) return;
        const message =
          err instanceof ApiError && typeof (err.body as { error?: string })?.error === 'string'
            ? (err.body as { error: string }).error
            : 'Could not preview this import.';
        setPreviewError(message);
      } finally {
        setPreviewLoading(false);
      }
    },
    [onAuthError, selectedRoot, selectedUser, sourceRelativePath],
  );

  async function startImport() {
    if (!selectedRoot || !selectedUser) return;
    setRunStarting(true);
    setRunError(null);
    try {
      const result = await runImport({
        rootId: selectedRoot.rootId,
        relativePath: sourceRelativePath,
        targetUserId: selectedUser.id,
        destinationFolderId: destination.id,
      });
      setStep('run');
      setRunStatus({
        importRunId: result.importRunId,
        jobId: result.jobId,
        status: result.status,
        cancelRequested: false,
        phase: null,
        rootId: selectedRoot.rootId,
        sourceRelativePath,
        targetUserId: selectedUser.id,
        targetUserEmail: selectedUser.email,
        destinationFolderId: destination.id,
        scannedFiles: 0,
        pendingFiles: 0,
        importedFiles: 0,
        skippedFiles: 0,
        skippedPreviouslyDeletedFiles: 0,
        skippedAlreadyPresentFiles: 0,
        failedFiles: 0,
        conflictFiles: 0,
        alreadyImportedFiles: 0,
        cancelledFiles: 0,
        importedBytes: 0,
        totalBytes: preview?.totalBytes ?? 0,
        totalDirectories: preview?.totalDirectories ?? 0,
        currentRelativePath: null,
        error: null,
        createdAt: new Date().toISOString(),
        startedAt: null,
        completedAt: null,
        scanCompletedAt: null,
        metrics: EMPTY_METRICS,
        timings: EMPTY_TIMINGS,
        conflictSamples: [],
      });
    } catch (err) {
      if (onAuthError(err)) return;
      const message =
        err instanceof ApiError && typeof (err.body as { error?: string })?.error === 'string'
          ? (err.body as { error: string }).error
          : 'Could not start the import.';
      setRunError(message);
    } finally {
      setRunStarting(false);
    }
  }

  // ---- run status polling --------------------------------------------------
  const runId = runStatus?.importRunId;
  const runTerminal = runStatus ? TERMINAL.has(runStatus.status) : false;
  const refreshRunStatus = useCallback(async () => {
    if (!runId) return;
    try {
      setRunStatus(await getImportRunStatus(runId));
    } catch (err) {
      if (onAuthError(err)) return;
      // Transient: keep showing the last known status.
    }
  }, [runId, onAuthError]);

  const pollRef = useRef(refreshRunStatus);
  pollRef.current = refreshRunStatus;
  useEffect(() => {
    if (!runId || runTerminal) return;
    const id = setInterval(() => void pollRef.current(), 1500);
    return () => clearInterval(id);
  }, [runId, runTerminal]);

  async function cancelWizardRun() {
    if (!runId) return;
    try {
      await cancelImportRun(runId);
      await refreshRunStatus();
    } catch (err) {
      onAuthError(err);
    }
  }

  function resetWizard() {
    setStep('source');
    setSelectedRoot(null);
    setSourceRelativePath('');
    setSelectedUser(null);
    setDestination({ id: null, name: 'Library root' });
    setBrowse(null);
    setPreview(null);
    setRunStatus(null);
    setRunError(null);
  }

  if (state.status !== 'authed') return null;

  return (
    <section className="admin-page admin-import-page" aria-busy={rootsState.kind === 'loading'}>
      <header className="admin-header">
        <h2>Server-side import</h2>
      </header>

      <p className="admin-import-throttle-note muted">
        Imports run in the background with low-priority throttling.
        {throttle && (
          <>
            {' '}
            <span>
              Delay {throttle.delayBetweenFilesMs} ms/file
              {' · '}
              {throttle.maxBytesPerSecond > 0
                ? `${formatSize(throttle.maxBytesPerSecond)}/s cap`
                : 'no rate cap'}
              {' · '}
              {throttle.maxRunMinutes > 0
                ? `${throttle.maxRunMinutes} min/slice`
                : 'no time limit'}
              {' · '}
              yield every {throttle.yieldEveryFiles} files
            </span>
          </>
        )}
      </p>

      {rootsState.kind === 'loading' && <p role="status">Loading…</p>}
      {rootsState.kind === 'forbidden' && (
        <p className="folder-error" role="alert">Admin access is required.</p>
      )}
      {rootsState.kind === 'error' && (
        <div className="folder-error" role="alert">
          {rootsState.message}
          <button type="button" className="retry-button" onClick={() => void loadRoots()}>Try again</button>
        </div>
      )}
      {rootsState.kind === 'disabled' && (
        <p className="empty-state" role="status">
          Server-side import is disabled. Set <code>AdminImport__Enabled=true</code> and configure{' '}
          <code>AdminImport__Roots__0</code>.
        </p>
      )}
      {rootsState.kind === 'unconfigured' && (
        <p className="empty-state" role="status">
          Server-side import is enabled but no import roots are configured.
        </p>
      )}

      {rootsState.kind === 'ready' && (
        <div className="admin-import-wizard">
          <ol className="admin-import-steps" aria-label="Import steps">
            {(['source', 'user', 'destination', 'preview', 'run'] as Step[]).map((s) => (
              <li key={s} className={step === s ? 'is-active' : undefined} aria-current={step === s ? 'step' : undefined}>
                {STEP_LABELS[s]}
              </li>
            ))}
          </ol>

          {step === 'source' && (
            <SourceStep
              roots={rootsState.roots}
              selectedRoot={selectedRoot}
              onChooseRoot={chooseRoot}
              browse={browse}
              loading={browseLoading}
              error={browseError}
              onEnter={(rel) => selectedRoot && void doBrowse(selectedRoot, rel)}
              onSelect={selectSourceDirectory}
            />
          )}

          {step === 'user' && (
            <UserStep
              users={users}
              error={usersError}
              onBack={() => setStep('source')}
              onSelect={selectUser}
            />
          )}

          {step === 'destination' && selectedUser && (
            <DestinationStep
              user={selectedUser}
              view={destFolders}
              error={destError}
              onEnter={(crumbs) => void loadDestFolders(selectedUser.id, crumbs)}
              onBack={() => setStep('user')}
              onSelect={selectDestination}
            />
          )}

          {step === 'preview' && (
            <PreviewStep
              rootLabel={selectedRoot?.label ?? ''}
              sourceRelativePath={sourceRelativePath}
              user={selectedUser}
              destinationName={destination.name}
              preview={preview}
              loading={previewLoading}
              error={previewError}
              starting={runStarting}
              runError={runError}
              onBack={() => setStep('destination')}
              onConfirm={() => void startImport()}
            />
          )}

          {step === 'run' && runStatus && (
            <RunStep
              status={runStatus}
              terminal={runTerminal}
              onRefresh={() => void refreshRunStatus()}
              onReset={resetWizard}
              onCancel={() => void cancelWizardRun()}
            />
          )}
        </div>
      )}

      {(rootsState.kind === 'ready'
        || rootsState.kind === 'disabled'
        || rootsState.kind === 'unconfigured') && (
        <ImportRunsSection onAuthError={onAuthError} />
      )}
    </section>
  );
}

interface AdminImportFolderView {
  crumbs: FolderCrumb[];
  folders: { id: string; name: string }[];
}

const STEP_LABELS: Record<Step, string> = {
  source: '1. Source',
  user: '2. User',
  destination: '3. Destination',
  preview: '4. Preview',
  run: '5. Import',
};

function SourceStep({
  roots, selectedRoot, onChooseRoot, browse, loading, error, onEnter, onSelect,
}: {
  roots: AdminImportRoot[];
  selectedRoot: AdminImportRoot | null;
  onChooseRoot: (r: AdminImportRoot) => void;
  browse: AdminImportBrowseResponse | null;
  loading: boolean;
  error: string | null;
  onEnter: (relativePath: string) => void;
  onSelect: () => void;
}) {
  return (
    <div className="admin-import-step">
      <h3>Choose an import root</h3>
      <div className="admin-import-roots">
        {roots.map((r) => (
          <button
            key={r.rootId}
            type="button"
            className={`admin-import-root-card${selectedRoot?.rootId === r.rootId ? ' is-selected' : ''}`}
            onClick={() => onChooseRoot(r)}
          >
            {r.label}
          </button>
        ))}
      </div>

      {selectedRoot && (
        <div className="admin-import-browser">
          <h3>Browse {selectedRoot.label}</h3>
          <nav className="admin-import-breadcrumb" aria-label="Current location">
            <span>{selectedRoot.label}</span>
            {browse && browse.relativePath.length > 0 && <span> / {browse.relativePath}</span>}
          </nav>
          {error && <p className="folder-error" role="alert">{error}</p>}
          {loading && <p role="status">Loading…</p>}
          {browse && !loading && (
            <>
              {browse.parentRelativePath !== null && (
                <button type="button" className="row-action" onClick={() => onEnter(browse.parentRelativePath ?? '')}>
                  ↑ Up
                </button>
              )}
              <ul className="admin-import-dir-list">
                {browse.directories.length === 0 && (
                  <li className="muted">No subdirectories here.</li>
                )}
                {browse.directories.map((d) => (
                  <li key={d.relativePath}>
                    <button type="button" className="admin-import-dir" onClick={() => onEnter(d.relativePath)}>
                      📁 {d.name}
                    </button>
                    <span className="muted"> ({d.fileCount} files, {d.childDirectoryCount} folders)</span>
                  </li>
                ))}
              </ul>
              <button type="button" className="row-action-primary" onClick={onSelect}>
                Select this directory
              </button>
            </>
          )}
        </div>
      )}
    </div>
  );
}

function UserStep({
  users, error, onBack, onSelect,
}: {
  users: AdminImportUser[] | null;
  error: string | null;
  onBack: () => void;
  onSelect: (u: AdminImportUser) => void;
}) {
  return (
    <div className="admin-import-step">
      <h3>Choose the target user</h3>
      {error && <p className="folder-error" role="alert">{error}</p>}
      {users === null && !error && <p role="status">Loading users…</p>}
      {users && (
        <ul className="admin-import-user-list" role="list">
          {users.map((u) => (
            <li key={u.id}>
              <button
                type="button"
                className="admin-import-user"
                disabled={!u.isActive}
                onClick={() => onSelect(u)}
              >
                {u.displayName} ({u.email})
                {u.isAdmin && <span className="admin-badge"> admin</span>}
                {!u.isActive && <span className="muted"> — disabled</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
      <button type="button" className="row-action" onClick={onBack}>← Back</button>
    </div>
  );
}

function DestinationStep({
  user, view, error, onEnter, onBack, onSelect,
}: {
  user: AdminImportUser;
  view: AdminImportFolderView | null;
  error: string | null;
  onEnter: (crumbs: FolderCrumb[]) => void;
  onBack: () => void;
  onSelect: () => void;
}) {
  return (
    <div className="admin-import-step">
      <h3>Choose a destination folder for {user.displayName}</h3>
      {error && <p className="folder-error" role="alert">{error}</p>}
      {view === null && !error && <p role="status">Loading folders…</p>}
      {view && (
        <>
          <nav className="admin-import-breadcrumb" aria-label="Destination location">
            {view.crumbs.map((c, i) => (
              <button
                key={c.id ?? 'root'}
                type="button"
                className="admin-import-crumb"
                onClick={() => onEnter(view.crumbs.slice(0, i + 1))}
              >
                {c.name}
                {i < view.crumbs.length - 1 ? ' / ' : ''}
              </button>
            ))}
          </nav>
          <ul className="admin-import-dir-list">
            {view.folders.length === 0 && <li className="muted">No subfolders here.</li>}
            {view.folders.map((f) => (
              <li key={f.id}>
                <button
                  type="button"
                  className="admin-import-dir"
                  onClick={() => onEnter([...view.crumbs, { id: f.id, name: f.name }])}
                >
                  📁 {f.name}
                </button>
              </li>
            ))}
          </ul>
          <button type="button" className="row-action-primary" onClick={onSelect}>
            Use this folder
          </button>
        </>
      )}
      <button type="button" className="row-action" onClick={onBack}>← Back</button>
    </div>
  );
}

function PreviewStep({
  rootLabel, sourceRelativePath, user, destinationName, preview, loading, error, starting, runError, onBack, onConfirm,
}: {
  rootLabel: string;
  sourceRelativePath: string;
  user: AdminImportUser | null;
  destinationName: string;
  preview: AdminImportPreview | null;
  loading: boolean;
  error: string | null;
  starting: boolean;
  runError: string | null;
  onBack: () => void;
  onConfirm: () => void;
}) {
  const sourceLabel = sourceRelativePath.length > 0 ? `${rootLabel} / ${sourceRelativePath}` : rootLabel;
  return (
    <div className="admin-import-step">
      <h3>Preview</h3>
      <dl className="admin-import-summary">
        <dt>Source</dt><dd>{sourceLabel}</dd>
        <dt>Target user</dt><dd>{user ? `${user.displayName} (${user.email})` : ''}</dd>
        <dt>Destination</dt><dd>{destinationName}</dd>
      </dl>
      {loading && <p role="status">Scanning…</p>}
      {error && <p className="folder-error" role="alert">{error}</p>}
      {preview && (
        <>
          <ul className="admin-import-counts">
            <li>{preview.totalFiles} files</li>
            <li>{preview.totalDirectories} directories</li>
            <li>{formatSize(preview.totalBytes)}</li>
          </ul>
          {preview.warnings.length > 0 && (
            <ul className="admin-import-warnings" aria-label="Warnings">
              {preview.warnings.map((w) => <li key={w} className="muted">⚠ {w}</li>)}
            </ul>
          )}
          {runError && <p className="folder-error" role="alert">{runError}</p>}
          <p className="admin-import-confirm-text">
            Import {preview.totalFiles} files / {formatSize(preview.totalBytes)} from{' '}
            <strong>{sourceLabel}</strong> into <strong>{user?.email}</strong> under{' '}
            <strong>{destinationName}</strong>?
          </p>
          <button
            type="button"
            className="row-action-primary"
            disabled={starting || preview.totalFiles === 0}
            onClick={onConfirm}
          >
            {starting ? 'Starting…' : 'Confirm & import'}
          </button>
        </>
      )}
      <button type="button" className="row-action" onClick={onBack}>← Back</button>
    </div>
  );
}

function RunStep({
  status, terminal, onRefresh, onReset, onCancel,
}: {
  status: AdminImportRunStatus;
  terminal: boolean;
  onRefresh: () => void;
  onReset: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="admin-import-step">
      <h3>Import {terminal ? 'complete' : 'in progress'}</h3>
      <RunProgress status={status} terminal={terminal} />
      {status.status === 'queued' && (
        <p className="muted">
          Queued. Imports run via the background worker (<code>Jobs__WorkerEnabled=true</code>) or{' '}
          <code>jobs run-once</code>.
        </p>
      )}
      <div className="admin-import-actions">
        {!terminal && (
          <button type="button" className="row-action" onClick={onRefresh}>Refresh</button>
        )}
        {!terminal && (
          <CancelButton cancelRequested={status.cancelRequested} onCancel={onCancel} />
        )}
        {terminal && (
          <button type="button" className="row-action-primary" onClick={onReset}>Start another import</button>
        )}
      </div>
    </div>
  );
}

// ── shared bits used by the wizard run step and the history detail ──────────

function CancelButton({
  cancelRequested, onCancel,
}: {
  cancelRequested: boolean;
  onCancel: () => void;
}) {
  if (cancelRequested) {
    return <span className="muted" role="status">Cancelling after the current file…</span>;
  }
  return (
    <button
      type="button"
      className="row-action row-action-danger"
      onClick={() => {
        if (window.confirm('Stop this import after the current file?')) onCancel();
      }}
    >
      Cancel import
    </button>
  );
}

function RunProgress({ status, terminal }: { status: AdminImportRunStatus; terminal: boolean }) {
  // Slice 92: once the scan persisted the manifest, total files are known —
  // show a real progress bar (processed / scanned).
  const processed =
    status.importedFiles + status.skippedFiles + status.failedFiles
    + status.conflictFiles + status.cancelledFiles;
  const percent = status.scanCompletedAt && status.scannedFiles > 0
    ? Math.min(100, Math.round((processed / status.scannedFiles) * 100))
    : null;

  return (
    <>
      <p role="status" aria-live="polite">
        Status: {status.status}
        {status.status === 'running' && status.phase ? ` — ${status.phase}` : ''}
        {status.cancelRequested && status.status === 'running' ? ' (cancelling)' : ''}
      </p>
      {status.status === 'running' && status.phase === 'scanning' && (
        <p className="muted">
          Scanning the source tree ({status.scannedFiles} files discovered so far)…
        </p>
      )}
      {percent !== null && (
        <div
          className="admin-import-progressbar"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent}
          aria-label="Import progress"
        >
          <span className="admin-import-progressbar-fill" style={{ width: `${percent}%` }} />
          <span className="admin-import-progressbar-text">
            {processed}/{status.scannedFiles} files ({percent}%)
          </span>
        </div>
      )}
      <ul className="admin-import-counts">
        <li>Imported: {status.importedFiles}</li>
        <li>Pending: {status.pendingFiles}</li>
        <li>Conflicts: {status.conflictFiles}</li>
        <li>Resumed (already imported): {status.alreadyImportedFiles}</li>
        <li>Skipped: {status.skippedFiles}</li>
        <li>Failed: {status.failedFiles}</li>
        {status.cancelledFiles > 0 && <li>Cancelled: {status.cancelledFiles}</li>}
        <li>Bytes: {formatSize(status.importedBytes)}{status.totalBytes > 0 ? ` / ${formatSize(status.totalBytes)}` : ''}</li>
      </ul>
      {status.alreadyImportedFiles > 0 && status.conflictFiles === 0 && (
        <p className="muted">
          “Resumed” = files this run had already imported before a pause/retry,
          re-detected from the import manifest. Not a conflict.
        </p>
      )}
      {status.conflictSamples.length > 0 && (
        <details className="admin-import-samples">
          <summary>Conflict samples ({status.conflictSamples.length})</summary>
          <ul>
            {status.conflictSamples.map((s, i) => (
              <li key={`${s.relativePath}-${i}`} className="muted">
                <code>{s.relativePath}</code> — {s.reason}
              </li>
            ))}
          </ul>
        </details>
      )}
      {!terminal && status.currentRelativePath && (
        <p className="muted">Current: {status.currentRelativePath}</p>
      )}
      {status.status === 'paused' && (
        <p className="muted" role="status">
          Paused after reaching the time budget — re-queued and will resume from
          the saved manifest (completed files are skipped by state, without
          re-walking the source).
        </p>
      )}
      <p className="muted admin-import-derivatives-note">
        Thumbnails, previews and video posters are generated <em>after</em> the
        import by a background job (and on demand), so they may appear
        progressively in the gallery.
      </p>
      {status.error && <p className="folder-error" role="alert">{status.error}</p>}
      <PerfBreakdown status={status} />
    </>
  );
}

function fmtMs(ms: number | null): string {
  if (ms == null) return 'not available';
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(1)} s`;
}

function fmtThroughput(bytesPerSec: number | null): string {
  if (bytesPerSec == null) return 'not available';
  return `${formatSize(Math.round(bytesPerSec))}/s`;
}

const PHASE_LABELS: Array<{ key: keyof AdminImportPhaseTimings; label: string }> = [
  { key: 'readMillis', label: 'Read (source)' },
  { key: 'hashMillis', label: 'SHA-256' },
  { key: 'writeMillis', label: 'Write (blob)' },
  { key: 'blobDbMillis', label: 'Blob DB' },
  { key: 'detectMillis', label: 'Media detect' },
  { key: 'metadataMillis', label: 'Metadata (full extract)' },
  { key: 'fileItemMillis', label: 'FileItem + metadata row' },
  { key: 'thumbnailMillis', label: 'Thumbnail' },
  { key: 'folderMillis', label: 'Folders' },
  { key: 'itemDbMillis', label: 'Item bookkeeping' },
];

function PerfBreakdown({ status }: { status: AdminImportRunStatus }) {
  const { metrics, timings } = status;
  const phases = PHASE_LABELS.map((p) => ({ ...p, value: timings[p.key] }));
  const measured = phases.filter((p) => p.value != null);
  const max = measured.reduce((m, p) => Math.max(m, p.value ?? 0), 0);

  return (
    <div className="admin-import-perf">
      <h4>Performance</h4>
      <ul className="admin-import-counts">
        <li>Duration: {fmtMs(metrics.durationMillis)}</li>
        <li>
          Throughput:{' '}
          {metrics.filesPerSecond == null
            ? 'not available'
            : `${metrics.filesPerSecond.toFixed(1)} files/s`}
          {' · '}
          {fmtThroughput(metrics.bytesPerSecond)}
        </li>
        <li>
          Avg file:{' '}
          {metrics.averageImportedFileBytes == null
            ? 'not available'
            : formatSize(metrics.averageImportedFileBytes)}
        </li>
      </ul>
      {measured.length === 0 ? (
        <p className="muted">Phase timings not available for this run.</p>
      ) : (
        <table className="admin-import-phase-table">
          <tbody>
            {phases.map((p) => (
              <tr key={p.key}>
                <th scope="row">{p.label}</th>
                <td>{fmtMs(p.value)}</td>
                <td className="admin-import-phase-bar-cell">
                  <span
                    className="admin-import-phase-bar"
                    style={{ width: max > 0 && p.value != null ? `${(p.value / max) * 100}%` : '0%' }}
                    aria-hidden="true"
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="muted admin-import-perf-note">
        Duplicate files still spend time in Read + SHA even when blob Write is low (the bytes are
        read and hashed to detect the duplicate; only the final write is skipped).
      </p>
    </div>
  );
}

// ── Import runs history (list + live detail + cancel) ───────────────────────

function ImportRunsSection({ onAuthError }: { onAuthError: (err: unknown) => boolean }) {
  const [runs, setRuns] = useState<AdminImportRunStatus[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);
    try {
      const data = await listImportRuns(25, 0, signal);
      setRuns(data.runs);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (onAuthError(err)) return;
      setError('Could not load import runs.');
    }
  }, [onAuthError]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return (
    <section className="admin-import-runs" aria-label="Import runs">
      <div className="admin-header">
        <h3>Import runs</h3>
        <button type="button" className="refresh-button" onClick={() => void load()}>Refresh</button>
      </div>
      {error && <p className="folder-error" role="alert">{error}</p>}
      {runs === null && !error && <p role="status">Loading runs…</p>}
      {runs && runs.length === 0 && <p className="empty-state">No import runs yet.</p>}
      {runs && runs.length > 0 && (
        <table className="admin-import-runs-table">
          <thead>
            <tr>
              <th scope="col">Status</th>
              <th scope="col">Source</th>
              <th scope="col">User</th>
              <th scope="col">Imported</th>
              <th scope="col">Conflicts</th>
              <th scope="col">Failed</th>
              <th scope="col">Bytes</th>
              <th scope="col">Duration</th>
              <th scope="col">Created</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((r) => (
              <tr
                key={r.importRunId}
                className="admin-import-run-row"
                onClick={() => setSelectedId(r.importRunId)}
                tabIndex={0}
                role="button"
                aria-label={`Open run for ${r.targetUserEmail ?? r.targetUserId}`}
                onKeyDown={(e) => { if (e.key === 'Enter') setSelectedId(r.importRunId); }}
              >
                <td>{r.status}</td>
                <td>{r.sourceRelativePath.length > 0 ? r.sourceRelativePath : '(root)'}</td>
                <td>{r.targetUserEmail ?? r.targetUserId}</td>
                <td>{r.importedFiles}</td>
                <td>{r.conflictFiles}</td>
                <td>{r.failedFiles}</td>
                <td>{formatSize(r.importedBytes)}</td>
                <td>{fmtMs(r.metrics.durationMillis)}</td>
                <td>{new Date(r.createdAt).toLocaleString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {selectedId && (
        <RunDetail
          runId={selectedId}
          onClose={() => setSelectedId(null)}
          onAuthError={onAuthError}
          onChanged={() => void load()}
        />
      )}
    </section>
  );
}

function RunDetail({
  runId, onClose, onAuthError, onChanged,
}: {
  runId: string;
  onClose: () => void;
  onAuthError: (err: unknown) => boolean;
  onChanged: () => void;
}) {
  const [status, setStatus] = useState<AdminImportRunStatus | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      setStatus(await getImportRunStatus(runId, signal));
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (onAuthError(err)) return;
      setError('Could not load this run.');
    }
  }, [runId, onAuthError]);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [refresh]);

  const terminal = status ? TERMINAL.has(status.status) : false;
  const refreshRef = useRef(refresh);
  refreshRef.current = refresh;
  useEffect(() => {
    if (!status || terminal) return;
    const id = setInterval(() => void refreshRef.current(), 1500);
    return () => clearInterval(id);
  }, [status, terminal]);

  async function cancel() {
    try {
      await cancelImportRun(runId);
      await refresh();
      onChanged();
    } catch (err) {
      onAuthError(err);
    }
  }

  // Slice 92: hand the finished run to the idempotent derivatives backfill job.
  const [derivativesMessage, setDerivativesMessage] = useState<string | null>(null);
  async function enqueueDerivatives() {
    setDerivativesMessage(null);
    try {
      const result = await enqueueImportDerivatives(runId);
      setDerivativesMessage(`Derivatives job ${result.jobStatus} — see the Jobs dashboard.`);
    } catch (err) {
      if (onAuthError(err)) return;
      setDerivativesMessage('Could not enqueue the derivatives job.');
    }
  }

  return (
    <div className="admin-import-run-detail" role="region" aria-label="Run detail">
      <div className="admin-header">
        <h4>Run detail</h4>
        <button type="button" className="row-action" onClick={onClose}>Close</button>
      </div>
      {error && <p className="folder-error" role="alert">{error}</p>}
      {!status && !error && <p role="status">Loading…</p>}
      {status && (
        <>
          <RunProgress status={status} terminal={terminal} />
          {!terminal && (
            <div className="admin-import-actions">
              <button type="button" className="row-action" onClick={() => void refresh()}>Refresh</button>
              <CancelButton cancelRequested={status.cancelRequested} onCancel={() => void cancel()} />
            </div>
          )}
          {terminal && (status.status === 'succeeded' || status.status === 'partial') && (
            <div className="admin-import-actions">
              <button type="button" className="row-action" onClick={() => void enqueueDerivatives()}>
                Generate missing derivatives
              </button>
              {derivativesMessage && <span className="muted" role="status">{derivativesMessage}</span>}
            </div>
          )}
          <RunItemsSection runId={runId} onAuthError={onAuthError} />
        </>
      )}
    </div>
  );
}

// ── Slice 92: paginated, filterable manifest items of a run ─────────────────

const ITEM_STATUSES = ['', 'pending', 'importing', 'imported', 'skipped', 'conflict', 'failed', 'cancelled'] as const;
const ITEMS_PAGE_SIZE = 25;

function RunItemsSection({
  runId, onAuthError,
}: {
  runId: string;
  onAuthError: (err: unknown) => boolean;
}) {
  const [items, setItems] = useState<AdminImportItem[] | null>(null);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [statusFilter, setStatusFilter] = useState('');
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);
    try {
      const data = await listImportRunItems(
        runId,
        { status: statusFilter || undefined, page, pageSize: ITEMS_PAGE_SIZE },
        signal,
      );
      setItems(data.items);
      setTotal(data.total);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      if (onAuthError(err)) return;
      setError('Could not load import items.');
    }
  }, [runId, statusFilter, page, onAuthError]);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const pageCount = Math.max(1, Math.ceil(total / ITEMS_PAGE_SIZE));

  return (
    <details className="admin-import-items" data-testid="import-items">
      <summary>Files ({total})</summary>
      <div className="admin-import-items-controls">
        <label>
          Status{' '}
          <select
            value={statusFilter}
            onChange={(e) => { setStatusFilter(e.target.value); setPage(1); }}
            aria-label="Filter items by status"
          >
            {ITEM_STATUSES.map((s) => (
              <option key={s || 'all'} value={s}>{s === '' ? 'all' : s}</option>
            ))}
          </select>
        </label>
        <button type="button" className="row-action" onClick={() => void load()}>Refresh</button>
      </div>
      {error && <p className="folder-error" role="alert">{error}</p>}
      {items === null && !error && <p role="status">Loading items…</p>}
      {items && items.length === 0 && <p className="empty-state">No items match.</p>}
      {items && items.length > 0 && (
        <table className="admin-import-items-table">
          <thead>
            <tr>
              <th scope="col">Path</th>
              <th scope="col">Size</th>
              <th scope="col">Status</th>
              <th scope="col">Detail</th>
              <th scope="col">Attempts</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={item.relativePath}>
                <td><code>{item.kind === 'directory' ? '📁 ' : ''}{item.relativePath}</code></td>
                <td>{item.kind === 'file' ? formatSize(item.sizeBytes) : ''}</td>
                <td>
                  {item.status}
                  {item.conflictCategory === 'already-imported-this-run' && (
                    <span className="muted"> (resumed)</span>
                  )}
                </td>
                <td className="muted">
                  {item.conflictCategory === 'preexisting' && 'pre-existing file with this name'}
                  {item.failureCategory && `${item.failureCategory}${item.failureMessage ? ` — ${item.failureMessage}` : ''}`}
                </td>
                <td>{item.kind === 'file' ? item.attempts : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {pageCount > 1 && (
        <div className="admin-import-items-pager">
          <button
            type="button"
            className="row-action"
            disabled={page <= 1}
            onClick={() => setPage((p) => Math.max(1, p - 1))}
          >
            ← Prev
          </button>
          <span className="muted">Page {page} / {pageCount}</span>
          <button
            type="button"
            className="row-action"
            disabled={page >= pageCount}
            onClick={() => setPage((p) => Math.min(pageCount, p + 1))}
          >
            Next →
          </button>
        </div>
      )}
    </details>
  );
}
