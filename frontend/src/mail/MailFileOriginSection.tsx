import { useEffect, useState, type ReactNode } from 'react';
import { Link, useInRouterContext } from 'react-router';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { listMailFileOrigins } from './mailApi';
import { formatAddress } from './mailFormat';
import type { MailFileOrigin } from './types';
import './mail.css';

// "Origine email" in the file manager's details panel: which archived e-mail(s)
// this file arrived as an attachment of, with a link back to the message.
//
// Purely additive. It renders NOTHING while loading, when the file has no live
// origin, or when the request fails (MailArchive disabled, older server, a
// network blip): the details panel must look exactly as it always has for
// every file that did not come from a mailbox.
export function MailFileOriginSection({ fileItemId }: { fileItemId: string }) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [origins, setOrigins] = useState<MailFileOrigin[]>([]);

  useEffect(() => {
    const controller = new AbortController();
    setOrigins([]);
    listMailFileOrigins(fileItemId, controller.signal)
      .then((items) => { if (!controller.signal.aborted) setOrigins(items); })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) invalidateAuth();
      });
    return () => controller.abort();
  }, [fileItemId, invalidateAuth]);

  if (origins.length === 0) return null;

  return (
    <section className="mail-origin" aria-labelledby={`mail-origin-${fileItemId}`} data-testid="mail-file-origin">
      <h3 id={`mail-origin-${fileItemId}`} className="mail-origin-title">{t('mail.origin.title')}</h3>
      <ul className="mail-origin-list">
        {origins.map((o) => (
          <li key={`${o.messageId}:${o.attachmentName}`} className="mail-origin-item">
            <OriginLink messageId={o.messageId}>
              {o.subject?.trim() ? o.subject : t('mail.noSubject')}
            </OriginLink>
            <dl className="details-props mail-origin-props">
              <dt>{t('mail.origin.archive')}</dt>
              <dd>{o.archiveName}</dd>
              {o.from && (
                <>
                  <dt>{t('mail.field.from')}</dt>
                  <dd>{formatAddress(o.from)}</dd>
                </>
              )}
              {o.date && (
                <>
                  <dt>{t('mail.field.date')}</dt>
                  <dd>{formatDate(o.date, { dateStyle: 'medium', timeStyle: 'short' })}</dd>
                </>
              )}
              <dt>{t('mail.origin.attachment')}</dt>
              <dd>{o.attachmentName}</dd>
            </dl>
          </li>
        ))}
      </ul>
    </section>
  );
}

// The file browser is rendered without a router in some harnesses; a plain
// anchor is the same destination there.
function OriginLink({ messageId, children }: { messageId: string; children: ReactNode }) {
  const inRouter = useInRouterContext();
  const to = `/mail/messages/${encodeURIComponent(messageId)}`;
  return inRouter
    ? <Link to={to} className="mail-origin-link">{children}</Link>
    : <a href={to} className="mail-origin-link">{children}</a>;
}
