import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { ApiError, listParties, setAlbumPartyMode, type PartySummary } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { partyStatusLabelKey } from '../party/partyModel';

// The album's link to its party — and DELIBERATELY nothing more.
//
// This block used to be the whole Party application: guest access, upload
// switches, moderation links, slideshow numbers, the game, its deck, printing.
// Party has its own destination now, so keeping a second complete interface for
// the same configuration would leave two places to change one thing and two
// places for them to disagree. What remains is one sentence and one door.
//
// The compatibility entry point survives inside it: an album that is not yet
// part of a party can still become one from here, through the SAME
// `setAlbumPartyMode` call it always used — which creates the party, links the
// album as its main source and publishes it, server-side, in one transaction.
// It then hands the host straight to the party, because that is where the rest
// of the decisions now live.

export function AlbumPartyBridge({ albumId, partyMode }: { albumId: string; partyMode: boolean }) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [party, setParty] = useState<PartySummary | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Which party is this album's, answered from the party list rather than by a
  // new album-shaped endpoint: the root already knows its main album, so asking
  // it is one request and no new contract.
  useEffect(() => {
    const ctrl = new AbortController();
    listParties(ctrl.signal)
      .then((parties) => setParty(parties.find((p) => p.mainAlbumId === albumId) ?? null))
      .catch(() => { if (!ctrl.signal.aborted) setParty(null); });
    return () => ctrl.abort();
  }, [albumId, partyMode]);

  async function useForParty() {
    setBusy(true); setError(null);
    try {
      await setAlbumPartyMode(albumId, true);
      const parties = await listParties();
      setParty(parties.find((p) => p.mainAlbumId === albumId) ?? null);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('party.bridge.useFailed'));
    } finally { setBusy(false); }
  }

  if (party === undefined) return null;

  return (
    <div className="album-party-bridge" data-testid="album-party-bridge">
      <h4>{t('party.bridge.heading')}</h4>
      {party ? (
        <>
          <p className="muted">
            {party.title} · {t(partyStatusLabelKey(party.status))}
          </p>
          <p>
            <Link to={`/parties/${party.id}`} data-testid="album-party-open">
              {t('party.bridge.open')}
            </Link>
          </p>
        </>
      ) : (
        <>
          <p className="muted">{t('party.bridge.unlinked')}</p>
          <button
            type="button" className="row-action" data-testid="album-party-use"
            disabled={busy} onClick={() => void useForParty()}
          >
            {t('party.bridge.use')}
          </button>
          {error && <p className="inline-error" role="alert">{error}</p>}
        </>
      )}
    </div>
  );
}
