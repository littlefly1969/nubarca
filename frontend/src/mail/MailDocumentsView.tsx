import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { getMailMessage } from './mailApi';
import { folderPathOf } from './folderTree';
import { formatAddress } from './mailFormat';
import { hasSearchCriteria, type MailFilters } from './mailQuery';
import { MailSearchForm } from './MailSearchForm';
import { LoadMore } from './MailMessageListView';
import { useArchiveFolders, type FolderLoad } from './useArchiveFolders';
import type { MailMessageList } from './useMailMessages';
import type { MailArchive, MailAttachmentView, MailMessageSummary } from './types';

interface MailDocumentsViewProps {
  archives: readonly MailArchive[];
  filters: MailFilters;
  list: MailMessageList;
  hidden: boolean;
  hrefFor: (messageId: string) => string;
  onSearch: (next: MailFilters) => void;
}

// The documentary view: the same messages, the same search API, presented as
// documents — subject as the title, who and when, where it was filed, and its
// attachments as chips that open the NubArca files they became. Nothing is
// duplicated: it reads the endpoints the mail view reads.
export function MailDocumentsView({ archives, filters, list, hidden, hrefFor, onSearch }: MailDocumentsViewProps) {
  const { t } = useI18n();
  const [draftArchiveId, setDraftArchiveId] = useState<string | null>(filters.archiveId);
  const archiveNames = useMemo(() => new Map(archives.map((a) => [a.id, a.displayName])), [archives]);
  const archiveIds = useMemo(() => {
    const ids = new Set(list.items.map((m) => m.archiveId));
    if (draftArchiveId) ids.add(draftArchiveId);
    if (filters.archiveId) ids.add(filters.archiveId);
    return [...ids];
  }, [list.items, draftArchiveId, filters.archiveId]);
  const folders = useArchiveFolders(archiveIds);
  const formFolders = draftArchiveId ? readyFolders(folders[draftArchiveId]) : [];

  return (
    <div className="mail-documents" hidden={hidden} data-testid="mail-documents">
      <MailSearchForm
        filters={filters}
        archives={archives}
        folders={formFolders}
        onArchiveDraftChange={setDraftArchiveId}
        onSubmit={onSearch}
      />
      {list.status === 'loading' && <p className="muted" role="status">{t('mail.list.loading')}</p>}
      {list.status === 'error' && (
        <div className="inline-error" role="alert">
          {t('mail.list.error')}{' '}
          <button type="button" className="files-action" onClick={list.reload}>{t('common.retry')}</button>
        </div>
      )}
      {list.status === 'ready' && list.items.length === 0 && (
        <p className="muted" data-testid="mail-list-empty">
          {hasSearchCriteria(filters) ? t('mail.list.noResults') : t('mail.list.empty')}
        </p>
      )}
      {list.status === 'ready' && list.items.length > 0 && (
        <>
          <ul className="mail-doc-list" aria-label={t('mail.documents.label')}>
            {list.items.map((m) => (
              <li key={m.id}>
                <MailDocumentCard
                  message={m}
                  archiveName={archiveNames.get(m.archiveId) ?? ''}
                  folderPath={folderPathOf(readyFolders(folders[m.archiveId]), m.folderId)}
                  href={hrefFor(m.id)}
                />
              </li>
            ))}
          </ul>
          <LoadMore list={list} />
        </>
      )}
    </div>
  );
}

function readyFolders(load: FolderLoad | undefined) {
  return load?.kind === 'ready' ? load.folders : [];
}

type AttachmentLoad = { kind: 'idle' } | { kind: 'loading' } | { kind: 'ready'; items: MailAttachmentView[] } | { kind: 'error' };

function MailDocumentCard({ message: m, archiveName, folderPath, href }: {
  message: MailMessageSummary; archiveName: string; folderPath: string[]; href: string;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [attachments, setAttachments] = useState<AttachmentLoad>({ kind: 'idle' });

  // The list summary carries only a count; the chips come from the message
  // itself, fetched when asked for rather than for every card on the page.
  async function loadAttachments() {
    setAttachments({ kind: 'loading' });
    try {
      const detail = await getMailMessage(m.id);
      setAttachments({ kind: 'ready', items: detail.attachments.filter((a) => !a.inline) });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setAttachments({ kind: 'error' });
    }
  }

  return (
    <article className="mail-doc-card" data-testid="mail-doc-card">
      <h3 className="mail-doc-title">
        <Link to={href}>{m.subject?.trim() ? m.subject : t('mail.noSubject')}</Link>
      </h3>
      <p className="mail-doc-meta">
        <span>{formatAddress(m.from) || t('mail.unknownSender')}</span>
        {m.date && <span className="muted"> · {formatDate(m.date, { dateStyle: 'medium', timeStyle: 'short' })}</span>}
      </p>
      <p className="muted mail-doc-place">
        {[archiveName, folderPath.join(' / ')].filter((s) => s.length > 0).join(' · ')}
      </p>
      {m.preview && <p className="mail-doc-preview">{m.preview}</p>}
      {m.attachmentCount > 0 && (
        <div className="mail-chips">
          {attachments.kind !== 'ready' && (
            <button
              type="button"
              className="mail-chip"
              onClick={() => void loadAttachments()}
              disabled={attachments.kind === 'loading'}
              data-testid="mail-doc-attachments"
            >
              <Icon name="attachment" />
              {attachments.kind === 'loading'
                ? t('common.loading')
                : t('mail.documents.attachments', { count: m.attachmentCount })}
            </button>
          )}
          {attachments.kind === 'error' && <span className="inline-error" role="alert">{t('mail.documents.attachmentsError')}</span>}
          {attachments.kind === 'ready' && attachments.items.map((a) => (
            a.state === 'available' && a.fileItemId ? (
              <a
                key={a.id}
                className="mail-chip"
                href={`/api/files/${encodeURIComponent(a.fileItemId)}/content`}
                target="_blank"
                rel="noopener noreferrer"
                data-testid="mail-doc-chip"
              >
                <Icon name="attachment" />
                {a.currentFileName ?? a.name}
              </a>
            ) : (
              <span key={a.id} className="mail-chip is-unavailable" data-testid="mail-doc-chip"
                title={a.state === 'file_deleted' ? t('mail.attachments.fileDeleted') : t('mail.attachments.notExtractedShort')}>
                <Icon name="attachment" />
                {a.name}
              </span>
            )
          ))}
        </div>
      )}
    </article>
  );
}
