import { useParams } from 'react-router';
import { useI18n } from '../i18n';
import { PartyApiProvider, crewPartyApi } from '../party/workspace/partyApi';
import { PartyControlRoomPage } from './PartyControlRoomPage';
import { PartyMessagesPage } from './PartyMessagesPage';
import { PartyUploadsPage } from './PartyUploadsPage';
import './PartyCrew.css';

// THE THREE PAGES A SECTION OPENS, for a collaborator.
//
// The moderation queue, the greetings queue and the control room are the host's
// own pages, rendered through the Party Crew family of routes. Nothing is
// duplicated: the same components, the same words, the same keyboard handling —
// they simply ask a different server route, because `usePartyApi()` returns a
// different object inside this provider.
//
// THE ALBUM ID IS A PLACEHOLDER. Those pages take one from the URL and pass it
// to the api; every crew route ignores it and resolves the party's own album on
// the server. Rather than leave the parameter empty — which the pages read as
// "not ready yet" — the party's id stands in for it. Nothing is ever fetched
// with it.

type Kind = 'photos' | 'messages' | 'game';

export function PartyCrewDeepPage({ kind }: { kind: Kind }) {
  const { partyId } = useParams<{ partyId: string }>();
  const { t } = useI18n();
  const back = { to: `/party/crew/${partyId ?? ''}`, label: t('crew.deep.back') };

  return (
    <PartyApiProvider api={crewPartyApi}>
      <div className="crew-shell" data-testid={`party-crew-${kind}`}>
        {kind === 'photos' && <PartyUploadsPage albumId={partyId} back={back} />}
        {kind === 'messages' && <PartyMessagesPage albumId={partyId} back={back} />}
        {kind === 'game' && <PartyControlRoomPage albumId={partyId} back={back} />}
      </div>
    </PartyApiProvider>
  );
}
