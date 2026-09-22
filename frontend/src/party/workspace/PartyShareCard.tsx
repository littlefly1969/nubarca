import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { useI18n } from '../../i18n';
import { copyWhenReady } from '../guestShare';
import { Button, ButtonLink, Notice, Panel } from './ui';

// THE party's public link — the QR on the table, the address in a chat.
//
// One component, used wherever the host needs to hand the party to somebody:
// the summary, and the screens section. It is deliberately the SAME link in
// both places, because there is one, and a product that shows it twice in two
// shapes teaches a host that there are two.
//
// What this link is NOT is a check-in: it opens the ordinary guest party, and
// whoever follows it becomes a visitor of the evening — not a guest on the
// list, and not an arrival. The copy says so, because a QR beside a door is
// read as a turnstile unless it tells you otherwise.

/** The absolute address a guest would type, from the relative one the API returns. */
export function absoluteGuestUrl(relative: string): string {
  if (typeof window === 'undefined') return relative;
  return `${window.location.origin}${relative}`;
}

/**
 * A QR for an address, generated in the browser.
 *
 * Exported because the album share panel needs exactly this and a second
 * implementation would be a second answer to "what does this QR point at".
 */
export function useQrSvg(url: string | null, size: number): string | null {
  const [svg, setSvg] = useState<string | null>(null);
  useEffect(() => {
    if (!url) { setSvg(null); return; }
    let cancelled = false;
    void QRCode.toString(url, { type: 'svg', margin: 1, width: size })
      .then((value) => { if (!cancelled) setSvg(value); })
      .catch(() => { if (!cancelled) setSvg(null); });
    return () => { cancelled = true; };
  }, [url, size]);
  return svg;
}

export function PartyShareCard({
  partyUrl, tone, showQr = true, testId = 'party-share',
}: {
  /** The relative public URL, or null while the party is not open to guests. */
  partyUrl: string | null;
  tone?: 'quiet';
  showQr?: boolean;
  testId?: string;
}) {
  const { t } = useI18n();
  const [copied, setCopied] = useState<'idle' | 'ok' | 'failed'>('idle');
  const absolute = partyUrl ? absoluteGuestUrl(partyUrl) : null;
  const qr = useQrSvg(showQr ? absolute : null, 176);

  // The confirmation is short-lived, and it is never the only thing that says
  // the link exists — the address itself is on the page either way.
  useEffect(() => {
    if (copied === 'idle') return;
    const timer = setTimeout(() => setCopied('idle'), 4000);
    return () => clearTimeout(timer);
  }, [copied]);

  if (!absolute) return null;

  return (
    <Panel
      title={t('party.share.heading')}
      note={t('party.share.note')}
      tone={tone}
      testId={testId}
    >
      <div className="pw-share">
        <code className="pw-share-url" data-testid={`${testId}-url`}>{absolute}</code>
        <div className="pw-panel-actions">
          <Button
            tone="primary"
            data-testid={`${testId}-copy`}
            onClick={() => {
              void copyWhenReady(Promise.resolve(absolute))
                .then((ok) => setCopied(ok ? 'ok' : 'failed'));
            }}
          >
            {t('party.share.copy')}
          </Button>
          <ButtonLink href={absolute} data-testid={`${testId}-open`}>
            {t('party.share.open')}
          </ButtonLink>
        </div>
        <div aria-live="polite">
          {copied === 'ok' && (
            <p className="pw-small pw-muted" role="status" data-testid={`${testId}-copied`}>
              {t('party.share.copied')}
            </p>
          )}
          {copied === 'failed' && (
            <p className="pw-small pw-muted" role="status">{t('party.share.copyFailed')}</p>
          )}
        </div>
        {showQr && qr && (
          <figure className="pw-qr" data-testid={`${testId}-qr`}>
            {/* The QR is generated in the browser from the same address above:
                there is no second source of truth for where a guest lands. */}
            <div
              className="pw-qr-code"
              role="img"
              aria-label={t('party.share.qrAlt')}
              dangerouslySetInnerHTML={{ __html: qr }}
            />
            <figcaption className="pw-small pw-muted">{t('party.share.qrCaption')}</figcaption>
          </figure>
        )}
      </div>
    </Panel>
  );
}

/** Said once, wherever the public link is offered: it is not a door register. */
export function PublicLinkMeaningNote() {
  const { t } = useI18n();
  return (
    <Notice tone="info" testId="party-share-meaning">
      <p>{t('party.share.notCheckIn')}</p>
    </Notice>
  );
}
