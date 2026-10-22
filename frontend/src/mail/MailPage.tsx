import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { listMailArchives } from './mailApi';
import {
  EMPTY_FILTERS, filtersToParams, hasSearchCriteria, readFilters, readView, type MailFilters, type MailView,
} from './mailQuery';
import { MailDocumentsView } from './MailDocumentsView';
import { MailFolderTree } from './MailFolderTree';
import { MailImportPanel } from './MailImportPanel';
import { MailMessageListView } from './MailMessageListView';
import { MailReader } from './MailReader';
import { MailSearchForm } from './MailSearchForm';
import { useArchiveFolders } from './useArchiveFolders';
import { useMailMessages } from './useMailMessages';
import type { MailArchive, MailMessageDetail } from './types';
import './mail.css';

type ArchivesLoad = { kind: 'loading' } | { kind: 'ready'; value: MailArchive[] } | { kind: 'error' };

const TABS: ReadonlyArray<{ view: MailView; labelKey: MessageKey }> = [
  { view: 'mail', labelKey: 'mail.tabs.mail' },
  { view: 'documents', labelKey: 'mail.tabs.documents' },
  { view: 'imports', labelKey: 'mail.tabs.imports' },
];

export function listHref(view: MailView, filters: MailFilters): string {
  const qs = filtersToParams(view, filters).toString();
  return qs ? `/mail?${qs}` : '/mail';
}

export function messageHref(id: string, view: MailView, filters: MailFilters): string {
  const qs = filtersToParams(view, filters).toString();
  return `/mail/messages/${encodeURIComponent(id)}${qs ? `?${qs}` : ''}`;
}

// MailArchive: the owner's imported Outlook archives, read-only.
//
// ONE section, three tabs: the mail view (archive, folder tree, list, reader),
// the documentary view (the same messages as documents) and import management.
// Everything that defines what is on screen — tab, archive, folder, search,
// open message — is in the URL, so every state is linkable and Back/Forward
// walks through them. `/mail/messages/:messageId` opens a message directly
// (it is where a file's "Origine email" points).
export function MailPage() {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const { messageId } = useParams();
  const [searchParams] = useSearchParams();
  const paramsKey = searchParams.toString();
  // Stable identities per URL, so effects keyed on them run once per change.
  const view = useMemo(() => readView(new URLSearchParams(paramsKey)), [paramsKey]);
  const filters = useMemo(() => readFilters(new URLSearchParams(paramsKey)), [paramsKey]);

  const [archives, setArchives] = useState<ArchivesLoad>({ kind: 'loading' });
  const [notice, setNotice] = useState<string | null>(null);

  const loadArchives = useCallback(async (signal?: AbortSignal) => {
    try {
      const value = await listMailArchives(signal);
      setArchives({ kind: 'ready', value });
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setArchives((prev) => (prev.kind === 'ready' ? prev : { kind: 'error' }));
    }
  }, [invalidateAuth]);

  useEffect(() => {
    const controller = new AbortController();
    void loadArchives(controller.signal);
    return () => controller.abort();
  }, [loadArchives]);

  const archiveList = archives.kind === 'ready' ? archives.value : [];

  // The mail view always shows one archive: the one in the URL when it is one of
  // the owner's, else the first.
  const urlArchiveKnown = filters.archiveId !== null && archiveList.some((a) => a.id === filters.archiveId);
  const mailArchiveId = view === 'mail'
    ? (urlArchiveKnown ? filters.archiveId : archiveList[0]?.id ?? null)
    : null;
  const effectiveFilters = useMemo<MailFilters | null>(() => {
    if (view === 'imports' || archives.kind !== 'ready' || archiveList.length === 0) return null;
    if (view === 'mail') return mailArchiveId ? { ...filters, archiveId: mailArchiveId } : null;
    return filters;
  }, [view, archives.kind, archiveList.length, mailArchiveId, filters]);
  const list = useMailMessages(effectiveFilters);

  const openHref = useCallback((id: string) => messageHref(id, view, filters), [view, filters]);
  const search = useCallback((next: MailFilters) => {
    setNotice(null);
    void navigate(listHref(view, next));
  }, [navigate, view]);

  // Closing the reader returns focus to the row that opened it.
  const lastOpened = useRef<string | null>(null);
  useEffect(() => {
    if (messageId) { lastOpened.current = messageId; return; }
    const id = lastOpened.current;
    if (!id) return;
    lastOpened.current = null;
    const row = Array.from(document.querySelectorAll<HTMLElement>('[data-message-id]'))
      .find((el) => el.dataset.messageId === id);
    row?.focus();
  }, [messageId]);

  const closeReader = useCallback(() => {
    void navigate(listHref(view === 'imports' ? 'mail' : view, filters));
  }, [navigate, view, filters]);

  const onDeleted = useCallback((id: string) => {
    list.remove(id);
    lastOpened.current = null;
    setNotice(t('mail.delete.done'));
    void navigate(listHref(view === 'imports' ? 'mail' : view, filters));
  }, [list, navigate, view, filters, t]);

  // A message opened with no archive in the URL (the file manager's link): put
  // its archive and folder in the URL so the list beside it is its own.
  const onLoaded = useCallback((m: MailMessageDetail) => {
    if (view !== 'mail' || filters.archiveId) return;
    void navigate(
      messageHref(m.id, 'mail', { ...EMPTY_FILTERS, archiveId: m.archiveId, folderId: m.folderId }),
      { replace: true },
    );
  }, [navigate, view, filters.archiveId]);

  const reader = messageId ? (
    <MailReader messageId={messageId} onClose={closeReader} onDeleted={onDeleted} onLoaded={onLoaded} />
  ) : null;

  return (
    <section className="ws-page-outer mail-page" data-testid="mail-page">
      <header className="ws-page-header">
        <h1>{t('mail.title')}</h1>
        <p className="muted">{t('mail.intro')}</p>
      </header>

      <nav className="lab-tabs" aria-label={t('mail.tabs.label')}>
        {TABS.map((tab) => {
          const active = tab.view === view;
          return (
            <Link
              key={tab.view}
              to={tab.view === 'imports' ? listHref('imports', EMPTY_FILTERS) : listHref(tab.view, filters)}
              className={active ? 'lab-tab is-active' : 'lab-tab'}
              aria-current={active ? 'page' : undefined}
            >
              {t(tab.labelKey)}
            </Link>
          );
        })}
      </nav>

      {notice && <p className="mail-notice" role="status">{notice}</p>}

      {view === 'imports' && (
        <MailImportPanel archives={archives} reloadArchives={() => void loadArchives()} />
      )}

      {view !== 'imports' && archives.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
      {view !== 'imports' && archives.kind === 'error' && (
        <p className="inline-error" role="alert">{t('mail.archives.error')}</p>
      )}
      {view !== 'imports' && archives.kind === 'ready' && archiveList.length === 0 && (
        <>
          {reader}
          {!reader && (
            <div className="mail-empty" data-testid="mail-no-archives">
              <p>{t('mail.archives.none')}</p>
              <Link className="row-action-primary" to={listHref('imports', EMPTY_FILTERS)}>{t('mail.archives.goImport')}</Link>
            </div>
          )}
        </>
      )}

      {view === 'mail' && archives.kind === 'ready' && archiveList.length > 0 && mailArchiveId && (
        <MailBrowser
          archives={archiveList}
          archiveId={mailArchiveId}
          filters={filters}
          list={list}
          messageId={messageId ?? null}
          reader={reader}
          hrefFor={openHref}
          onNavigate={(next) => void navigate(listHref('mail', next))}
          onSearch={search}
        />
      )}

      {view === 'documents' && archives.kind === 'ready' && archiveList.length > 0 && (
        <>
          {reader && <div className="mail-documents-reader">{reader}</div>}
          <MailDocumentsView
            archives={archiveList}
            filters={filters}
            list={list}
            hidden={reader !== null}
            hrefFor={openHref}
            onSearch={search}
          />
        </>
      )}
    </section>
  );
}

interface MailBrowserProps {
  archives: readonly MailArchive[];
  archiveId: string;
  filters: MailFilters;
  list: ReturnType<typeof useMailMessages>;
  messageId: string | null;
  reader: ReactNode;
  hrefFor: (id: string) => string;
  onNavigate: (next: MailFilters) => void;
  onSearch: (next: MailFilters) => void;
}

// Outlook-like three panes on a wide screen; on a phone, stacked navigation:
// folders and list, then the message alone with a way back.
function MailBrowser({ archives, archiveId, filters, list, messageId, reader, hrefFor, onNavigate, onSearch }: MailBrowserProps) {
  const { t } = useI18n();
  const folderLoads = useArchiveFolders([archiveId]);
  const folderLoad = folderLoads[archiveId];
  const scoped = useMemo(() => ({ ...filters, archiveId }), [filters, archiveId]);

  return (
    <div className={messageId ? 'mail-browser has-reader' : 'mail-browser'} data-testid="mail-browser">
      <aside className="mail-sidebar" aria-label={t('mail.sidebar.label')}>
        <label className="mail-field">
          <span>{t('mail.sidebar.archive')}</span>
          <select
            value={archiveId}
            onChange={(e) => onNavigate({ ...filters, archiveId: e.target.value, folderId: null })}
            data-testid="mail-archive-select"
          >
            {archives.map((a) => <option key={a.id} value={a.id}>{a.displayName}</option>)}
          </select>
        </label>
        {(!folderLoad || folderLoad.kind === 'loading') && <p className="muted" role="status">{t('common.loading')}</p>}
        {folderLoad?.kind === 'error' && <p className="inline-error" role="alert">{t('mail.folders.error')}</p>}
        {folderLoad?.kind === 'ready' && (
          <MailFolderTree
            folders={folderLoad.folders}
            selectedId={filters.folderId}
            onSelect={(folderId) => onNavigate({ ...scoped, folderId })}
          />
        )}
      </aside>

      <section className="mail-list-pane" aria-label={t('mail.list.paneLabel')}>
        <MailSearchForm filters={scoped} onSubmit={onSearch} />
        <MailMessageListView
          list={list}
          selectedId={messageId}
          hrefFor={hrefFor}
          searching={hasSearchCriteria(filters)}
        />
      </section>

      <section className="mail-reader-pane" aria-label={t('mail.reader.paneLabel')}>
        {reader ?? <p className="muted mail-reader-placeholder">{t('mail.reader.placeholder')}</p>}
      </section>
    </div>
  );
}
