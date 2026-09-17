import { useCallback, useEffect, useState } from 'react';
import { useParams } from 'react-router';
import {
  ApiError,
  disconnectPartyCrewDevice,
  getPartyCrewSession,
  leavePartyCrewParty,
  listMyPartyCrewDevices,
  revokeMyPartyCrewDevice,
  type PartyCrewDevice,
  type PartyCrewSession,
  type PartyStatus,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { crewLandingSection, crewRoleLabelKey, crewSections } from '../party/crew/crewModel';
import { PartyApiProvider, crewPartyApi } from '../party/workspace/partyApi';
import {
  WORKSPACE_SECTIONS, type WorkspaceSection,
} from '../party/workspace/partyWorkspaceModel';
import { PartyWorkspacePage } from './PartyWorkspacePage';
import './PartyCrew.css';

// THE SAME PARTY, RUN BY SOMEBODY WHO IS NOT ITS HOST.
//
// THE URL NAMES THE PARTY, AND THE SERVER CHECKS IT. One browser may hold
// several assignments — the same person helping at two parties is one device
// with two grants — so the party id in the route says which of them this page
// is about. It is a resource selector and never an authority: the server
// requires it to match a grant this device actually holds, and a party it has
// no grant for is the same nothing as no device at all. The page therefore
// asks for the party in the URL and does NOT follow the server somewhere else.
//
// NOT A SECOND PRODUCT. This page resolves who the device is, hands the
// workspace the Party Crew family of routes, and gets back the workspace — the
// same sections, the same panels, the same words, the same loading and failure
// states. The only three things it decides are which sections exist, where the
// person lands, and what sits where the host's "← Le tue feste" would be.
//
// NO AUTHENTICATED SHELL. There is no sidebar, no library, no albums, no
// search: a collaborator has no account and nothing else in NubArca to reach.
// What they get instead is their own name, their role, and the two things that
// are theirs to control — which devices they are using, and leaving.
//
// THE UI IS NOT THE AUTHORITY. `crewSections` hides what a role cannot use, but
// every route behind every panel re-reads the grants on the server and answers
// 404 regardless. A section rendered by mistake would be an empty section, not
// an open door.

export function PartyCrewWorkspacePage() {
  const { partyId } = useParams<{ partyId: string }>();
  const { t } = useI18n();
  const [state, setState] = useState<
    | { kind: 'loading' }
    | { kind: 'ready'; session: PartyCrewSession }
    | { kind: 'gone' }
    | { kind: 'left'; scope: 'party' | 'device' }
  >({ kind: 'loading' });

  useEffect(() => {
    let live = true;
    void (async () => {
      if (!partyId) { setState({ kind: 'gone' }); return; }
      try {
        // THE PARTY IN THE URL, and no other. A device without a grant for it
        // is told so; it is never quietly moved to one it does have, which
        // would make the address bar and the page disagree about which evening
        // somebody is running.
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
      <main className="crew-shell" data-testid="party-crew-workspace">
        <p className="crew-pair-lede" role="status">{t('crew.shell.loading')}</p>
      </main>
    );
  }

  // LEAVING UNMOUNTS THE PARTY, and that is the point of holding this state
  // here rather than inside the menu. Hiding a panel would leave every guest
  // name, arrival and photograph this person had already loaded sitting in the
  // DOM of a session they just ended.
  if (state.kind === 'left') {
    return (
      <main className="crew-shell" data-testid="party-crew-workspace" data-left={state.scope}>
        <div className="crew-pair-card">
          <h1 className="crew-pair-title">{t('crew.shell.leftTitle')}</h1>
          <p className="crew-pair-lede">
            {t(state.scope === 'device' ? 'crew.shell.disconnected' : 'crew.shell.leftParty')}
          </p>
        </div>
      </main>
    );
  }

  if (state.kind === 'gone') {
    return (
      <main className="crew-shell" data-testid="party-crew-workspace">
        <div className="crew-pair-card">
          <h1 className="crew-pair-title">{t('crew.shell.gone.title')}</h1>
          <p className="crew-pair-lede">{t('crew.shell.gone.body')}</p>
        </div>
      </main>
    );
  }

  const { session } = state;
  const sections = (status: PartyStatus): readonly WorkspaceSection[] =>
    crewSections(session.capabilities, status, WORKSPACE_SECTIONS);
  const landing = (status: PartyStatus): WorkspaceSection =>
    crewLandingSection(session.capabilities, status, WORKSPACE_SECTIONS);

  return (
    <PartyApiProvider api={crewPartyApi(session.partyId, session.capabilities)}>
      <div className="crew-shell" data-testid="party-crew-workspace">
        <PartyWorkspacePage
          sections={sections}
          landing={landing}
          header={(
            <CrewIdentity
              session={session}
              onLeft={(scope) => setState({ kind: 'left', scope })}
            />
          )}
        />
      </div>
    </PartyApiProvider>
  );
}

/**
 * Who this device is, and the two things that belong to the person rather than
 * to the party: their devices, and leaving.
 *
 * Folded into a details element rather than a menu, because it is opened rarely
 * and a menu would need focus management to be worth the same 44px.
 */
function CrewIdentity({
  session, onLeft,
}: {
  session: PartyCrewSession;
  onLeft(scope: 'party' | 'device'): void;
}) {
  const { t } = useI18n();
  return (
    <details className="crew-identity" data-testid="crew-identity">
      <summary>
        <span className="crew-identity-name">
          {t('crew.shell.as', {
            name: session.displayName,
            role: t(crewRoleLabelKey(session.roleKey)),
          })}
        </span>
      </summary>
      <div className="crew-identity-body">
        <CrewDevices partyId={session.partyId} onLeft={onLeft} />
      </div>
    </details>
  );
}

function CrewDevices({
  partyId, onLeft,
}: {
  partyId: string;
  onLeft(scope: 'party' | 'device'): void;
}) {
  const { t } = useI18n();
  const [load, setLoad] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [devices, setDevices] = useState<PartyCrewDevice[]>([]);
  const [busy, setBusy] = useState(false);

  const reload = useCallback(async () => {
    try {
      setDevices(await listMyPartyCrewDevices(partyId));
      setLoad('ready');
    } catch {
      setLoad('failed');
    }
  }, [partyId]);

  useEffect(() => { void reload(); }, [reload]);

  async function drop(grantId: string, isCurrent: boolean) {
    setBusy(true);
    try {
      await revokeMyPartyCrewDevice(partyId, grantId);
      // Dropping the grant you are HOLDING ends this session, so the workspace
      // goes with it rather than staying on screen full of party data.
      if (isCurrent) { onLeft('party'); return; }
      await reload();
    } catch (err) {
      if (!(err instanceof ApiError)) throw err;
      await reload();
    } finally { setBusy(false); }
  }

  return (
    <>
      <p className="crew-identity-note">{t('crew.shell.devicesHelp')}</p>

      {load === 'failed' && (
        <p className="crew-identity-note" role="status">{t('crew.shell.devicesFailed')}</p>
      )}

      {load === 'ready' && (
        <ul className="crew-identity-devices" data-testid="crew-my-devices">
          {devices.map((device) => (
            <li key={device.grantId}>
              <span>
                {device.label}
                {device.isCurrent && <> · {t('crew.shell.deviceThis')}</>}
              </span>
              {!device.isCurrent && (
                <button
                  type="button"
                  className="crew-identity-drop"
                  disabled={busy}
                  data-testid={`crew-my-device-drop-${device.grantId}`}
                  onClick={() => void drop(device.grantId, false)}
                >
                  {t('party.crew.device.remove')}
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {/* TWO DIFFERENT DECISIONS, and the product says which is which.
          Leaving one party keeps this browser paired to the others it helps
          at; disconnecting it ends all of them. A single "log out" that did
          one and read as the other would lose somebody two jobs when they
          meant to leave one. */}
      <button
        type="button"
        className="crew-identity-signout"
        disabled={busy}
        data-testid="crew-leave-party"
        onClick={() => void (async () => {
          setBusy(true);
          try { await leavePartyCrewParty(partyId); } finally { setBusy(false); onLeft('party'); }
        })()}
      >
        {t('crew.shell.leaveParty')}
      </button>

      <p className="crew-identity-note">{t('crew.shell.disconnectHelp')}</p>

      <button
        type="button"
        className="crew-identity-signout"
        disabled={busy}
        data-testid="crew-disconnect-device"
        onClick={() => void (async () => {
          setBusy(true);
          try { await disconnectPartyCrewDevice(); } finally { setBusy(false); onLeft('device'); }
        })()}
      >
        {t('crew.shell.disconnect')}
      </button>
    </>
  );
}
