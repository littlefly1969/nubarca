import type { VaultFile, VaultFolder } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { VaultMediaCard } from './VaultMediaCard';

// Visual grid for the unlocked Private area. Folders stay navigable (rows/cards
// distinct from media, no preview needed); files render as media cards. Only at
// the vault root are items selectable for restore (mirrors the existing
// move-out rule). This is a dedicated, small Vault component — it never reuses
// the normal gallery, which would issue unauthorized `/api/files` requests.

export function VaultMediaGrid({
  token,
  folders,
  files,
  selectable,
  selected,
  onToggleFolder,
  onToggleFile,
  onOpenFolder,
  onOpenFile,
  onExpired,
}: {
  token: string;
  folders: VaultFolder[];
  files: VaultFile[];
  selectable: boolean;
  selected: ReadonlySet<string>;
  onToggleFolder: (id: string) => void;
  onToggleFile: (id: string) => void;
  onOpenFolder: (id: string, name: string) => void;
  onOpenFile: (file: VaultFile, trigger: HTMLElement) => void;
  onExpired: () => void;
}) {
  const { t } = useI18n();

  return (
    <div className="vault-grid" aria-label={t('vault.contentsAria')}>
      {folders.map((f) => (
        <div key={`folder:${f.id}`} className="vault-folder-card">
          {selectable && (
            <input
              type="checkbox"
              className="vault-card-select"
              aria-label={t('vault.selectFolder', { name: f.name })}
              checked={selected.has(`folder:${f.id}`)}
              onChange={() => onToggleFolder(f.id)}
            />
          )}
          <button
            type="button"
            className="vault-folder-open"
            onClick={() => onOpenFolder(f.id, f.name)}
          >
            <span className="vault-folder-icon" aria-hidden="true">
              📁
            </span>
            <span className="vault-folder-name">{f.name}</span>
          </button>
        </div>
      ))}

      {files.map((file) => (
        <VaultMediaCard
          key={`file:${file.id}`}
          token={token}
          file={file}
          selectable={selectable}
          selected={selected.has(`file:${file.id}`)}
          onToggleSelect={() => onToggleFile(file.id)}
          onOpen={onOpenFile}
          onExpired={onExpired}
        />
      ))}
    </div>
  );
}
