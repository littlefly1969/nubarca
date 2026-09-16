import { useI18n } from '../i18n';
import type { ShareOutcome } from './guestConsoleActions';

// WHAT THE HOST DOES WITH A LINK THEY HAVE JUST BEEN HANDED.
//
// WhatsApp usually opens by itself; when the browser refuses — Safari counts
// an opening only inside the tap, and the share had to be recorded first — a
// real link is the way through, and tapping a link is also what makes iOS hand
// the address to the WhatsApp app instead of to a web page. A clipboard that
// would not take the link leaves the link itself, ready to select.
//
// It also says plainly what a share is: NubArca handed over a link. Whether a
// message was sent, arrived or was read is not something it can know.

export function GuestSharedLink({ share, label }: { share: ShareOutcome; label: string }) {
  const { t } = useI18n();

  return (
    <div className="guest-shared" data-testid="guest-shared" data-channel={share.channel}>
      {share.channel === 'whatsapp' ? (
        <>
          <p className="guest-share-note" role="status">
            {t(share.opened ? 'party.console.share.whatsappOpened' : 'party.console.share.whatsappBlocked')}
          </p>
          {share.whatsappUrl && (
            <a
              className="row-action-primary guest-shared-open" href={share.whatsappUrl}
              target="_blank" rel="noopener noreferrer" data-testid="guest-shared-whatsapp"
            >
              {t('party.console.share.whatsappOpen')}
            </a>
          )}
        </>
      ) : (
        <>
          <p className="guest-share-note" role="status">
            {t(share.copied ? 'party.console.share.copied' : 'party.console.share.copyFallback')}
          </p>
          {!share.copied && (
            <label className="party-field">
              <span className="visually-hidden">{t('party.console.share.linkLabel', { label })}</span>
              <input
                className="guest-share-link" readOnly value={share.url} data-testid="guest-shared-link"
                onFocus={(e) => e.currentTarget.select()}
              />
            </label>
          )}
        </>
      )}
      <p className="muted guest-share-note">{t('party.console.share.notDelivered')}</p>
    </div>
  );
}
