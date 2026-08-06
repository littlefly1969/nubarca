import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  listVaultFolder,
  listVaultRoot,
  lockVault,
  vaultMoveOut,
  type VaultFile,
  type VaultListing,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PrivateVaultAccessForm } from '../vault/PrivateVaultAccessForm';
import { VaultMediaGrid } from '../vault/VaultMediaGrid';
import { VaultImageViewer } from '../vault/VaultImageViewer';

// Private tab (v0). Exclusion-first: while LOCKED this page reveals nothing —
// no counts, names, folders, or thumbnails, and no signal about whether the
// vault holds content or even whether it's empty. Access requires a password;
// unlocking returns a short-lived token kept ONLY in component memory (never
// localStorage/sessionStorage/URL), so a refresh or a new tab re-locks.

type Crumb = { id: string | null; name: string };

export function PrivateVaultPage() {
  const { t } = useI18n();
  // `null` token = locked. Kept in state (memory) only.
  const [token, setToken] = useState<string | null>(null);
  const tokenRef = useRef<string | null>(null);
  tokenRef.current = token;

  // Best-effort lock when the page unmounts so the token doesn't linger.
  useEffect(
    () => () => {
      if (tokenRef.current) void lockVault(tokenRef.current).catch(() => {});
    },
    [],
  );

  const onUnlocked = useCallback((t: string) => setToken(t), []);
  const onLock = useCallback(() => {
    const t = tokenRef.current;
    setToken(null);
    if (t) void lockVault(t).catch(() => {});
  }, []);

  return (
    <section className="admin-page private-vault">
      <div className="admin-header">
        <h2>{t('vault.heading')}</h2>
        {token && (
          <button type="button" className="row-action" onClick={onLock} data-testid="vault-lock">
            {t('vault.lock')}
          </button>
        )}
      </div>

      {token ? (
        <VaultBrowser token={token} onExpired={onLock} />
      ) : (
        <PrivateVaultAccessForm onUnlocked={onUnlocked} />
      )}
    </section>
  );
}

// ── Unlocked: browse + restore ──────────────────────────────────────────────
function VaultBrowser({ token, onExpired }: { token: string; onExpired(): void }) {
  const { t, tn } = useI18n();
  const [trail, setTrail] = useState<Crumb[]>([{ id: null, name: 'Private' }]);
  const [listing, setListing] = useState<VaultListing | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  // Which file the photo/video viewer is showing (null = closed). Kept in
  // memory only; changing folder or restoring closes it.
  const [viewerStartId, setViewerStartId] = useState<string | null>(null);

  const current = trail[trail.length - 1];
  const atRoot = current.id === null;

  const load = useCallback(
    async (folderId: string | null) => {
      setStatus('loading');
      setSelected(new Set());
      try {
        const data =
          folderId === null
            ? await listVaultRoot(token)
            : await listVaultFolder(token, folderId);
        setListing(data);
        setStatus('ready');
      } catch (err: unknown) {
        if (err instanceof ApiError && err.status === 401) {
          onExpired();
          return;
        }
        setStatus('error');
      }
    },
    [token, onExpired],
  );

  useEffect(() => {
    void load(current.id);
  }, [load, current.id]);

  function openFolder(id: string, name: string) {
    setViewerStartId(null);
    setTrail((t) => [...t, { id, name }]);
  }
  function goToCrumb(index: number) {
    setViewerStartId(null);
    setTrail((t) => t.slice(0, index + 1));
  }
  const openFile = useCallback((file: VaultFile) => setViewerStartId(file.id), []);
  function toggle(key: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  async function restoreSelected() {
    if (busy || selected.size === 0) return;
    setBusy(true);
    setNotice(null);
    const fileIds: string[] = [];
    const folderIds: string[] = [];
    for (const key of selected) {
      const [kind, id] = key.split(':');
      if (kind === 'file') fileIds.push(id);
      else folderIds.push(id);
    }
    try {
      const result = await vaultMoveOut(token, { fileIds, folderIds });
      setNotice(tn(result.movedFiles + result.movedFolders, 'vault.restored'));
      setViewerStartId(null);
      await load(null);
      setTrail([{ id: null, name: 'Private' }]);
    } catch (err: unknown) {
      if (err instanceof ApiError && err.status === 401) {
        onExpired();
        return;
      }
      setNotice(t('vault.restoreError'));
      setBusy(false);
    }
    setBusy(false);
  }

  return (
    <div className="vault-browser">
      <nav className="vault-breadcrumb" aria-label={t('vault.pathAria')}>
        {trail.map((c, i) => (
          <span key={`${c.id ?? 'root'}-${i}`}>
            {i > 0 && <span className="vault-breadcrumb-sep"> / </span>}
            <button
              type="button"
              className="vault-crumb"
              onClick={() => goToCrumb(i)}
              disabled={i === trail.length - 1}
            >
              {i === 0 ? t('vault.rootCrumb') : c.name}
            </button>
          </span>
        ))}
      </nav>

      {notice && (
        <div className="folder-banner folder-banner-info" role="status">
          {notice}
        </div>
      )}

      {atRoot && selected.size > 0 && (
        <div className="vault-selection-bar">
          <span>{t('vault.selected', { count: selected.size })}</span>
          <button
            type="button"
            className="row-action-primary"
            onClick={() => void restoreSelected()}
            disabled={busy}
            data-testid="vault-restore"
          >
            {busy ? t('vault.restoring') : t('vault.restoreToLibrary')}
          </button>
          <button type="button" className="row-action" onClick={() => setSelected(new Set())}>
            {t('common.clear')}
          </button>
        </div>
      )}

      {status === 'loading' && <p className="muted">{t('common.loading')}</p>}
      {status === 'error' && (
        <div className="folder-error" role="alert">
          {t('vault.loadError')}
          <button type="button" className="files-action" onClick={() => void load(current.id)}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}

      {status === 'ready' && listing && (
        <>
          {listing.folders.length === 0 && listing.files.length === 0 && (
            <p className="muted vault-empty">{t('vault.empty')}</p>
          )}
          <VaultMediaGrid
            token={token}
            folders={listing.folders}
            files={listing.files}
            selectable={atRoot}
            selected={selected}
            onToggleFolder={(id) => toggle(`folder:${id}`)}
            onToggleFile={(id) => toggle(`file:${id}`)}
            onOpenFolder={openFolder}
            onOpenFile={openFile}
            onExpired={onExpired}
          />
        </>
      )}

      {viewerStartId && listing && (
        <VaultImageViewer
          token={token}
          files={listing.files}
          startId={viewerStartId}
          onClose={() => setViewerStartId(null)}
          onExpired={onExpired}
        />
      )}
    </div>
  );
}
