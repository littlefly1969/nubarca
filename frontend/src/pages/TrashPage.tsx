import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  emptyTrash,
  getTrash,
  permanentDeleteFile,
  permanentDeleteFolder,
  restoreFile,
  restoreFolder,
  type EmptyTrashResult,
  type FileTrashSummary,
  type FolderTrashSummary,
  type TrashResponse,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { formatSize } from '../components/format';
import { useI18n } from '../i18n';
import type { I18nContextValue } from '../i18n';

type TranslateFn = I18nContextValue['t'];

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; data: TrashResponse }
  | { kind: 'error'; message: string };

interface BannerMessage {
  tone: 'info' | 'error';
  text: string;
}

export function TrashPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(new Set());
  const [emptying, setEmptying] = useState(false);
  const [banner, setBanner] = useState<BannerMessage | null>(null);

  // The trash listing is owner-scoped server-side, but ProtectedRoute already
  // guarantees we have an authed user when this component renders.
  const load = useCallback(
    async (signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      try {
        const data = await getTrash(signal);
        setStatus({ kind: 'ready', data });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        setStatus({
          kind: 'error',
          message: t('trash.loadError'),
        });
      }
    },
    [invalidateAuth, t],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  function markBusy(id: string, busy: boolean) {
    setBusyIds((prev) => {
      const next = new Set(prev);
      if (busy) next.add(id);
      else next.delete(id);
      return next;
    });
  }

  async function runItemAction(
    id: string,
    operation: () => Promise<unknown>,
    onResult: (outcome: { kind: 'ok' } | { kind: 'banner'; banner: BannerMessage }) => void,
  ) {
    if (busyIds.has(id)) return;
    markBusy(id, true);
    setBanner(null);
    try {
      await operation();
      onResult({ kind: 'ok' });
      await load();
    } catch (err) {
      const message = classifyError(err, t);
      if (message.invalidate) {
        invalidateAuth();
        return;
      }
      onResult({
        kind: 'banner',
        banner: { tone: 'error', text: message.text },
      });
      // Refresh on 404 so the stale row disappears.
      if (err instanceof ApiError && err.status === 404) {
        await load();
      }
    } finally {
      markBusy(id, false);
    }
  }

  async function onRestoreFile(file: FileTrashSummary) {
    await runItemAction(
      file.id,
      () => restoreFile(file.id),
      (outcome) => setBanner(outcome.kind === 'ok'
        ? { tone: 'info', text: t('trash.restored', { name: file.name }) }
        : outcome.banner),
    );
  }

  async function onRestoreFolder(folder: FolderTrashSummary) {
    await runItemAction(
      folder.id,
      () => restoreFolder(folder.id),
      (outcome) => setBanner(outcome.kind === 'ok'
        ? { tone: 'info', text: t('trash.restored', { name: folder.name }) }
        : outcome.banner),
    );
  }

  async function onPermanentDeleteFile(file: FileTrashSummary) {
    const confirmed = window.confirm(
      t('trash.confirmDeleteFile', { name: file.name }),
    );
    if (!confirmed) return;
    await runItemAction(
      file.id,
      () => permanentDeleteFile(file.id),
      (outcome) => setBanner(outcome.kind === 'ok'
        ? { tone: 'info', text: t('trash.deletedItem', { name: file.name }) }
        : outcome.banner),
    );
  }

  async function onPermanentDeleteFolder(folder: FolderTrashSummary) {
    const confirmed = window.confirm(
      t('trash.confirmDeleteFolder', { name: folder.name }),
    );
    if (!confirmed) return;
    await runItemAction(
      folder.id,
      () => permanentDeleteFolder(folder.id),
      (outcome) => setBanner(outcome.kind === 'ok'
        ? { tone: 'info', text: t('trash.deletedFolder', { name: folder.name }) }
        : outcome.banner),
    );
  }

  async function onEmptyTrash() {
    if (status.kind !== 'ready') return;
    const total = status.data.files.length + status.data.folders.length;
    if (total === 0) return;
    const confirmed = window.confirm(
      t('trash.confirmEmpty', { total }),
    );
    if (!confirmed) return;

    setEmptying(true);
    setBanner(null);
    try {
      const result: EmptyTrashResult = await emptyTrash();
      const summary = t('trash.emptySummary', { files: result.deletedFiles, folders: result.deletedFolders });
      const tail = result.conflicts > 0 || result.errors > 0
        ? t('trash.emptyTail', { conflicts: result.conflicts, errors: result.errors })
        : '';
      setBanner({
        tone: result.conflicts > 0 || result.errors > 0 ? 'error' : 'info',
        text: summary + tail,
      });
      await load();
    } catch (err) {
      const message = classifyError(err, t);
      if (message.invalidate) {
        invalidateAuth();
        return;
      }
      setBanner({ tone: 'error', text: message.text });
    } finally {
      setEmptying(false);
    }
  }

  if (state.status !== 'authed') {
    // ProtectedRoute already enforces this; the check keeps TypeScript happy.
    return null;
  }

  const isEmpty = status.kind === 'ready'
    && status.data.files.length === 0
    && status.data.folders.length === 0;

  return (
    <section className="trash-page" aria-busy={status.kind === 'loading'}>
      <header className="trash-header">
        <h2>{t('trash.heading')}</h2>
        <div className="trash-header-actions">
          <button
            type="button"
            className="refresh-button"
            onClick={() => void load()}
            disabled={status.kind === 'loading' || emptying}
          >
            {t('common.refresh')}
          </button>
          <button
            type="button"
            className="destructive-button"
            onClick={() => void onEmptyTrash()}
            disabled={
              status.kind !== 'ready'
              || isEmpty
              || emptying
              || busyIds.size > 0
            }
          >
            {emptying ? t('trash.emptying') : t('trash.emptyTrash')}
          </button>
        </div>
      </header>

      {banner !== null && (
        <p
          className={`trash-banner trash-banner-${banner.tone}`}
          role={banner.tone === 'error' ? 'alert' : 'status'}
        >
          {banner.text}
        </p>
      )}

      {status.kind === 'loading' && (
        <p className="muted" role="status">
          {t('trash.loading')}
        </p>
      )}

      {status.kind === 'error' && (
        <div className="folder-error" role="alert">
          {status.message}
          <button
            type="button"
            className="retry-button"
            onClick={() => void load()}
          >
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {status.kind === 'ready' && isEmpty && (
        <p className="muted">{t('trash.isEmpty')}</p>
      )}

      {status.kind === 'ready' && !isEmpty && (
        <TrashList
          data={status.data}
          busyIds={busyIds}
          disabled={emptying}
          onRestoreFile={(f) => void onRestoreFile(f)}
          onRestoreFolder={(f) => void onRestoreFolder(f)}
          onDeleteFile={(f) => void onPermanentDeleteFile(f)}
          onDeleteFolder={(f) => void onPermanentDeleteFolder(f)}
        />
      )}
    </section>
  );
}

interface TrashListProps {
  data: TrashResponse;
  busyIds: ReadonlySet<string>;
  disabled: boolean;
  onRestoreFile(file: FileTrashSummary): void;
  onRestoreFolder(folder: FolderTrashSummary): void;
  onDeleteFile(file: FileTrashSummary): void;
  onDeleteFolder(folder: FolderTrashSummary): void;
}

function TrashList({
  data,
  busyIds,
  disabled,
  onRestoreFile,
  onRestoreFolder,
  onDeleteFile,
  onDeleteFolder,
}: TrashListProps) {
  const { t, formatDate } = useI18n();
  return (
    <ul className="trash-list" aria-label={t('trash.contentsAria')}>
      {data.folders.map((folder) => (
        <li key={folder.id} className="trash-row">
          <span className="row-icon" aria-hidden="true">📁</span>
          <div className="trash-row-main">
            <span className="trash-row-name" title={folder.name}>
              {folder.name}
            </span>
            <span className="trash-row-meta">
              {t('trash.folderMeta', { date: formatDate(folder.deletedAt) })}
              {folder.parentFolderId !== null && (
                <>
                  {' · '}
                  <span className="trash-row-parent" title={t('trash.originalParentTitle', { id: folder.parentFolderId })}>
                    {t('trash.wasInsideFolder')}
                  </span>
                </>
              )}
            </span>
          </div>
          <div className="trash-row-actions">
            <button
              type="button"
              className="restore-button"
              disabled={disabled || busyIds.has(folder.id)}
              onClick={() => onRestoreFolder(folder)}
            >
              {t('trash.restore')}
            </button>
            <button
              type="button"
              className="destructive-button"
              disabled={disabled || busyIds.has(folder.id)}
              onClick={() => onDeleteFolder(folder)}
            >
              {t('trash.deleteForever')}
            </button>
          </div>
        </li>
      ))}
      {data.files.map((file) => (
        <li key={file.id} className="trash-row">
          <span className="row-icon" aria-hidden="true">📄</span>
          <div className="trash-row-main">
            <span className="trash-row-name" title={file.name}>
              {file.name}
            </span>
            <span className="trash-row-meta">
              {t('trash.fileMeta', { mime: file.mimeType, size: formatSize(file.sizeBytes), date: formatDate(file.deletedAt) })}
              {file.parentFolderId !== null && (
                <>
                  {' · '}
                  <span className="trash-row-parent" title={t('trash.originalParentTitle', { id: file.parentFolderId })}>
                    {t('trash.wasInsideAFolder')}
                  </span>
                </>
              )}
            </span>
          </div>
          <div className="trash-row-actions">
            <button
              type="button"
              className="restore-button"
              disabled={disabled || busyIds.has(file.id)}
              onClick={() => onRestoreFile(file)}
            >
              {t('trash.restore')}
            </button>
            <button
              type="button"
              className="destructive-button"
              disabled={disabled || busyIds.has(file.id)}
              onClick={() => onDeleteFile(file)}
            >
              {t('trash.deleteForever')}
            </button>
          </div>
        </li>
      ))}
    </ul>
  );
}

interface ClassifiedError {
  text: string;
  invalidate: boolean;
}

function classifyError(err: unknown, t: TranslateFn): ClassifiedError {
  if (err instanceof ApiError) {
    if (err.status === 401) {
      return { text: t('trash.errSession'), invalidate: true };
    }
    if (err.status === 404) {
      return {
        text: t('trash.errGone'),
        invalidate: false,
      };
    }
    if (err.status === 409) {
      return {
        text: t('trash.errConflict'),
        invalidate: false,
      };
    }
    if (err.status === 400) {
      const fromBody =
        typeof err.body === 'object' && err.body !== null && 'error' in err.body
          ? (err.body as { error?: unknown }).error
          : undefined;
      return {
        text: typeof fromBody === 'string' && fromBody.length > 0
          ? fromBody
          : t('trash.errRejected'),
        invalidate: false,
      };
    }
  }
  return {
    text: t('trash.errGeneric'),
    invalidate: false,
  };
}
