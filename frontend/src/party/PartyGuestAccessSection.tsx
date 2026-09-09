import { useState } from 'react';
import {
  ApiError,
  setAlbumPartyMode,
  type AlbumPartyStatus,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// Whether the guests can reach this party, and until when.
//
// The source of truth is the CAPABILITY from P1, not the party's status: this
// switch calls the same `setAlbumPartyMode` the album panel always called, on
// the party's main album, and the backend's own `EnableAsync` performs the
// Draft -> Published transition as part of minting the link. There is no second
// token minting path here, and turning access off deliberately does NOT move
// the status back — a party that was published was published.
//
// A party that has ENDED may still have guest access, and that is not a bug to
// tidy up: it is what the post-event library will be built on.

export function PartyGuestAccessSection({
  party, albumId, albumParty, onAlbumPartyUpdated,
}: {
  party: Party;
  albumId: string;
  albumParty: AlbumPartyStatus | null;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [busy, setBusy] = useState(false);

  async function toggle(next: boolean) {
    setBusy(true);
    try {
      onAlbumPartyUpdated(await setAlbumPartyMode(albumId, next));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setBusy(false); }
  }

  const on = albumParty?.partyMode ?? false;
  const expired =
    party.guestAccessExpiresAt !== null && new Date(party.guestAccessExpiresAt) <= new Date();

  return (
    <section className="party-card" data-testid="party-guest-access">
      <h3>{t('party.guest.heading')}</h3>
      <p className="muted">{t('party.guest.help')}</p>

      <label className="party-toggle">
        <input
          type="checkbox" checked={on} disabled={busy || albumParty === null}
          aria-label={t('party.guest.on')}
          onChange={(e) => void toggle(e.target.checked)}
        />
        <span>{t('party.guest.on')}</span>
      </label>

      {on && albumParty?.partyUrl && (
        <p className="party-guest-url" data-testid="party-guest-url">
          {t('party.guest.link')}{' '}
          <a href={albumParty.partyUrl} target="_blank" rel="noopener noreferrer">
            {window.location.origin}{albumParty.partyUrl}
          </a>
        </p>
      )}

      {/* WHEN it ends is edited with the party's other details, because it is
          the party's own data and shares its version — one form, one save, one
          concurrency check. What belongs here is the consequence. */}
      {party.guestAccessExpiresAt && (
        <p className="muted" data-testid="party-guest-expires">
          {t('party.guest.expiresLabel')}: {formatDate(party.guestAccessExpiresAt)}
        </p>
      )}
      {expired && <p className="inline-error" role="status">{t('party.guest.expired')}</p>}
    </section>
  );
}
