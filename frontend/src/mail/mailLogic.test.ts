import { describe, expect, it } from 'vitest';
import { buildFolderTree, folderPathOf } from './folderTree';
import { EMPTY_FILTERS, filtersToApiQuery, filtersToParams, localDayStartIso, readFilters, readView } from './mailQuery';
import { buildMailSrcDoc, MAIL_BODY_CSP } from './MailBody';
import { FOLDERS } from './mail.test.fixtures';
import type { MailFolder } from './types';

describe('buildFolderTree', () => {
  it('nests folders by parentId, keeping the server order among siblings', () => {
    const roots = buildFolderTree(FOLDERS);
    expect(roots.map((r) => r.folder.id)).toEqual(['f-root']);
    const [root] = roots;
    expect(root.children.map((c) => c.folder.id)).toEqual(['f-inbox', 'f-sent']);
    expect(root.children[0].children.map((c) => c.folder.id)).toEqual(['f-clients']);
    expect(root.children[1].children).toEqual([]);
  });

  it('does not depend on parents arriving first', () => {
    const roots = buildFolderTree([...FOLDERS].reverse());
    expect(roots.map((r) => r.folder.id)).toEqual(['f-root']);
    expect(roots[0].children.find((c) => c.folder.id === 'f-inbox')?.children[0].folder.id).toBe('f-clients');
  });

  it('keeps a folder whose parent is missing, as a root', () => {
    const orphan: MailFolder = { id: 'f-x', parentId: 'f-missing', name: 'Orfana', depth: 3, messageCount: 1 };
    const roots = buildFolderTree([...FOLDERS, orphan]);
    expect(roots.map((r) => r.folder.id)).toEqual(['f-root', 'f-x']);
  });

  it('survives a parent cycle without losing either folder', () => {
    const roots = buildFolderTree([
      { id: 'a', parentId: 'b', name: 'A', depth: 1, messageCount: 0 },
      { id: 'b', parentId: 'a', name: 'B', depth: 1, messageCount: 0 },
    ]);
    const all = roots.flatMap(function walk(n): string[] { return [n.folder.id, ...n.children.flatMap(walk)]; });
    expect(all.sort()).toEqual(['a', 'b']);
  });

  it('gives the path of a folder from its root', () => {
    expect(folderPathOf(FOLDERS, 'f-clients')).toEqual(['Top of Outlook data file', 'Posta in arrivo', 'Clienti']);
    expect(folderPathOf(FOLDERS, 'nope')).toEqual([]);
  });
});

describe('mail query', () => {
  it('reads and writes the address-bar form', () => {
    const params = new URLSearchParams('view=documents&archive=arc-1&q=fattura&since=2024-03-01&until=2024-03-31&attachments=1&from=ada');
    const filters = readFilters(params);
    expect(readView(params)).toBe('documents');
    expect(filters).toMatchObject({ archiveId: 'arc-1', q: 'fattura', from: 'ada', since: '2024-03-01', until: '2024-03-31', hasAttachments: true });
    expect(filtersToParams('documents', filters).toString())
      .toBe('view=documents&archive=arc-1&q=fattura&from=ada&since=2024-03-01&until=2024-03-31&attachments=1');
  });

  it('ignores a malformed date and an unknown view', () => {
    const params = new URLSearchParams('since=yesterday&view=nope');
    expect(readFilters(params).since).toBe('');
    expect(readView(params)).toBe('mail');
  });

  it('builds the API query: after inclusive, before exclusive (the day after "Al")', () => {
    const qs = filtersToApiQuery({
      ...EMPTY_FILTERS, archiveId: 'arc-1', folderId: 'f-inbox', q: 'x', to: 'bob', subject: 'rel',
      since: '2024-03-01', until: '2024-03-31', hasAttachments: true,
    });
    expect(qs.get('archiveId')).toBe('arc-1');
    expect(qs.get('folderId')).toBe('f-inbox');
    expect(qs.get('q')).toBe('x');
    expect(qs.get('to')).toBe('bob');
    expect(qs.get('subject')).toBe('rel');
    expect(qs.get('after')).toBe(new Date(2024, 2, 1).toISOString());
    expect(qs.get('before')).toBe(new Date(2024, 3, 1).toISOString());
    expect(qs.get('hasAttachments')).toBe('true');
    expect(qs.get('limit')).toBe('50');
    expect(qs.has('from')).toBe(false);
  });

  it('crosses month ends correctly', () => {
    expect(localDayStartIso('2024-02-29', 1)).toBe(new Date(2024, 2, 1).toISOString());
  });
});

describe('mail body document', () => {
  it('puts the CSP meta before the message markup and forbids remote loads', () => {
    const doc = buildMailSrcDoc('<p>hi</p>');
    expect(doc.indexOf('Content-Security-Policy')).toBeLessThan(doc.indexOf('<p>hi</p>'));
    expect(doc).toContain(`content="${MAIL_BODY_CSP}"`);
    expect(MAIL_BODY_CSP).toContain("default-src 'none'");
    expect(MAIL_BODY_CSP).toContain("img-src 'self' data:");
    expect(MAIL_BODY_CSP).not.toMatch(/script-src|https?:/);
  });
});
