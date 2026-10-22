import type { MailFolder } from './types';

export interface MailFolderNode {
  folder: MailFolder;
  children: MailFolderNode[];
}

// Builds the PST's original folder tree from the flat list the API returns.
//
// The server sends parents first, but the tree must not depend on it: a folder
// whose parent is missing from the list (a corrupt parent the engine could not
// read) becomes a root rather than disappearing. Sibling order is the order the
// server sent, which is the PST's own. A cycle cannot occur through parentId
// links that reach a root, and anything that does not reach one is attached at
// the top level once.
export function buildFolderTree(folders: readonly MailFolder[]): MailFolderNode[] {
  const nodes = new Map<string, MailFolderNode>();
  for (const folder of folders) {
    if (!nodes.has(folder.id)) nodes.set(folder.id, { folder, children: [] });
  }
  const roots: MailFolderNode[] = [];
  for (const node of nodes.values()) {
    const parentId = node.folder.parentId;
    const parent = parentId !== null && parentId !== node.folder.id ? nodes.get(parentId) : undefined;
    if (parent && !isAncestor(node, parent, nodes)) parent.children.push(node);
    else roots.push(node);
  }
  return roots;
}

// Whether `node` is already an ancestor of `candidateParent` (would be a cycle).
function isAncestor(node: MailFolderNode, candidateParent: MailFolderNode, nodes: Map<string, MailFolderNode>): boolean {
  let cursor: MailFolderNode | undefined = candidateParent;
  const seen = new Set<string>();
  while (cursor) {
    if (cursor === node) return true;
    if (seen.has(cursor.folder.id)) return true;
    seen.add(cursor.folder.id);
    const pid: string | null = cursor.folder.parentId;
    cursor = pid !== null ? nodes.get(pid) : undefined;
  }
  return false;
}

// The names from a root down to `folderId`, for a breadcrumb or a card.
export function folderPathOf(folders: readonly MailFolder[], folderId: string): string[] {
  const byId = new Map(folders.map((f) => [f.id, f]));
  const path: string[] = [];
  const seen = new Set<string>();
  let cursor = byId.get(folderId);
  while (cursor && !seen.has(cursor.id)) {
    seen.add(cursor.id);
    path.unshift(cursor.name);
    cursor = cursor.parentId !== null ? byId.get(cursor.parentId) : undefined;
  }
  return path;
}
