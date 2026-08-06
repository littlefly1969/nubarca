import { useEffect, useState } from 'react';
import {
  ApiError,
  getFileMetadata,
  writeFileDateTaken,
  type FileMetadata,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { AlbumPickerModal } from '../../gallery/AlbumPickerModal';
import { MediaMetadataEditor } from './MediaMetadataEditor';
import { MediaMetadataView } from './MediaMetadataView';

// The single metadata panel for BOTH galleries.
//
// It owns the whole interaction: load, loading/error/ready state, edit, save,
// local refresh, 401 handling, add-to-album and the DateTaken write. Photos and
// videos differ only in which rows and actions the view renders — see
// MediaMetadataView — so there is exactly one editor.
//
// When the host already holds the metadata document (the media viewer loads it
// for the open item to build its summary line), it passes it as `initialData`
// and this panel renders immediately without a second request.

export type MediaKind = 'image' | 'video';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; data: FileMetadata }
  | { kind: 'error' };

export interface MediaMetadataPanelProps {
  fileId: string;
  kind: MediaKind;
  // Already-loaded document for `fileId`. When present the panel skips its own
  // fetch; when it becomes available later (host still loading) the panel
  // adopts it.
  initialData?: FileMetadata | null;
  // The host's load already failed, so there is nothing to wait for.
  loadError?: boolean;
  // Fired whenever the stored metadata changes (save, DateTaken write). Hosts
  // use it to patch the matching item in their loaded page immutably, so a title
  // edit shows on the card and in the viewer header at once.
  onMetadataChanged?: (fileId: string, metadata: FileMetadata) => void;
  // Photos only: apply the library's similar-image anchor filter.
  onFindSimilarInLibrary?: () => void;
  // Photos only: open the dedicated Similar Photos Explorer.
  onExploreSimilar?: () => void;
}

export function MediaMetadataPanel({
  fileId,
  kind,
  initialData,
  loadError,
  onMetadataChanged,
  onFindSimilarInLibrary,
  onExploreSimilar,
}: MediaMetadataPanelProps) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>(
    () => (initialData ? { kind: 'ready', data: initialData } : { kind: 'loading' }),
  );
  const [editing, setEditing] = useState(false);
  const [writing, setWriting] = useState(false);
  const [writeError, setWriteError] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);

  // Adopt a host-supplied document (first render, a later arrival, or a switch
  // to another file) without issuing a request of our own.
  useEffect(() => {
    if (!initialData) return;
    setStatus({ kind: 'ready', data: initialData });
    setEditing(false);
    setWriteError(null);
  }, [initialData]);

  useEffect(() => {
    if (loadError !== true) return;
    setStatus({ kind: 'error' });
  }, [loadError]);

  useEffect(() => {
    // The host is providing the document (or has already failed) — no fetch.
    if (initialData || loadError === true) return;
    const controller = new AbortController();
    setStatus({ kind: 'loading' });
    setEditing(false);
    setWriteError(null);
    void (async () => {
      try {
        const data = await getFileMetadata(fileId, controller.signal);
        setStatus({ kind: 'ready', data });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error' });
      }
    })();
    return () => controller.abort();
    // `initialData` is intentionally not a dependency here: its own effect above
    // adopts it, and re-running this one would cancel nothing useful.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fileId, loadError, invalidateAuth]);

  // One place where a fresh document is adopted, so every mutation path
  // notifies the host identically.
  function adopt(next: FileMetadata) {
    setStatus({ kind: 'ready', data: next });
    onMetadataChanged?.(fileId, next);
  }

  if (status.kind === 'loading') {
    return (
      <section className="lightbox-metadata" aria-label={t('mediaMeta.panelAria')}>
        <p className="muted" role="status">{t('gallery.loadingDetails')}</p>
      </section>
    );
  }

  if (status.kind === 'error') {
    return (
      <section className="lightbox-metadata" aria-label={t('mediaMeta.panelAria')}>
        <p className="muted" role="alert">{t('gallery.metadataLoadError')}</p>
      </section>
    );
  }

  const data = status.data;

  if (editing) {
    return (
      <MediaMetadataEditor
        data={data}
        onCancel={() => setEditing(false)}
        onSaved={(next) => { adopt(next); setEditing(false); }}
      />
    );
  }

  async function onWriteDateTaken() {
    const confirmed = window.confirm(t('gallery.writeConfirm'));
    if (!confirmed) return;
    setWriting(true);
    setWriteError(null);
    try {
      adopt(await writeFileDateTaken(fileId));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && (err.status === 400 || err.status === 415)) {
        const body = err.body as { error?: unknown } | null;
        setWriteError(
          typeof body?.error === 'string' && body.error.length > 0
            ? body.error
            : t('gallery.writeCantType'),
        );
        return;
      }
      setWriteError(t('gallery.writeError'));
    } finally {
      setWriting(false);
    }
  }

  return (
    <>
      <MediaMetadataView
        data={data}
        kind={kind}
        onEdit={() => setEditing(true)}
        onWriteDateTaken={onWriteDateTaken}
        writing={writing}
        writeError={writeError}
        onAddToAlbum={() => setPickerOpen(true)}
        onFindSimilarInLibrary={onFindSimilarInLibrary}
        onExploreSimilar={onExploreSimilar}
      />
      {pickerOpen && (
        // The same accessible picker the bulk selection bar uses — choose an
        // existing album or create one, with success/error feedback in-dialog.
        <AlbumPickerModal fileItemIds={[fileId]} onClose={() => setPickerOpen(false)} />
      )}
    </>
  );
}
