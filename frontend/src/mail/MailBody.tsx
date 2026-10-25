import { useCallback, useEffect, useRef, useState } from 'react';
import { useI18n } from '../i18n';
import type { MailMessageDetail } from './types';

// What the message's HTML may do inside its frame: nothing remote, ever.
// Images only from NubArca itself (the server rewrites `cid:` to the owner's
// authorized attachment URL) or inline data; styles only inline; no scripts,
// no frames, no connections. The server has already sanitized the markup
// (contract §5) — this is the second wall, not the first.
export const MAIL_BODY_CSP =
  "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; font-src 'self' data:";

const BASE_STYLE =
  'html,body{margin:0;padding:0;background:#fff;color:#111;}' +
  'body{padding:12px 14px;font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif;' +
  'overflow-wrap:anywhere;word-break:break-word;}' +
  'img{max-width:100%;height:auto;}table{max-width:100%;}pre{white-space:pre-wrap;}' +
  'a{color:#0b4fd6;}';

// The frame's whole document. The CSP <meta> comes FIRST, before any of the
// message's own markup, so it governs everything the parser meets after it.
export function buildMailSrcDoc(html: string): string {
  return (
    '<!doctype html><html><head><meta charset="utf-8">' +
    `<meta http-equiv="Content-Security-Policy" content="${MAIL_BODY_CSP}">` +
    '<meta name="referrer" content="no-referrer">' +
    `<style>${BASE_STYLE}</style></head><body>${html}</body></html>`
  );
}

const MIN_FRAME_PX = 120;
const MAX_FRAME_PX = 20000;

export function MailBody({ body, title }: { body: MailMessageDetail['body']; title: string }) {
  const { t } = useI18n();

  if (body.kind === 'html' && body.html) {
    return (
      <div className="mail-body">
        <MailHtmlFrame html={body.html} title={title} />
        {body.truncated && <p className="mail-notice" role="note">{t('mail.body.truncated')}</p>}
      </div>
    );
  }

  // `rtf` arrives as text the server already derived; it is shown as given.
  const text = body.text;
  if (body.kind === 'none' || !text) {
    return <p className="muted mail-body-empty">{t('mail.body.none')}</p>;
  }
  return (
    <div className="mail-body">
      <pre className="mail-body-text" data-kind={body.kind}>{text}</pre>
      {body.truncated && <p className="mail-notice" role="note">{t('mail.body.truncated')}</p>}
    </div>
  );
}

// The sandbox grants `allow-same-origin` and NOTHING else: no scripts, forms,
// popups, top navigation or plugins. Same-origin is what lets the parent read
// the content height to size the frame (and lets `img-src 'self'` match
// NubArca); without `allow-scripts` nothing inside the frame can use it.
function MailHtmlFrame({ html, title }: { html: string; title: string }) {
  const frameRef = useRef<HTMLIFrameElement>(null);
  const [height, setHeight] = useState(320);

  const measure = useCallback(() => {
    const doc = frameRef.current?.contentDocument;
    // The body's own height, not the document's: a document is never shorter
    // than the frame showing it, so measuring it could grow the frame but
    // never let it shrink again after a wider pane reflowed the text.
    const h = doc?.body?.scrollHeight ?? doc?.documentElement?.scrollHeight ?? 0;
    if (h > 0) setHeight(Math.min(Math.max(h + 4, MIN_FRAME_PX), MAX_FRAME_PX));
  }, []);

  const onLoad = useCallback(() => {
    measure();
    // Images arrive after `load` of the srcdoc in some engines; re-measure as
    // each one settles so the frame neither clips nor leaves a gap.
    const doc = frameRef.current?.contentDocument;
    doc?.querySelectorAll('img').forEach((img) => {
      if (!img.complete) {
        img.addEventListener('load', measure, { once: true });
        img.addEventListener('error', measure, { once: true });
      }
    });
  }, [measure]);

  // The message reflows when its pane is resized (a divider dragged, the
  // layout switched, the window narrowed): its height follows.
  useEffect(() => {
    const frame = frameRef.current;
    if (!frame || typeof ResizeObserver === 'undefined') return;
    let width = frame.clientWidth;
    const ro = new ResizeObserver(() => {
      if (frame.clientWidth === width) return;
      width = frame.clientWidth;
      measure();
    });
    ro.observe(frame);
    return () => ro.disconnect();
  }, [measure]);

  return (
    <iframe
      ref={frameRef}
      className="mail-body-frame"
      title={title}
      sandbox="allow-same-origin"
      referrerPolicy="no-referrer"
      srcDoc={buildMailSrcDoc(html)}
      style={{ height }}
      onLoad={onLoad}
      data-testid="mail-body-frame"
    />
  );
}
