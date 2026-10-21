import { Link } from 'react-router';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';
import { senderLabel } from './mailFormat';
import type { MailMessageList } from './useMailMessages';

interface MailMessageListViewProps {
  list: MailMessageList;
  selectedId: string | null;
  hrefFor: (messageId: string) => string;
  searching: boolean;
}

// The message list, Outlook-style: sender, subject, date and a preview line,
// newest first as the server orders them. Each row is a link, so it is a Tab
// stop, opens with Enter, and can be opened in a new tab; the open message
// carries aria-current.
export function MailMessageListView({ list, selectedId, hrefFor, searching }: MailMessageListViewProps) {
  const { t, formatDate } = useI18n();

  if (list.status === 'loading') return <p className="muted" role="status">{t('mail.list.loading')}</p>;
  if (list.status === 'error') {
    return (
      <div className="inline-error" role="alert">
        {t('mail.list.error')}{' '}
        <button type="button" className="files-action" onClick={list.reload}>{t('common.retry')}</button>
      </div>
    );
  }
  if (list.items.length === 0) {
    return <p className="muted" data-testid="mail-list-empty">{searching ? t('mail.list.noResults') : t('mail.list.empty')}</p>;
  }

  return (
    <>
      <ul className="mail-message-list" aria-label={t('mail.list.label')} data-testid="mail-message-list">
        {list.items.map((m) => {
          const selected = m.id === selectedId;
          return (
            <li key={m.id}>
              <Link
                to={hrefFor(m.id)}
                className={selected ? 'mail-message-row is-selected' : 'mail-message-row'}
                aria-current={selected ? 'true' : undefined}
                data-message-id={m.id}
                data-testid="mail-message-row"
              >
                <span className="mail-message-from">{senderLabel(m.from) || t('mail.unknownSender')}</span>
                <span className="mail-message-date">
                  {m.date ? formatDate(m.date, { dateStyle: 'short', timeStyle: 'short' }) : ''}
                </span>
                <span className="mail-message-subject">
                  {m.hasAttachments && (
                    <span className="mail-clip">
                      <Icon name="attachment" />
                      <span className="visually-hidden">{t('mail.list.hasAttachments')}</span>
                    </span>
                  )}
                  {m.subject?.trim() ? m.subject : t('mail.noSubject')}
                </span>
                {m.preview && <span className="mail-message-preview">{m.preview}</span>}
              </Link>
            </li>
          );
        })}
      </ul>
      <LoadMore list={list} />
    </>
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
