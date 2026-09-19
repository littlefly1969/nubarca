import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { getPartyCrewSession, type PartyCrewSession } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartyApiProvider, crewPartyApi } from '../party/workspace/partyApi';
import { PartyControlRoomPage } from './PartyControlRoomPage';
import { PartyGuestbookPage } from './PartyGuestbookPage';
import { PartyMessagesPage } from './PartyMessagesPage';
import { PartyUploadsPage } from './PartyUploadsPage';
import './PartyCrew.css';

// THE FOUR PAGES A SECTION OPENS, for a collaborator.
//
// The moderation queue, the greetings queue, the guest book and the control
// room are the host's own pages, rendered through the Party Crew family of
// routes. Nothing is duplicated: the same components, the same words, the same
// keyboard handling — they simply ask a different server route, because
// `usePartyApi()` returns a different object inside this provider.
//
// IT RESOLVES THE SESSION FIRST, for the capabilities. The adapter needs them
// to answer `can(...)`, and a page that rendered before they arrived would show
// a control this role does not hold for as long as the read took.
//
// The album id those pages take is a placeholder: every crew route names the
// party and resolves its album server-side, so the party id stands in and
// nothing is ever fetched with it.

type Kind = 'photos' | 'messages' | 'guestbook' | 'game';

export function PartyCrewDeepPage({ kind }: { kind: Kind }) {
  const { partyId } = useParams<{ partyId: string }>();
  const { t } = useI18n();
  const [state, setState] =
    useState<{ kind: 'loading' } | { kind: 'ready'; session: PartyCrewSession } | { kind: 'gone' }>(
      { kind: 'loading' });

  useEffect(() => {
    if (!partyId) { setState({ kind: 'gone' }); return; }
    let live = true;
    void (async () => {
      try {
        const session = await getPartyCrewSession(partyId);
        if (live) setState({ kind: 'ready', session });
      } catch {
        if (live) setState({ kind: 'gone' });
      }
    })();
    return () => { live = false; };
  }, [partyId]);

  if (state.kind === 'loading') {
    return (
      <main className="crew-shell" data-testid={`party-crew-${kind}`}>
        <p className="crew-pair-lede" role="status">{t('crew.shell.loading')}</p>
      </main>
    );
  }

  if (state.kind === 'gone') {
    return (
      <main className="crew-shell" data-testid={`party-crew-${kind}`}>
        <div className="crew-pair-card">
          <h1 className="crew-pair-title">{t('crew.shell.gone.title')}</h1>
          <p className="crew-pair-lede">{t('crew.shell.gone.body')}</p>
        </div>
      </main>
    );
  }

  const { session } = state;
  const back = { to: `/party/crew/${session.partyId}`, label: t('crew.deep.back') };

  return (
    <PartyApiProvider api={crewPartyApi(session.partyId, session.capabilities)}>
      <div className="crew-shell" data-testid={`party-crew-${kind}`}>
        {kind === 'photos' && <PartyUploadsPage albumId={session.partyId} back={back} />}
        {kind === 'messages' && <PartyMessagesPage albumId={session.partyId} back={back} />}
        {/* The BOOK is party-scoped on both surfaces, so it takes the real
            party id rather than the placeholder the album-scoped pages use. */}
        {kind === 'guestbook' && <PartyGuestbookPage partyId={session.partyId} back={back} />}
        {kind === 'game' && <PartyControlRoomPage albumId={session.partyId} back={back} />}
      </div>
    </PartyApiProvider>
  );
}
