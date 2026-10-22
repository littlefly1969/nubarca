import { useMemo, useState } from 'react';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { buildFolderTree, type MailFolderNode } from './folderTree';
import type { MailFolder } from './types';

interface MailFolderTreeProps {
  folders: readonly MailFolder[];
  selectedId: string | null;
  onSelect: (folderId: string | null) => void;
}

// The PST's original folder tree, collapsible. Plain nested lists of buttons:
// every folder is a Tab stop, Enter/Space selects it, and each expander says
// whether it is open (aria-expanded). Top-level folders start open, so a PST
// shows "Top of Outlook data file" with its Inbox/Sent/… children visible.
export function MailFolderTree({ folders, selectedId, onSelect }: MailFolderTreeProps) {
  const { t } = useI18n();
  const roots = useMemo(() => buildFolderTree(folders), [folders]);
  const [toggled, setToggled] = useState<ReadonlySet<string>>(new Set());

  const isOpen = (node: MailFolderNode, level: number) =>
    (level < 1) !== toggled.has(node.folder.id);

  const toggle = (id: string) =>
    setToggled((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const renderNode = (node: MailFolderNode, level: number) => {
    const { folder } = node;
    const open = isOpen(node, level);
    const hasChildren = node.children.length > 0;
    const selected = folder.id === selectedId;
    return (
      <li key={folder.id} className="mail-folder" data-testid="mail-folder">
        <div className={selected ? 'mail-folder-row is-selected' : 'mail-folder-row'}>
          {hasChildren ? (
            <button
              type="button"
              className="mail-folder-toggle"
              aria-expanded={open}
              aria-label={open
                ? t('mail.folders.collapse', { name: folder.name })
                : t('mail.folders.expand', { name: folder.name })}
              onClick={() => toggle(folder.id)}
            >
              <Icon name={open ? 'chevron-down' : 'chevron-right'} />
            </button>
          ) : <span className="mail-folder-toggle-spacer" aria-hidden="true" />}
          <button
            type="button"
            className="mail-folder-select"
            aria-current={selected ? 'true' : undefined}
            onClick={() => onSelect(folder.id)}
          >
            <span className="mail-folder-name">{folder.name}</span>
            {folder.messageCount > 0 && <span className="mail-folder-count">{folder.messageCount}</span>}
          </button>
        </div>
        {hasChildren && open && <ul className="mail-folder-children">{node.children.map((c) => renderNode(c, level + 1))}</ul>}
      </li>
    );
  };

  return (
    <nav className="mail-folders" aria-label={t('mail.folders.label')}>
      <button
        type="button"
        className={selectedId === null ? 'mail-folder-all is-selected' : 'mail-folder-all'}
        aria-current={selectedId === null ? 'true' : undefined}
        onClick={() => onSelect(null)}
      >
        {t('mail.folders.all')}
      </button>
      {roots.length === 0
        ? <p className="muted">{t('mail.folders.empty')}</p>
        : <ul className="mail-folder-children mail-folder-root">{roots.map((r) => renderNode(r, 0))}</ul>}
    </nav>
  );
}
