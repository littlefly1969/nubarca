import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { MouseEvent } from 'react';
import {
  deleteFile,
  deleteFolderRecursive,
  moveFile,
  moveFolder,
  type FolderSummary,
} from '@nubarca/api-client';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { getMyStorageUsage, type UserStorageUsage } from '@nubarca/api-client';
import { type BreadcrumbEntry } from './Breadcrumb';
import { formatSize } from './format';
import { CreateFolderForm } from './CreateFolderForm';
import { UploadPanel } from './UploadPanel';
import { MediaViewer, type MediaViewerItem } from './MediaViewer';
import { FilesToolbar } from './files/FilesToolbar';
import { FileItemCard } from './files/FileItemCard';
import { FileItemRow } from './files/FileItemRow';
import { SelectionBar } from './files/SelectionBar';
import { DestinationPicker } from './files/DestinationPicker';
import { MoveToVaultModal } from './files/MoveToVaultModal';
import { DetailsPanel, type DetailsOutcome } from './files/DetailsPanel';
import { OrganizeByDateWizard } from './files/OrganizeByDateWizard';
import { useDirectoryListing } from './files/useDirectoryListing';
import { useSelection } from './files/useSelection';
import {
  downloadUrl,
  entryKey,
  mediaKindOf,
  toEntries,
  type DirectorySortField,
  type Entry,
  type SortDirection,
  type ViewMode,
} from './files/types';

const ROOT_ENTRY: BreadcrumbEntry = { id: null, name: 'Home' };

const VIEW_KEY = 'nc.files.view';
const SORT_KEY = 'nc.files.sort';

interface BannerMessage {
  tone: 'info' | 'error';
  text: string;
}

interface SortPref {
  field: DirectorySortField;
  direction: SortDirection;
}

function readView(): ViewMode {
  try {
    return localStorage.getItem(VIEW_KEY) === 'list' ? 'list' : 'grid';
  } catch {
    return 'grid';
  }
}

function readSort(): SortPref {
  try {
    const raw = localStorage.getItem(SORT_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<SortPref>;
      const field = parsed.field;
      const direction = parsed.direction;
      if (
        (field === 'name' || field === 'created' || field === 'size' || field === 'type') &&
        (direction === 'asc' || direction === 'desc')
      ) {
        return { field, direction };
      }
    }
  } catch {
    // fall through to default
  }
  return { field: 'name', direction: 'asc' };
}

// Files UI v2 orchestrator. Owns navigation (breadcrumb trail), view mode +
// sort (persisted), the listing + selection hooks, the details side panel, the
// full-screen media viewer, bulk actions, and infinite scroll. Leaf rendering
// lives in files/*; this component is the glue + interaction model.
export function FolderBrowser() {
  const { invalidateAuth } = useAuth();
  const [trail, setTrail] = useState<BreadcrumbEntry[]>([ROOT_ENTRY]);
  const [viewMode, setViewMode] = useState<ViewMode>(readView);
  const [sort, setSort] = useState<SortPref>(readSort);
  const [banner, setBanner] = useState<BannerMessage | null>(null);
  const [newFolderOpen, setNewFolderOpen] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [detailsTarget, setDetailsTarget] = useState<Entry | null>(null);
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const [bulkMoveOpen, setBulkMoveOpen] = useState(false);
  const [moveToVaultOpen, setMoveToVaultOpen] = useState(false);
  const [organizeOpen, setOrganizeOpen] = useState(false);
  const [bulkBusy, setBulkBusy] = useState(false);
  const [bulkError, setBulkError] = useState<string | null>(null);
  const [storageReloadToken, setStorageReloadToken] = useState(0);

  const current = trail[trail.length - 1];
  const listing = useDirectoryListing(current.id, sort.field, sort.direction);
  const selection = useSelection();

  const entries = useMemo(
    () => toEntries(listing.folders, listing.files),
    [listing.folders, listing.files],
  );

  // Media files (in listing order) drive the viewer's next/prev within folder.
  const mediaItems = useMemo<MediaViewerItem[]>(
    () =>
      listing.files
        .map((file) => {
          const kind = mediaKindOf(file);
          // The file browser is a FILE surface, not a media gallery: it shows
          // the logical file name, never a gallery title. Passing the name as
          // the display name keeps that deliberate.
          return kind ? { id: file.id, name: file.name, displayName: file.name, kind } : null;
        })
        .filter((m): m is MediaViewerItem => m !== null),
    [listing.files],
  );

  // Prune selection + close panels that point at items no longer present after
  // a reload / navigation.
  useEffect(() => {
    selection.retainExisting(entries);
    setDetailsTarget((prev) => {
      if (prev === null) return null;
      return entries.some((e) => entryKey(e) === entryKey(prev)) ? prev : null;
    });
  }, [entries, selection]);

  // Persist preferences.
  useEffect(() => {
    try { localStorage.setItem(VIEW_KEY, viewMode); } catch { /* ignore */ }
  }, [viewMode]);
  useEffect(() => {
    try { localStorage.setItem(SORT_KEY, JSON.stringify(sort)); } catch { /* ignore */ }
  }, [sort]);

  const refresh = useCallback(() => {
    listing.reload();
    setStorageReloadToken((t) => t + 1);
  }, [listing]);

  function openFolder(folder: FolderSummary) {
    setBanner(null);
    selection.clear();
    setDetailsTarget(null);
    setTrail((prev) => [...prev, { id: folder.id, name: folder.name }]);
  }

  function navigateToTrailIndex(index: number) {
    setBanner(null);
    selection.clear();
    setDetailsTarget(null);
    setTrail((prev) => prev.slice(0, index + 1));
  }

  function onMutationOutcome(message: DetailsOutcome | BannerMessage) {
    setBanner(message);
    selection.clear();
    refresh();
  }

  function openViewerFor(fileId: string) {
    const idx = mediaItems.findIndex((m) => m.id === fileId);
    if (idx >= 0) setViewerIndex(idx);
  }

  // --- interaction model -----------------------------------------------------

  const onActivate = useCallback(
    (entry: Entry, e: MouseEvent) => {
      if (e.shiftKey) {
        selection.selectRange(entries, entryKey(entry));
        return;
      }
      if (e.ctrlKey || e.metaKey) {
        selection.toggle(entryKey(entry));
        return;
      }
      if (entry.kind === 'folder') {
        openFolder(entry.folder);
        return;
      }
      if (mediaKindOf(entry.file)) {
        openViewerFor(entry.file.id);
      } else {
        setDetailsTarget(entry);
      }
    },
    // openFolder/openViewerFor are stable enough for this closure; entries +
    // selection are the meaningful deps.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [entries, selection, mediaItems],
  );

  const onToggleSelect = useCallback(
    (entry: Entry, e: MouseEvent) => {
      if (e.shiftKey) selection.selectRange(entries, entryKey(entry));
      else selection.toggle(entryKey(entry));
    },
    [entries, selection],
  );

  const onDetails = useCallback((entry: Entry) => {
    setBanner(null);
    setDetailsTarget(entry);
  }, []);

  const onLongPress = useCallback(
    (entry: Entry) => selection.toggle(entryKey(entry)),
    [selection],
  );

  function onOpenFromDetails(entry: Entry) {
    if (entry.kind === 'folder') {
      openFolder(entry.folder);
      return;
    }
    if (mediaKindOf(entry.file)) {
      openViewerFor(entry.file.id);
    } else {
      // Non-media file: trigger a download via a transient anchor.
      const a = document.createElement('a');
      a.href = downloadUrl(entry.id);
      a.rel = 'noopener noreferrer';
      document.body.appendChild(a);
      a.click();
      a.remove();
    }
  }

  // --- bulk actions ----------------------------------------------------------

  const selectedEntries = useMemo(
    () => entries.filter((e) => selection.isSelected(entryKey(e))),
    [entries, selection],
  );
  const selectedFileCount = selectedEntries.filter((e) => e.kind === 'file').length;

  function onBulkDownload() {
    // Files only — folders have no archive endpoint. A transient anchor per
    // file lets the browser handle each attachment download.
    for (const entry of selectedEntries) {
      if (entry.kind !== 'file') continue;
      const a = document.createElement('a');
      a.href = downloadUrl(entry.id);
      a.rel = 'noopener noreferrer';
      document.body.appendChild(a);
      a.click();
      a.remove();
    }
  }

  async function onBulkDelete() {
    const count = selectedEntries.length;
    if (count === 0) return;
    const ok = window.confirm(
      `Move ${count} item${count !== 1 ? 's' : ''} to Trash? You can restore them later from Trash.`,
    );
    if (!ok) return;
    setBulkBusy(true);
    let okCount = 0;
    let failed = 0;
    let authLost = false;
    for (const entry of selectedEntries) {
      try {
        if (entry.kind === 'folder') await deleteFolderRecursive(entry.id);
        else await deleteFile(entry.id);
        okCount++;
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          authLost = true;
          break;
        }
        failed++;
      }
    }
    setBulkBusy(false);
    if (authLost) {
      invalidateAuth();
      return;
    }
    onMutationOutcome({
      tone: failed > 0 ? 'error' : 'info',
      text:
        failed > 0
          ? `Moved ${okCount} to Trash; ${failed} could not be moved.`
          : `Moved ${okCount} item${okCount !== 1 ? 's' : ''} to Trash.`,
    });
  }

  async function onBulkMoveChoose(destinationId: string | null) {
    setBulkBusy(true);
    setBulkError(null);
    let okCount = 0;
    let failed = 0;
    let authLost = false;
    for (const entry of selectedEntries) {
      try {
        if (entry.kind === 'folder') await moveFolder(entry.id, destinationId);
        else await moveFile(entry.id, destinationId);
        okCount++;
      } catch (err) {
        if (err instanceof ApiError && err.status === 401) {
          authLost = true;
          break;
        }
        failed++;
      }
    }
    setBulkBusy(false);
    if (authLost) {
      invalidateAuth();
      return;
    }
    setBulkMoveOpen(false);
    onMutationOutcome({
      tone: failed > 0 ? 'error' : 'info',
      text:
        failed > 0
          ? `Moved ${okCount} item${okCount !== 1 ? 's' : ''}; ${failed} skipped (name conflict or invalid destination).`
          : `Moved ${okCount} item${okCount !== 1 ? 's' : ''}.`,
    });
  }

  // --- infinite scroll -------------------------------------------------------

  const loadMoreRef = useRef(listing.loadMore);
  loadMoreRef.current = listing.loadMore;
  const observerRef = useRef<IntersectionObserver | null>(null);
  const canLoadMore = listing.status === 'ready' && listing.hasMore && !listing.loadingMore && !listing.moreError;
  const canLoadMoreRef = useRef(canLoadMore);
  canLoadMoreRef.current = canLoadMore;

  const sentinelRef = useCallback((node: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if (node && typeof IntersectionObserver !== 'undefined') {
      const observer = new IntersectionObserver(
        (entriesObs) => {
          if (entriesObs.some((en) => en.isIntersecting) && canLoadMoreRef.current) {
            loadMoreRef.current();
          }
        },
        { rootMargin: '400px' },
      );
      observer.observe(node);
      observerRef.current = observer;
    }
  }, []);
  useEffect(() => () => observerRef.current?.disconnect(), []);

  const busy = listing.status === 'loading';
  const isEmpty = listing.status === 'ready' && entries.length === 0;

  return (
    <section className="files-view" aria-busy={busy}>
      <FilesToolbar
        trail={trail}
        onNavigate={navigateToTrailIndex}
        viewMode={viewMode}
        onViewModeChange={setViewMode}
        sort={sort.field}
        direction={sort.direction}
        onSortChange={(field, direction) => setSort({ field, direction })}
        newFolderOpen={newFolderOpen}
        onToggleNewFolder={() => setNewFolderOpen((v) => !v)}
        uploadOpen={uploadOpen}
        onToggleUpload={() => setUploadOpen((v) => !v)}
        onOrganize={() => { setBanner(null); setOrganizeOpen(true); }}
        onRefresh={refresh}
        busy={busy}
      />

      <StorageUsage reloadToken={storageReloadToken} />

      {newFolderOpen && (
        <CreateFolderForm
          key={current.id ?? 'root'}
          parentFolderId={current.id}
          disabled={busy}
          onCreated={() => { setNewFolderOpen(false); refresh(); }}
        />
      )}

      {uploadOpen && (
        <UploadPanel
          parentFolderId={current.id}
          disabled={busy}
          onUploadsComplete={refresh}
        />
      )}

      {banner !== null && (
        <p
          className={`folder-banner folder-banner-${banner.tone}`}
          role={banner.tone === 'error' ? 'alert' : 'status'}
        >
          {banner.text}
        </p>
      )}

      <SelectionBar
        count={selection.count}
        fileCount={selectedFileCount}
        busy={bulkBusy}
        onDownload={onBulkDownload}
        onMove={() => { setBulkError(null); setBulkMoveOpen(true); }}
        onMoveToVault={() => setMoveToVaultOpen(true)}
        onDelete={() => void onBulkDelete()}
        onClear={() => selection.clear()}
      />

      <div className="files-body">
        <div className="files-main">
          {listing.status === 'loading' && <SkeletonGrid viewMode={viewMode} />}

          {listing.status === 'error' && (
            <div className="folder-error" role="alert">
              {listing.missing ? 'This folder no longer exists.' : 'Could not load this folder. Please try again.'}
              {listing.missing ? (
                <button type="button" className="files-action" onClick={() => navigateToTrailIndex(0)}>
                  Go to Home
                </button>
              ) : (
                <button type="button" className="files-action" onClick={() => listing.reload()}>
                  Try again
                </button>
              )}
            </div>
          )}

          {isEmpty && <p className="muted folder-empty">This folder is empty.</p>}

          {listing.status === 'ready' && entries.length > 0 && (
            <>
              {viewMode === 'grid' ? (
                <ul className="files-grid" aria-label="Folder contents">
                  {entries.map((entry) => (
                    <FileItemCard
                      key={entryKey(entry)}
                      entry={entry}
                      selected={selection.isSelected(entryKey(entry))}
                      selectionActive={selection.count > 0}
                      onActivate={onActivate}
                      onToggleSelect={onToggleSelect}
                      onDetails={onDetails}
                      onLongPress={onLongPress}
                    />
                  ))}
                </ul>
              ) : (
                <ul className="files-list" aria-label="Folder contents">
                  {entries.map((entry) => (
                    <FileItemRow
                      key={entryKey(entry)}
                      entry={entry}
                      selected={selection.isSelected(entryKey(entry))}
                      selectionActive={selection.count > 0}
                      onActivate={onActivate}
                      onToggleSelect={onToggleSelect}
                      onDetails={onDetails}
                      onLongPress={onLongPress}
                    />
                  ))}
                </ul>
              )}

              {listing.loadingMore && <p className="muted" role="status">Loading more…</p>}
              {listing.moreError && (
                <div className="folder-error" role="alert">
                  Could not load more.
                  <button type="button" className="files-action" onClick={() => listing.loadMore()}>
                    Try again
                  </button>
                </div>
              )}
              {listing.hasMore && <div ref={sentinelRef} className="files-scroll-sentinel" aria-hidden="true" />}
            </>
          )}
        </div>

        {detailsTarget !== null && (
          <DetailsPanel
            key={entryKey(detailsTarget)}
            entry={detailsTarget}
            currentParentId={current.id}
            onClose={() => setDetailsTarget(null)}
            onOutcome={onMutationOutcome}
            onOpen={onOpenFromDetails}
          />
        )}
      </div>

      {viewerIndex !== null && mediaItems.length > 0 && (
        <MediaViewer
          items={mediaItems}
          index={Math.min(viewerIndex, mediaItems.length - 1)}
          onClose={() => setViewerIndex(null)}
          onIndexChange={setViewerIndex}
          onNearEnd={() => { if (canLoadMoreRef.current) loadMoreRef.current(); }}
        />
      )}

      {organizeOpen && (
        <OrganizeByDateWizard
          currentFolderId={current.id}
          currentFolderName={current.name}
          selectedFileIds={selectedEntries.filter((e) => e.kind === 'file').map((e) => e.id)}
          onClose={() => setOrganizeOpen(false)}
          onDone={(message) => { setBanner(message); selection.clear(); refresh(); }}
        />
      )}

      {moveToVaultOpen && (
        <MoveToVaultModal
          fileIds={selectedEntries.filter((e) => e.kind === 'file').map((e) => e.id)}
          folderIds={selectedEntries.filter((e) => e.kind === 'folder').map((e) => e.id)}
          onCancel={() => setMoveToVaultOpen(false)}
          onDone={(message) => {
            setMoveToVaultOpen(false);
            setBanner(message);
            selection.clear();
            refresh();
          }}
        />
      )}

      {bulkMoveOpen && (
        <div className="files-modal" role="dialog" aria-modal="true" aria-label="Move selected items">
          <div className="files-modal-backdrop" onClick={() => !bulkBusy && setBulkMoveOpen(false)} />
          <div className="files-modal-content">
            <DestinationPicker
              title={`Move ${selectedEntries.length} item${selectedEntries.length !== 1 ? 's' : ''} to:`}
              excludeFolderIds={selectedEntries.filter((e) => e.kind === 'folder').map((e) => e.id)}
              currentParentId={current.id}
              busy={bulkBusy}
              error={bulkError}
              onChoose={(dest) => void onBulkMoveChoose(dest)}
              onCancel={() => setBulkMoveOpen(false)}
            />
          </div>
        </div>
      )}
    </section>
  );
}

// Loading skeleton matching the active view so the layout doesn't jump when the
// real items arrive.
function SkeletonGrid({ viewMode }: { viewMode: ViewMode }) {
  const placeholders = Array.from({ length: viewMode === 'grid' ? 12 : 8 });
  return (
    <ul className={viewMode === 'grid' ? 'files-grid' : 'files-list'} aria-hidden="true">
      {placeholders.map((_, i) => (
        <li key={i} className={viewMode === 'grid' ? 'file-card skeleton' : 'file-list-row skeleton'}>
          <span className="skeleton-thumb" />
          <span className="skeleton-line" />
        </li>
      ))}
    </ul>
  );
}

// Caller's own logical storage usage. Re-fetches whenever `reloadToken`
// changes. Renders nothing on error so a transient accounting hiccup never
// blocks the browser. Never shows any other user's figures.
function StorageUsage({ reloadToken }: { reloadToken: number }) {
  const [usage, setUsage] = useState<UserStorageUsage | null>(null);
  const { invalidateAuth } = useAuth();

  useEffect(() => {
    const controller = new AbortController();
    getMyStorageUsage(controller.signal)
      .then(setUsage)
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        // Non-fatal: leave the previous value (or nothing) in place.
      });
    return () => controller.abort();
  }, [reloadToken, invalidateAuth]);

  if (usage === null) return null;

  const fileLabel = usage.fileCount === 1 ? 'file' : 'files';
  return (
    <p className="storage-usage" aria-label="Storage usage">
      {usage.quotaBytes !== null ? (
        <>
          Using {formatSize(usage.usedBytes)} of {formatSize(usage.quotaBytes)}{' '}
          ({usage.fileCount} {fileLabel})
          {usage.remainingBytes !== null && (
            <span className="storage-usage-remaining"> · {formatSize(usage.remainingBytes)} free</span>
          )}
        </>
      ) : (
        <>Using {formatSize(usage.usedBytes)} ({usage.fileCount} {fileLabel})</>
      )}
    </p>
  );
}
