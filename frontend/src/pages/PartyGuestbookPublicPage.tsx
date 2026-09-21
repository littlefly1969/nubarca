import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { getPartyGuestContext } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PRODUCT_NAME } from '../brand/brand';
import { LanguageSwitcher } from '../components/LanguageSwitcher';
import { PartyGuestbookPanel } from '../components/PartyGuestbookPanel';
import './PartyContribution.css';
import './PartyGuestbook.css';

// PUBLIC, unauthenticated. THE PARTY'S GUEST BOOK on the party's own view
// token — the one on the QR, and the address a keepsake keeps after the
// evening.
//
// The book itself lives in PartyGuestbookPanel, which the contribution page
// shows too: one implementation behind two doors, so what a guest writes from
// the contribution page and what anybody reads here are the same book rather
// than two surfaces that agree by coincidence. This file is the page around
// it — the brand, the language switcher, the way back to the party.

const WORDMARK = {
  src: '/brand/nubarca-wordmark-on-dark-480w.png',
  width: 480,
  height: 135,
} as const;

export function PartyGuestbookPublicPage() {
  const { token } = useParams<{ token: string }>();
  const { t } = useI18n();
  const [partyTitle, setPartyTitle] = useState<string | null>(null);

  // The party's NAME, so the page can say who the dedication is for. Its own
  // request because the book's route answers about the book: a failure here
  // costs the heading a name and nothing else.
  useEffect(() => {
    if (!token) return undefined;
    const ctrl = new AbortController();
    getPartyGuestContext(token, ctrl.signal)
      .then((context) => { if (!ctrl.signal.aborted) setPartyTitle(context.title); })
      .catch(() => { /* the heading falls back to a generic one */ });
    return () => ctrl.abort();
  }, [token]);

  return (
    <main className="party-contribution party-guestbook">
      <div className="party-contribution-shell">
        <div className="party-contribution-topbar">
          <img
            className="party-contribution-logo"
            src={WORDMARK.src}
            alt={PRODUCT_NAME}
            width={WORDMARK.width}
            height={WORDMARK.height}
          />
          <LanguageSwitcher className="language-switcher language-switcher-public" compact />
        </div>

        <header className="party-contribution-head">
          <h1 className="party-contribution-title">{t('partyGuestbookPublic.title')}</h1>
          <p className="party-contribution-help">
            {partyTitle
              ? t('partyGuestbookPublic.forParty', { party: partyTitle })
              : t('partyGuestbookPublic.subtitle')}
          </p>
        </header>

        {token && (
          <p className="party-guestbook-back">
            <Link to={`/party/${encodeURIComponent(token)}`}>
              ← {t('partyGuestbookPublic.backToParty')}
            </Link>
          </p>
        )}

        {token && <PartyGuestbookPanel token={token} partyTitle={partyTitle} />}

      </div>
    </main>
  );
}


/**
 * The composer.
 *
 * Counts with the SAME rules the server validates by, so a guest never watches
 * the counter say "40 left" and the submit fail. Whitespace and zero-width
 * padding collapse before counting, which is why the number stops moving while
 * somebody keeps typing spaces.
 */
