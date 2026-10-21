import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { Modal } from '../components/Overlay';
import { formatSize } from '../components/format';
import { MailBody } from './MailBody';
import { deleteMailMessage, getMailMessage } from './mailApi';
import { formatAddress, formatAddressList } from './mailFormat';
import type { MailAttachmentView, MailMessageDetail } from './types';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; message: MailMessageDetail }
  | { kind: 'gone' }
  | { kind: 'error' };

const SKIP_REASON_KEYS: Record<string, MessageKey> = {
  unsupported_method: 'mail.skip.unsupported_method',
  too_large: 'mail.skip.too_large',
  corrupt: 'mail.skip.corrupt',
  limit_exceeded: 'mail.skip.limit_exceeded',
  depth_exceeded: 'mail.skip.depth_exceeded',
};

export function skipReasonKey(reason: string | null): MessageKey {
  return (reason && SKIP_REASON_KEYS[reason]) || 'mail.skip.unknown';
}

interface MailReaderProps {
  messageId: string;
  // Back to the list ("Chiudi" on desktop, "Indietro" on a phone).
  onClose: () => void;
  onDeleted: (id: string) => void;
  onLoaded?: (message: MailMessageDetail) => void;
}

// The reader pane: one archived message, read-only. Headers, body, attachments
// (each in its real state: a live NubArca file, a file the owner since deleted,
// or one the import could not extract) and embedded messages. The only action
// is deleting the message, which never touches the attachment files.
export function MailReader({ messageId, onClose, onDeleted, onLoaded }: MailReaderProps) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState(false);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const onLoadedRef = useRef(onLoaded);
  onLoadedRef.current = onLoaded;

  useEffect(() => {
    const controller = new AbortController();
    setState({ kind: 'loading' });
    setConfirming(false);
    setDeleteError(false);
    getMailMessage(messageId, controller.signal)
      .then((message) => {
        if (controller.signal.aborted) return;
        setState({ kind: 'ready', message });
        onLoadedRef.current?.(message);
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setState(err instanceof ApiError && err.status === 404 ? { kind: 'gone' } : { kind: 'error' });
      });
    return () => controller.abort();
  }, [messageId, invalidateAuth]);

  // Focus moves to the message as it opens, so a keyboard or screen-reader user
  // lands on what they asked for instead of staying in the list.
  useEffect(() => {
    if (state.kind === 'ready') headingRef.current?.focus();
  }, [state.kind, messageId]);

  async function confirmDelete() {
    setDeleting(true);
    setDeleteError(false);
    try {
      await deleteMailMessage(messageId);
      setConfirming(false);
      onDeleted(messageId);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 404) {
        // Already gone: the outcome the owner asked for.
        setConfirming(false);
        onDeleted(messageId);
        return;
      }
      setDeleteError(true);
    } finally {
      setDeleting(false);
    }
  }

  const closeButton = (
    <button type="button" className="files-action mail-reader-close" onClick={onClose}>
      {t('mail.reader.back')}
    </button>
  );

  if (state.kind === 'loading') {
    return (
      <article className="mail-reader" aria-busy="true">
        <div className="mail-reader-bar">{closeButton}</div>
        <p className="muted" role="status">{t('mail.reader.loading')}</p>
      </article>
    );
  }
  if (state.kind === 'gone' || state.kind === 'error') {
    return (
      <article className="mail-reader">
        <div className="mail-reader-bar">{closeButton}</div>
        <p className={state.kind === 'gone' ? 'muted' : 'inline-error'} role="alert" data-testid="mail-reader-unavailable">
          {state.kind === 'gone' ? t('mail.reader.gone') : t('mail.reader.error')}
        </p>
      </article>
    );
  }

  const m = state.message;
  const subject = m.subject?.trim() ? m.subject : t('mail.noSubject');
  const when = m.sentAt ?? m.date ?? m.receivedAt;
  const fmt = (iso: string) => formatDate(iso, { dateStyle: 'full', timeStyle: 'short' });
  const visibleAttachments = m.attachments;

  return (
    <article className="mail-reader" aria-labelledby="mail-reader-subject" data-testid="mail-reader">
      <div className="mail-reader-bar">
        {closeButton}
        <button
          type="button"
          className="files-action files-action-destructive"
          onClick={() => { setDeleteError(false); setConfirming(true); }}
        >
          {t('mail.delete.action')}
        </button>
      </div>

      <header className="mail-reader-head">
        <h2 id="mail-reader-subject" ref={headingRef} tabIndex={-1} className="mail-reader-subject">{subject}</h2>
        <dl className="mail-headers">
          <dt>{t('mail.field.from')}</dt>
          <dd>{formatAddress(m.from) || t('mail.unknownSender')}</dd>
          {m.to.length > 0 && (<><dt>{t('mail.field.to')}</dt><dd>{formatAddressList(m.to)}</dd></>)}
          {m.cc.length > 0 && (<><dt>{t('mail.field.cc')}</dt><dd>{formatAddressList(m.cc)}</dd></>)}
          {m.bcc.length > 0 && (<><dt>{t('mail.field.bcc')}</dt><dd>{formatAddressList(m.bcc)}</dd></>)}
          {when && (<><dt>{t('mail.field.date')}</dt><dd>{fmt(when)}</dd></>)}
          {m.receivedAt && m.receivedAt !== when && (
            <><dt>{t('mail.field.received')}</dt><dd>{fmt(m.receivedAt)}</dd></>
          )}
          <dt>{t('mail.field.archive')}</dt>
          <dd>
            {m.archiveName}
            {m.folderPath.length > 0 && <span className="muted"> · {m.folderPath.join(' / ')}</span>}
          </dd>
        </dl>
      </header>

      {visibleAttachments.length > 0 && (
        <section className="mail-attachments" aria-labelledby="mail-reader-attachments">
          <h3 id="mail-reader-attachments" className="mail-section-title">
            {t('mail.attachments.title', { count: visibleAttachments.length })}
          </h3>
          <ul className="mail-attachment-list">
            {visibleAttachments.map((a) => <AttachmentRow key={a.id} attachment={a} />)}
          </ul>
        </section>
      )}

      <MailBody body={m.body} title={t('mail.body.frameTitle', { subject })} />

      {m.embeddedMessages.length > 0 && (
        <section className="mail-embedded" aria-labelledby="mail-reader-embedded">
          <h3 id="mail-reader-embedded" className="mail-section-title">{t('mail.embedded.title')}</h3>
          {m.embeddedMessages.map((e) => (
            <details key={e.path} className="mail-embedded-item" data-testid="mail-embedded">
              <summary>{e.subject?.trim() ? e.subject : t('mail.noSubject')}</summary>
              <dl className="mail-headers">
                {e.from && (<><dt>{t('mail.field.from')}</dt><dd>{formatAddress(e.from)}</dd></>)}
                {e.date && (<><dt>{t('mail.field.date')}</dt><dd>{fmt(e.date)}</dd></>)}
              </dl>
              {e.bodyText
                ? <pre className="mail-body-text">{e.bodyText}</pre>
                : <p className="muted">{t('mail.body.none')}</p>}
            </details>
          ))}
        </section>
      )}

      {confirming && (
        <Modal
          title={t('mail.delete.title')}
          onClose={() => { if (!deleting) setConfirming(false); }}
          dismissable={!deleting}
          testId="mail-delete-dialog"
          footer={(
            <>
              <button type="button" className="files-action" onClick={() => setConfirming(false)} disabled={deleting}>
                {t('common.cancel')}
              </button>
              <button
                type="button"
                className="files-action files-action-destructive"
                onClick={() => void confirmDelete()}
                disabled={deleting}
                data-testid="mail-delete-confirm"
              >
                {deleting ? t('mail.delete.busy') : t('mail.delete.confirm')}
              </button>
            </>
          )}
        >
          <p>{t('mail.delete.body', { subject })}</p>
          <p className="muted">{t('mail.delete.attachmentsStay')}</p>
          {deleteError && <p className="inline-error" role="alert">{t('mail.delete.error')}</p>}
        </Modal>
      )}
    </article>
  );
}

function AttachmentRow({ attachment: a }: { attachment: MailAttachmentView }) {
  const { t } = useI18n();
  const renamed = a.currentFileName && a.currentFileName !== a.name ? a.currentFileName : null;
  return (
    <li className={`mail-attachment is-${a.state}`} data-testid="mail-attachment" data-state={a.state}>
      <div className="mail-attachment-main">
        <span className="mail-attachment-name">{a.name}</span>
        <span className="muted mail-attachment-meta">
          {[
            a.sizeBytes != null ? formatSize(a.sizeBytes) : null,
            a.mimeType,
            a.inline ? t('mail.attachments.inline') : null,
            a.embeddedPath ? t('mail.attachments.fromEmbedded') : null,
          ].filter(Boolean).join(' · ')}
        </span>
        {renamed && <span className="muted mail-attachment-meta">{t('mail.attachments.renamed', { name: renamed })}</span>}
      </div>
      {a.state === 'available' && a.fileItemId && (
        <div className="mail-attachment-actions">
          <a
            className="files-action"
            href={`/api/files/${encodeURIComponent(a.fileItemId)}/content`}
            target="_blank"
            rel="noopener noreferrer"
          >
            {t('mail.attachments.open')}
          </a>
          {/* The file manager has no per-file deep link: this opens Files,
              where the attachment sits under Posta/<archive>/<folder>. */}
          <Link className="files-action" to="/" title={t('mail.attachments.showInFilesHint')}>
            {t('mail.attachments.showInFiles')}
          </Link>
        </div>
      )}
      {a.state === 'file_deleted' && <p className="mail-attachment-note muted">{t('mail.attachments.fileDeleted')}</p>}
      {a.state === 'not_extracted' && (
        <p className="mail-attachment-note muted">
          {t('mail.attachments.notExtracted', { reason: t(skipReasonKey(a.skipReason)) })}
        </p>
      )}
    </li>
  );
}
