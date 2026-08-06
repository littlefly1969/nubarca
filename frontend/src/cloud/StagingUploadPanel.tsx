import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  cancelStagingSession,
  createStagingSession,
  deleteStagingSession,
  getStagingConfig,
  getStagingMissing,
  getStagingSession,
  listStagingSessions,
  putStagingChunk,
  startStagingImport,
  submitStagingManifest,
  verifyStagingSession,
  type StagingConfig,
  type StagingMissingItem,
  type StagingSession,
  type StagingVerifyResult,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { formatSize } from '../components/format';
import { useI18n } from '../i18n';
import { runUploadQueue, validateClientRelativePath, type ChunkTask } from '../uploads/chunkUploader';

// Slice 93 — staged (resumable) upload of large folders/files. Bytes go into
// TEMPORARY server-side staging via chunked PUTs; nothing appears in the
// library until the verified session is imported by a background job. The
// server's session/item/chunk state is the source of truth for resume: the
// browser asks what is missing and uploads exactly that.

type ConfigState =
  | { kind: 'loading' }
  | { kind: 'disabled' }
  | { kind: 'ready'; config: StagingConfig }
  | { kind: 'error' };

interface SelectedFile {
  file: File;
  relativePath: string;
  rejectReason: string | null;
}

interface UploadProgress {
  running: boolean;
  paused: boolean;
  totalBytes: number;
  uploadedBytes: number;
  failedChunks: number;
  unmatchedFiles: number;
}

const RESUMABLE = new Set(['manifest_received', 'uploading']);
const UPLOAD_CONCURRENCY = 3;

function selectFiles(list: FileList | null): SelectedFile[] {
  if (!list) return [];
  return Array.from(list).map((file) => {
    const raw = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
    const relativePath = raw && raw.length > 0 ? raw : file.name;
    return { file, relativePath, rejectReason: validateClientRelativePath(relativePath) };
  });
}

export function StagingUploadPanel() {
  const { state } = useAuth();
  const { t } = useI18n();

  const [configState, setConfigState] = useState<ConfigState>({ kind: 'loading' });
  const [selected, setSelected] = useState<SelectedFile[]>([]);
  const [sessionName, setSessionName] = useState('');
  // deleted-content-import-skip: both default ON for this product.
  const [skipPreviouslyDeleted, setSkipPreviouslyDeleted] = useState(true);
  const [skipExistingContent, setSkipExistingContent] = useState(true);
  const [session, setSession] = useState<StagingSession | null>(null);
  const [progress, setProgress] = useState<UploadProgress | null>(null);
  const [verifyResult, setVerifyResult] = useState<StagingVerifyResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [recent, setRecent] = useState<StagingSession[] | null>(null);
  const [needsReselect, setNeedsReselect] = useState(false);

  const pausedRef = useRef(false);
  const cancelledRef = useRef(false);

  // ---- config + recent sessions --------------------------------------------
  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const config = await getStagingConfig(controller.signal);
        setConfigState(config.enabled ? { kind: 'ready', config } : { kind: 'disabled' });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        setConfigState({ kind: 'error' });
      }
    })();
    return () => controller.abort();
  }, []);

  const refreshRecent = useCallback(async () => {
    try {
      setRecent((await listStagingSessions()).sessions);
    } catch {
      // Non-fatal; the list simply stays unavailable.
    }
  }, []);
  useEffect(() => {
    if (configState.kind === 'ready') void refreshRecent();
  }, [configState.kind, refreshRecent]);

  // ---- session polling while the server is busy ------------------------------
  const sessionId = session?.sessionId;
  const sessionStatus = session?.status;
  useEffect(() => {
    if (!sessionId || (sessionStatus !== 'importing' && sessionStatus !== 'verifying')) return;
    const id = setInterval(() => {
      void getStagingSession(sessionId)
        .then(setSession)
        .catch(() => { /* transient; keep last state */ });
    }, 2000);
    return () => clearInterval(id);
  }, [sessionId, sessionStatus]);

  const apiErrorMessage = (err: unknown, fallback: string) =>
    err instanceof ApiError && typeof (err.body as { error?: string })?.error === 'string'
      ? (err.body as { error: string }).error
      : fallback;

  // ---- upload engine ----------------------------------------------------------
  // Pages the server's /missing endpoint, matches each missing item to a local
  // file (by relative path + size, mtime within 2s when known), and uploads
  // exactly the missing chunks with bounded concurrency + retry.
  const uploadMissing = useCallback(async (target: StagingSession, files: SelectedFile[]) => {
    const byPath = new Map(files.map((f) => [f.relativePath, f.file]));
    const tasks: ChunkTask[] = [];
    let unmatched = 0;
    let totalBytes = 0;

    let after = 0;
    while (true) {
      const page = await getStagingMissing(target.sessionId, after);
      for (const item of page.items) {
        const local = matchLocalFile(item, byPath.get(item.relativePath));
        if (local === null) {
          unmatched++;
          continue;
        }
        for (const chunkIndex of item.missingChunks) {
          const start = chunkIndex * page.chunkSizeBytes;
          const end = Math.min(start + page.chunkSizeBytes, item.sizeBytes);
          totalBytes += end - start;
          tasks.push({
            itemId: item.itemId,
            chunkIndex,
            sizeBytes: end - start,
            getBlob: () => local.slice(start, end),
          });
        }
      }
      if (!page.hasMore || page.nextAfterOrdinal === null) break;
      after = page.nextAfterOrdinal;
    }

    pausedRef.current = false;
    cancelledRef.current = false;
    setProgress({
      running: true, paused: false, totalBytes, uploadedBytes: 0,
      failedChunks: 0, unmatchedFiles: unmatched,
    });

    const result = await runUploadQueue({
      tasks,
      concurrency: UPLOAD_CONCURRENCY,
      put: (task, blob) => putStagingChunk(target.sessionId, task.itemId, task.chunkIndex, blob),
      onChunkDone: (task) => setProgress((p) => p && ({
        ...p, uploadedBytes: p.uploadedBytes + task.sizeBytes,
      })),
      onChunkFailed: () => setProgress((p) => p && ({ ...p, failedChunks: p.failedChunks + 1 })),
      isPaused: () => pausedRef.current,
      isCancelled: () => cancelledRef.current,
    });

    setProgress((p) => p && ({ ...p, running: false, paused: false }));
    setSession(await getStagingSession(target.sessionId));
    return result;
  }, []);

  // ---- actions -------------------------------------------------------------------
  async function startNewUpload() {
    if (configState.kind !== 'ready') return;
    const accepted = selected.filter((f) => f.rejectReason === null);
    if (accepted.length === 0) return;
    setError(null);
    setVerifyResult(null);
    try {
      const created = await createStagingSession({
        name: sessionName.trim().length > 0 ? sessionName.trim() : undefined,
        skipPreviouslyDeleted,
        skipExistingContent,
      });
      await submitStagingManifest(
        created.sessionId,
        accepted.map((f) => ({
          relativePath: f.relativePath,
          sizeBytes: f.file.size,
          lastModifiedAt: new Date(f.file.lastModified).toISOString(),
        })),
      );
      const fresh = await getStagingSession(created.sessionId);
      setSession(fresh);
      setNeedsReselect(false);
      await uploadMissing(fresh, accepted);
      void refreshRecent();
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.startError')));
    }
  }

  async function resumeUpload() {
    if (!session) return;
    const accepted = selected.filter((f) => f.rejectReason === null);
    if (accepted.length === 0) return;
    setError(null);
    setNeedsReselect(false);
    try {
      await uploadMissing(session, accepted);
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.resumeError')));
    }
  }

  async function doVerify() {
    if (!session) return;
    setError(null);
    try {
      const result = await verifyStagingSession(session.sessionId);
      setVerifyResult(result);
      setSession(await getStagingSession(session.sessionId));
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.verifyError')));
    }
  }

  async function doImport() {
    if (!session) return;
    setError(null);
    try {
      await startStagingImport(session.sessionId);
      setSession(await getStagingSession(session.sessionId));
      void refreshRecent();
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.importStartError')));
    }
  }

  async function doCancel() {
    if (!session) return;
    cancelledRef.current = true;
    try {
      await cancelStagingSession(session.sessionId);
      setSession(await getStagingSession(session.sessionId));
      void refreshRecent();
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.cancelError')));
    }
  }

  async function doDelete(target: StagingSession) {
    if (!window.confirm(t('staging.confirmDiscard'))) return;
    try {
      await deleteStagingSession(target.sessionId);
      if (session?.sessionId === target.sessionId) {
        setSession(null);
        setProgress(null);
      }
      void refreshRecent();
    } catch (err) {
      setError(apiErrorMessage(err, t('staging.deleteError')));
    }
  }

  function openSession(target: StagingSession) {
    setError(null);
    setVerifyResult(null);
    setProgress(null);
    setSelected([]);
    setSession(target);
    setNeedsReselect(RESUMABLE.has(target.status));
  }

  if (state.status !== 'authed') return null;

  return (
    // The Cloud Functions hub owns the tool title + description, so this panel
    // does not repeat them.
    <section className="staging-upload-page" aria-busy={configState.kind === 'loading'}>
      <p className="muted">{t('staging.intro')}</p>
      <p className="muted staging-standby-note">{t('staging.standbyNote')}</p>

      {configState.kind === 'loading' && <p role="status">{t('staging.loading')}</p>}
      {configState.kind === 'error' && (
        <p className="folder-error" role="alert">{t('staging.configError')}</p>
      )}
      {configState.kind === 'disabled' && (
        <p className="empty-state" role="status">
          {t('staging.disabled')}
        </p>
      )}

      {configState.kind === 'ready' && (
        <>
          {!session && (
            <PickStep
              config={configState.config}
              selected={selected}
              sessionName={sessionName}
              onName={setSessionName}
              skipPreviouslyDeleted={skipPreviouslyDeleted}
              skipExistingContent={skipExistingContent}
              onSkipPreviouslyDeleted={setSkipPreviouslyDeleted}
              onSkipExistingContent={setSkipExistingContent}
              onSelect={(files) => setSelected(selectFiles(files))}
              onStart={() => void startNewUpload()}
              error={error}
            />
          )}

          {session && (
            <SessionCard
              session={session}
              progress={progress}
              verifyResult={verifyResult}
              needsReselect={needsReselect}
              selected={selected}
              error={error}
              onSelect={(files) => setSelected(selectFiles(files))}
              onResume={() => void resumeUpload()}
              onPause={() => { pausedRef.current = true; setProgress((p) => p && ({ ...p, paused: true })); }}
              onUnpause={() => { pausedRef.current = false; setProgress((p) => p && ({ ...p, paused: false })); }}
              onVerify={() => void doVerify()}
              onImport={() => void doImport()}
              onCancel={() => void doCancel()}
              onDelete={() => void doDelete(session)}
              onClose={() => { setSession(null); setProgress(null); setVerifyResult(null); }}
            />
          )}

          <RecentSessions sessions={recent} onOpen={openSession} onDelete={(s) => void doDelete(s)} />
        </>
      )}
    </section>
  );
}

// Matches a missing manifest item against a re-selected local file. Size must
// match exactly; when the manifest recorded a modification time, it must agree
// within 2s (coarse filesystem timestamps). Null = no safe match.
function matchLocalFile(item: StagingMissingItem, local: File | undefined): File | null {
  if (!local) return null;
  if (local.size !== item.sizeBytes) return null;
  if (item.lastModifiedAt) {
    const manifestMs = Date.parse(item.lastModifiedAt);
    if (Number.isFinite(manifestMs) && Math.abs(local.lastModified - manifestMs) > 2000) {
      return null;
    }
  }
  return local;
}

function PickStep({
  config, selected, sessionName, onName,
  skipPreviouslyDeleted, skipExistingContent, onSkipPreviouslyDeleted, onSkipExistingContent,
  onSelect, onStart, error,
}: {
  config: StagingConfig;
  selected: SelectedFile[];
  sessionName: string;
  onName: (v: string) => void;
  skipPreviouslyDeleted: boolean;
  skipExistingContent: boolean;
  onSkipPreviouslyDeleted: (v: boolean) => void;
  onSkipExistingContent: (v: boolean) => void;
  onSelect: (files: FileList | null) => void;
  onStart: () => void;
  error: string | null;
}) {
  const { t } = useI18n();
  const accepted = selected.filter((f) => f.rejectReason === null);
  const rejected = selected.filter((f) => f.rejectReason !== null);
  const oversize = accepted.filter((f) => f.file.size > config.maxFileBytes);
  const totalBytes = accepted.reduce((sum, f) => sum + f.file.size, 0);
  const largest = [...accepted].sort((a, b) => b.file.size - a.file.size).slice(0, 5);
  const tooMany = accepted.length > config.maxFilesPerSession;
  const tooBig = totalBytes > config.maxSessionBytes;
  const startBlocked = accepted.length === 0 || oversize.length > 0 || tooMany || tooBig;

  return (
    <div className="staging-pick" data-testid="staging-pick">
      <h3>{t('staging.newUpload')}</h3>
      <div className="staging-pick-inputs">
        <label>
          {t('staging.nameOptional')}{' '}
          <input
            type="text"
            value={sessionName}
            onChange={(e) => onName(e.target.value)}
            maxLength={200}
            aria-label={t('staging.uploadNameAria')}
          />
        </label>
        <label className="staging-file-label">
          {t('staging.selectFiles')}
          <input
            type="file"
            multiple
            data-testid="staging-files-input"
            onChange={(e) => onSelect(e.target.files)}
          />
        </label>
        <label className="staging-file-label">
          {t('staging.selectFolder')}
          <input
            type="file"
            multiple
            data-testid="staging-folder-input"
            // Non-standard but widely supported attribute enabling folder
            // selection with per-file webkitRelativePath.
            {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
            onChange={(e) => onSelect(e.target.files)}
          />
        </label>
      </div>

      <fieldset className="staging-import-options" data-testid="staging-import-options">
        <legend>{t('staging.importOptions')}</legend>
        <label className="staging-option">
          <input
            type="checkbox"
            checked={skipPreviouslyDeleted}
            onChange={(e) => onSkipPreviouslyDeleted(e.target.checked)}
            data-testid="skip-previously-deleted"
          />
          <span>
            {t('staging.skipDeleted')}
            <small className="muted">{t('staging.skipDeletedHint')}</small>
          </span>
        </label>
        <label className="staging-option">
          <input
            type="checkbox"
            checked={skipExistingContent}
            onChange={(e) => onSkipExistingContent(e.target.checked)}
            data-testid="skip-existing-content"
          />
          <span>
            {t('staging.skipExisting')}
            <small className="muted">{t('staging.skipExistingHint')}</small>
          </span>
        </label>
      </fieldset>

      {selected.length > 0 && (
        <div className="staging-preflight" data-testid="staging-preflight">
          <ul className="admin-import-counts">
            <li>{t('staging.filesCount', { count: accepted.length })}</li>
            <li>{formatSize(totalBytes)}</li>
            <li>{t('staging.chunkSize', { size: formatSize(config.chunkSizeBytes) })}</li>
          </ul>
          {largest.length > 0 && (
            <details>
              <summary>{t('staging.largestFiles')}</summary>
              <ul>
                {largest.map((f) => (
                  <li key={f.relativePath} className="muted">
                    <code>{f.relativePath}</code> — {formatSize(f.file.size)}
                  </li>
                ))}
              </ul>
            </details>
          )}
          {rejected.length > 0 && (
            <div className="folder-error" role="alert">
              {t('staging.rejectedPaths', { count: rejected.length })}
              <ul>
                {rejected.slice(0, 10).map((f) => (
                  <li key={f.relativePath}>
                    <code>{f.relativePath}</code> — {f.rejectReason}
                  </li>
                ))}
              </ul>
            </div>
          )}
          {oversize.length > 0 && (
            <p className="folder-error" role="alert">
              {t('staging.oversize', { count: oversize.length, limit: formatSize(config.maxFileBytes) })}
            </p>
          )}
          {tooMany && (
            <p className="folder-error" role="alert">
              {t('staging.tooMany', { limit: config.maxFilesPerSession })}
            </p>
          )}
          {tooBig && (
            <p className="folder-error" role="alert">
              {t('staging.tooBig', { limit: formatSize(config.maxSessionBytes) })}
            </p>
          )}
        </div>
      )}

      {error && <p className="folder-error" role="alert">{error}</p>}
      <button
        type="button"
        className="row-action-primary"
        disabled={startBlocked}
        onClick={onStart}
      >
        {t('staging.start')}
      </button>
    </div>
  );
}

function SessionCard({
  session, progress, verifyResult, needsReselect, selected, error,
  onSelect, onResume, onPause, onUnpause, onVerify, onImport, onCancel, onDelete, onClose,
}: {
  session: StagingSession;
  progress: UploadProgress | null;
  verifyResult: StagingVerifyResult | null;
  needsReselect: boolean;
  selected: SelectedFile[];
  error: string | null;
  onSelect: (files: FileList | null) => void;
  onResume: () => void;
  onPause: () => void;
  onUnpause: () => void;
  onVerify: () => void;
  onImport: () => void;
  onCancel: () => void;
  onDelete: () => void;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const uploading = progress?.running === true;
  const uploadPct = progress && progress.totalBytes > 0
    ? Math.min(100, Math.round((progress.uploadedBytes / progress.totalBytes) * 100))
    : null;
  const canVerify = !uploading && RESUMABLE.has(session.status);
  const canImport = !uploading && session.status === 'ready_to_import';
  const terminal = ['imported', 'failed', 'cancelled', 'expired'].includes(session.status);

  return (
    <div className="staging-session" data-testid="staging-session" role="region" aria-label={t('staging.sessionAria')}>
      <div className="admin-header">
        <h3>{session.name}</h3>
        <button type="button" className="row-action" onClick={onClose}>{t('common.close')}</button>
      </div>
      <p role="status" aria-live="polite">
        {t('staging.statusLabel')} {session.status}
        {session.import?.phase ? ` — ${session.import.phase}` : ''}
      </p>
      <ul className="admin-import-counts">
        <li>{t('staging.filesLabel')} {session.totalFiles}</li>
        <li>{t('staging.uploadedLabel')} {session.receivedFiles}</li>
        <li>{t('staging.verifiedLabel')} {session.verifiedFiles}</li>
        <li>{t('staging.bytesLabel')} {formatSize(session.receivedBytes)} / {formatSize(session.totalBytes)}</li>
      </ul>

      {progress && (
        <div className="staging-upload-progress" data-testid="upload-progress">
          {uploadPct !== null && (
            <div
              className="admin-import-progressbar"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={uploadPct}
              aria-label={t('staging.uploadProgressAria')}
            >
              <span className="admin-import-progressbar-fill" style={{ width: `${uploadPct}%` }} />
              <span className="admin-import-progressbar-text">
                {formatSize(progress.uploadedBytes)} / {formatSize(progress.totalBytes)} ({uploadPct}%)
              </span>
            </div>
          )}
          {progress.failedChunks > 0 && (
            <p className="folder-error" role="alert">
              {t('staging.failedChunks', { count: progress.failedChunks })}
            </p>
          )}
          {progress.unmatchedFiles > 0 && (
            <p className="folder-error" role="alert">
              {t('staging.unmatched', { count: progress.unmatchedFiles })}
            </p>
          )}
          {uploading && (
            <div className="admin-import-actions">
              {!progress.paused
                ? <button type="button" className="row-action" onClick={onPause}>{t('staging.pause')}</button>
                : <button type="button" className="row-action" onClick={onUnpause}>{t('staging.resumeUpload')}</button>}
              {progress.paused && <span className="muted" role="status">{t('staging.paused')}</span>}
            </div>
          )}
        </div>
      )}

      {needsReselect && !uploading && (
        <div className="staging-reselect" data-testid="staging-reselect">
          <p className="muted">
            {t('staging.reselectHelp')}
          </p>
          <label className="staging-file-label">
            {t('staging.reselectLabel')}
            <input
              type="file"
              multiple
              data-testid="staging-reselect-input"
              {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
              onChange={(e) => onSelect(e.target.files)}
            />
          </label>
          <button
            type="button"
            className="row-action-primary"
            disabled={selected.filter((f) => f.rejectReason === null).length === 0}
            onClick={onResume}
          >
            {t('staging.resumeUpload')}
          </button>
        </div>
      )}

      {verifyResult && (
        <p className="muted" role="status">
          {t('staging.verification', {
            verified: verifyResult.verifiedFiles,
            incomplete: verifyResult.incompleteFiles,
            corrupt: verifyResult.corruptFiles,
          })}
        </p>
      )}

      {session.status === 'importing' && session.import && (
        <p className="muted" role="status">
          {t('staging.importingLine', {
            imported: session.import.importedFiles,
            pending: session.import.pendingFiles,
            failed: session.import.failedFiles,
            conflicts: session.import.conflictFiles,
          })}
        </p>
      )}
      {session.status === 'imported' && (
        <p role="status">
          {session.lastErrorCode === 'partial_import' ? t('staging.importedLinePartial') : t('staging.importedLine')}{' '}
          {session.import && t('staging.importedInLibrary', { count: session.import.importedFiles })}
        </p>
      )}
      {session.import
        && (session.status === 'importing' || session.status === 'imported') && (
        <ul className="admin-import-counts" data-testid="staging-import-summary">
          <li>{t('staging.summImported', { count: session.import.importedFiles })}</li>
          <li>{t('staging.summSkippedDeleted', { count: session.import.skippedPreviouslyDeletedFiles })}</li>
          <li>{t('staging.summSkippedPresent', { count: session.import.skippedAlreadyPresentFiles })}</li>
          <li>{t('staging.summFailed', { count: session.import.failedFiles })}</li>
        </ul>
      )}
      {session.lastErrorMessage && (
        <p className="muted">{session.lastErrorMessage}</p>
      )}
      {error && <p className="folder-error" role="alert">{error}</p>}

      <div className="admin-import-actions">
        {canVerify && (
          <button type="button" className="row-action" onClick={onVerify}>{t('staging.verifyUpload')}</button>
        )}
        {canImport && (
          <button type="button" className="row-action-primary" onClick={onImport}>{t('staging.startImport')}</button>
        )}
        {!terminal && (
          <button type="button" className="row-action row-action-danger" onClick={onCancel}>
            {t('staging.cancelSession')}
          </button>
        )}
        {session.status !== 'importing' && (
          <button type="button" className="row-action row-action-danger" onClick={onDelete}>
            {t('staging.discard')}
          </button>
        )}
      </div>
    </div>
  );
}

function RecentSessions({
  sessions, onOpen, onDelete,
}: {
  sessions: StagingSession[] | null;
  onOpen: (s: StagingSession) => void;
  onDelete: (s: StagingSession) => void;
}) {
  const { t, formatDate } = useI18n();
  if (sessions === null || sessions.length === 0) return null;
  return (
    <section className="staging-recent" aria-label={t('staging.recentAria')}>
      <h3>{t('staging.recentHeading')}</h3>
      <table className="admin-import-runs-table">
        <thead>
          <tr>
            <th scope="col">{t('staging.thName')}</th>
            <th scope="col">{t('staging.thStatus')}</th>
            <th scope="col">{t('staging.thFiles')}</th>
            <th scope="col">{t('staging.thBytes')}</th>
            <th scope="col">{t('staging.thCreated')}</th>
            <th scope="col" aria-label={t('staging.thActions')} />
          </tr>
        </thead>
        <tbody>
          {sessions.map((s) => (
            <tr key={s.sessionId}>
              <td>{s.name}</td>
              <td>{s.status}</td>
              <td>{s.receivedFiles}/{s.totalFiles}</td>
              <td>{formatSize(s.receivedBytes)}</td>
              <td>{formatDate(s.createdAt)}</td>
              <td>
                <button type="button" className="row-action" onClick={() => onOpen(s)}>
                  {RESUMABLE.has(s.status) ? t('staging.resume') : t('staging.open')}
                </button>
                {s.status !== 'importing' && (
                  <button type="button" className="row-action row-action-danger" onClick={() => onDelete(s)}>
                    {t('staging.discard')}
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
