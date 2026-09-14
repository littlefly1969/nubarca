import { useEffect, useState } from 'react';
import { ApiError, setPartyCovers, type Party } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartySlotImageField } from './PartyImageField';

// ONE of the party's two covers: the photograph that opens a guest page.
//
// The invitation's cover sits at the top of the invitation; the live cover takes
// over while the party is on. Each is optional, and each card says what happens
// without it — the album's chosen cover for the invitation, the invitation's
// cover for the party — so "no photo" is a choice with a visible consequence
// rather than an empty field.
//
// The server takes both covers WHOLE in one PUT, so this card sends the other
// one exactly as the party holds it. Choosing a photograph files it in no album,
// as a slot photograph never is.

type Which = 'invitation' | 'live';

export function PartyCoverCard({
  party, which, albumId, onPartyUpdated,
}: {
  party: Party;
  which: Which;
  /** The party's album, offered as a place to choose a photograph from — or null. */
  albumId: string | null;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const stored = () => (which === 'invitation'
    ? { fileItemId: party.invitationCoverFileItemId ?? null, previewUrl: party.invitationCoverUrl ?? null }
    : { fileItemId: party.liveCoverFileItemId ?? null, previewUrl: party.liveCoverUrl ?? null });
  const [draft, setDraft] = useState(stored);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] =
    useState<'idle' | 'saved' | 'conflict' | 'invalidMedia' | 'failed'>('idle');

  // Re-seeded whenever the SERVER's version moves: a save here, a save of the
  // other cover, or any other edit of the party.
  useEffect(() => { setDraft(stored()); }, [party.version, which]);

  async function save() {
    setBusy(true); setStatus('idle');
    try {
      onPartyUpdated(await setPartyCovers(party.id, {
        invitationCoverFileItemId: which === 'invitation'
          ? draft.fileItemId : party.invitationCoverFileItemId ?? null,
        liveCoverFileItemId: which === 'live'
          ? draft.fileItemId : party.liveCoverFileItemId ?? null,
        version: party.version,
      }));
      setStatus('saved');
    } catch (err) {
      const body = (err as ApiError).body as { error?: string; party?: Party } | undefined;
      if (err instanceof ApiError && err.status === 409 && body?.party) {
        onPartyUpdated(body.party);
        setStatus('conflict');
      } else if (err instanceof ApiError && err.status === 400 && body?.error === 'invalid_media') {
        setStatus('invalidMedia');
      } else {
        setStatus('failed');
      }
    } finally { setBusy(false); }
  }

  return (
    <section className="party-card" data-testid={`party-cover-${which}`}>
      <h3>{t(`partyCover.${which}.heading` as 'partyCover.invitation.heading')}</h3>
      <p className="muted">{t(`partyCover.${which}.help` as 'partyCover.invitation.help')}</p>
      <PartySlotImageField
        kind={`cover-${which}`}
        albumId={albumId}
        fileItemId={draft.fileItemId}
        previewUrl={draft.previewUrl}
        disabled={busy}
        onChange={(next) => setDraft({
          fileItemId: next?.fileItemId ?? null,
          previewUrl: next?.previewUrl ?? null,
        })}
      />
      <button
        type="button" className="row-action-primary" disabled={busy}
        data-testid={`party-cover-save-${which}`}
        onClick={() => void save()}
      >
        {t('party.overview.save')}
      </button>
      {status === 'saved' && <p className="muted" role="status">{t('party.overview.saved')}</p>}
      {status === 'conflict' && (
        <p className="inline-error" role="alert">{t('party.overview.conflict')}</p>
      )}
      {status === 'invalidMedia' && (
        <p className="inline-error" role="alert" data-testid={`party-cover-invalid-${which}`}>
          {t('partyContent.imageInvalid')}
        </p>
      )}
      {status === 'failed' && (
        <p className="inline-error" role="alert">{t('party.overview.saveFailed')}</p>
      )}
    </section>
  );
}
