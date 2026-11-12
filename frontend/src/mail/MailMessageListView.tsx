import type { KeyboardEvent } from 'react';
import { Link } from 'react-router';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { listDateStyle, senderLabel } from './mailFormat';
import { focusInPane, type MailListPresentation } from './MailWorkspace';
import type { MailMessageList } from './useMailMessages';

interface MailMessageListViewProps {
  list: MailMessageList;
  selectedId: string | null;
  hrefFor: (messageId: string) => string;
  searching: boolean;
  // "compact": stacked lines for a narrow column. "table": one row per message
  // with columns, for a list as wide as the workspace.
  presentation?: MailListPresentation;
}

// The message list: sender, subject, preview, attachment marker and date,
// newest first as the server orders them. Each row is a link, so it is a Tab
// stop, opens with Enter, and can be opened in a new tab; the open message
// carries aria-current; the spaces between its parts (invisible in the grid)
// keep its accessible name in words. Up/Down, Home and End move between rows without
// leaving the list's own scroll box.
export function MailMessageListView({ list, selectedId, hrefFor, searching, presentation = 'compact' }: MailMessageListViewProps) {
  const { t, tn, formatDate } = useI18n();

  if (list.status === 'loading') {
    return (
      <div className="mail-list-state" role="status" aria-busy="true">
        <span className="mail-spinner" aria-hidden="true" />
        <p className="muted">{t('mail.list.loading')}</p>
      </div>
    );
  }
  if (list.status === 'error') {
    return (
      <div className="mail-list-state">
        <p className="inline-error" role="alert">{t('mail.list.error')}</p>
        <button type="button" className="files-action" onClick={list.reload}>{t('common.retry')}</button>
      </div>
    );
  }
  const incomplete = searching && list.bodySearchIncomplete && (
    <p className="mail-list-note" role="note" data-testid="mail-body-search-incomplete">{t('mail.list.bodySearchIncomplete')}</p>
  );
  if (list.items.length === 0) {
    return (
      <>
        {incomplete}
        <div className="mail-list-state">
          <Icon name={searching ? 'search' : 'mail'} size={32} />
          <p className="muted" data-testid="mail-list-empty">{searching ? t('mail.list.noResults') : t('mail.list.empty')}</p>
        </div>
      </>
    );
  }

  const table = presentation === 'table';
  const now = new Date();

  function onKeyDown(e: KeyboardEvent<HTMLUListElement>) {
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    const rows = Array.from(e.currentTarget.querySelectorAll<HTMLElement>('[data-message-id]'));
    const at = rows.indexOf(e.target as HTMLElement);
    if (at < 0) return;
    let next = -1;
    if (e.key === 'ArrowDown') next = Math.min(at + 1, rows.length - 1);
    else if (e.key === 'ArrowUp') next = Math.max(at - 1, 0);
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = rows.length - 1;
    if (next < 0) return;
    e.preventDefault();
    focusInPane(rows[next]);
  }

  return (
    <div className={table ? 'mail-list is-table' : 'mail-list is-compact'} data-presentation={presentation}>
      {incomplete}
      {table && (
        <div className="mail-list-head" aria-hidden="true">
          <span className="mail-col-from">{t('mail.list.columnFrom')}</span>
          <span className="mail-col-subject">{t('mail.list.columnSubject')}</span>
          <span className="mail-col-clip"><Icon name="attachment" /></span>
          <span className="mail-col-date">{t('mail.list.columnDate')}</span>
        </div>
      )}
      <ul className="mail-message-list" aria-label={t('mail.list.label')} data-testid="mail-message-list" onKeyDown={onKeyDown}>
        {list.items.map((m) => {
          const selected = m.id === selectedId;
          const subject = m.subject?.trim();
          const sender = senderLabel(m.from);
          return (
            <li key={m.id}>
              <Link
                to={hrefFor(m.id)}
                className={selected ? 'mail-message-row is-selected' : 'mail-message-row'}
                aria-current={selected ? 'true' : undefined}
                data-message-id={m.id}
                data-testid="mail-message-row"
              >
                <span className={sender ? 'mail-message-from' : 'mail-message-from is-missing'}>
                  {sender || t('mail.unknownSender')}
                </span>{' '}
                <span className="mail-message-text">
                  <span className={subject ? 'mail-message-subject' : 'mail-message-subject is-missing'}>
                    {subject || t('mail.noSubject')}
                  </span>
                  {m.preview && <>{' '}<span className="mail-message-preview">{m.preview}</span></>}
                </span>{' '}
                <span className="mail-message-clip">
                  {m.hasAttachments && (
                    <>
                      <Icon name="attachment" />
                      <span className="visually-hidden">{t('mail.list.hasAttachments')}</span>
                    </>
                  )}
                </span>{' '}
                {m.date ? (
                  <time
                    className="mail-message-date"
                    dateTime={m.date}
                    title={formatDate(m.date, { dateStyle: 'full', timeStyle: 'short' })}
                  >
                    {formatDate(m.date, listDateStyle(m.date, now))}
                  </time>
                ) : (
                  <span className="mail-message-date is-missing">{t('mail.list.noDate')}</span>
                )}
              </Link>
            </li>
          );
        })}
      </ul>
      <div className="mail-list-foot">
        <p className="muted mail-list-count" data-testid="mail-list-count">
          {tn(list.items.length, 'mail.list.loaded')}
        </p>
        <LoadMore list={list} />
      </div>
    </div>
  );
}

export function LoadMore({ list }: { list: MailMessageList }) {
  const { t } = useI18n();
  if (!list.nextCursor) return null;
  return (
    <div className="mail-load-more">
      <button
        type="button"
        className="files-action"
        onClick={list.loadMore}
        disabled={list.loadingMore}
        data-testid="mail-load-more"
      >
        {list.loadingMore ? t('common.loading') : t('common.loadMore')}
      </button>
      {list.loadMoreError && <p className="inline-error" role="alert">{t('mail.list.moreError')}</p>}
    </div>
  );
}
