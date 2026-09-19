import { useEffect, useState } from 'react';
import { ApiError, type Party, type PartyGuestContentSlot } from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { copyWhenReady } from '../guestShare';
import { partyAddressShareText, partyVenueMapUrl } from '../partyAddress';
import { usePartyApi } from './partyApi';
import { Button, Notice, Panel } from './ui';
import type { Loaded, WorkspaceSection } from './partyWorkspaceModel';

// WHERE THE PARTY IS, and the one action that hands it to somebody.
//
// IT HAS NOTHING TO DO WITH THE GUEST LIST. Until this card existed, the only
// way to tell anybody where the party was, was to create an invitation group
// and share a personal invitation — so a host running an open evening, with
// nobody on a list and nothing to RSVP to, had no way to answer "where is it?"
// without inventing a fictional guest. Nothing here reads a guest, a group, an
// invitation or an RSVP, and the server's route does not either.
//
// AND IT HANDS OVER NO CAPABILITY. The payload is the party's name, its date
// and its venue. Not the guest link, not an invitation token, not the upload or
// print capability, not an email address. An address travels through chats and
// gets forwarded; a bearer URL must not travel with it. Somebody who wants to
// give away the party itself has the share card below, which is a different
// decision and says so.

interface Venue {
  venueName: string | null;
  address: string | null;
  note: string | null;
}

/**
 * The venue the host wrote, out of the party's own location slot.
 *
 * The slot's `enabled` flag governs whether GUESTS see that section; it does
 * not govern whether the host knows their own address, so it is deliberately
 * not consulted here.
 */
export function venueFromSlots(slots: readonly PartyGuestContentSlot[]): Venue | null {
  const slot = slots.find((s) => s.kind === 'location');
  if (!slot) return null;
  const content = slot.content as Record<string, unknown> | null | undefined;
  const text = (key: string): string | null => {
    const value = content?.[key];
    if (typeof value !== 'string') return null;
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  };
  return { venueName: text('venueName'), address: text('address'), note: text('note') };
}

type Share = 'idle' | 'sharing' | 'shared' | 'copied' | 'failed';

export function PartyAddressCard({
  party, slots, onNavigate,
}: {
  party: Party;
  slots: Loaded<readonly PartyGuestContentSlot[]>;
  onNavigate(section: WorkspaceSection): void;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const api = usePartyApi();
  const [state, setState] = useState<Share>('idle');

  // The confirmation is short-lived, and it is never the only thing that says
  // the address exists — it is on the card either way.
  useEffect(() => {
    if (state !== 'shared' && state !== 'copied') return undefined;
    const timer = setTimeout(() => setState('idle'), 4000);
    return () => clearTimeout(timer);
  }, [state]);

  // THE AUTHORITY, and it is the party's own FACTS rather than what the
  // invitation says: a Regista runs the evening and does not hand out its
  // address. The server refuses them on the same key, so this hides an action
  // that would be refused rather than being the boundary itself.
  if (!api.can(CREW_CAPABILITIES.detailsManage)) return null;
  // Asked for and not answered. A card that claimed there was no address would
  // be worse than one that is not there.
  if (slots.status !== 'ready') return null;

  const venue = venueFromSlots(slots.value);
  const hasAddress = Boolean(venue?.address);

  const share = async () => {
    setState('sharing');
    // The server records the share and hands back the facts; the browser
    // composes the words in the READER's language. Started before the share
    // sheet so Safari's clipboard, which only accepts a write inside the tap,
    // can take a promise.
    const pending = api.sharePartyAddress(party.id).then((answer) => partyAddressShareText(
      answer,
      {
        when: answer.eventStartsAt
          ? t('party.address.shareWhen', { date: formatDate(answer.eventStartsAt) })
          : null,
      },
    ));
    const copying = copyWhenReady(pending);

    try {
      const text = await pending;
      // navigator.share where the browser has it — it is what puts the address
      // into WhatsApp rather than into a web page — and the clipboard where it
      // does not. A dismissed share sheet is not a failure.
      if (typeof navigator !== 'undefined' && typeof navigator.share === 'function') {
        try {
          await navigator.share({ title: party.title, text });
          setState('shared');
          return;
        } catch {
          /* dismissed, or refused: the clipboard write above still stands */
        }
      }
      setState(await copying ? 'copied' : 'failed');
    } catch (err) {
      void copying;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setState('failed');
    }
  };

  const maps = venue ? partyVenueMapUrl(venue) : null;

  return (
    <Panel
      title={t('party.address.heading')}
      note={t('party.address.note')}
      testId="party-address"
    >
      {hasAddress && venue ? (
        <>
          <div className="pw-rows">
            <div className="pw-row">
              <span className="pw-row-text">
                {venue.venueName && (
                  <span className="pw-row-label">{venue.venueName}</span>
                )}
                <span className="pw-row-note">{venue.address}</span>
                {venue.note && <span className="pw-row-note">{venue.note}</span>}
              </span>
            </div>
          </div>
          <div className="pw-panel-actions">
            <Button
              tone="primary"
              busy={state === 'sharing'}
              data-testid="party-address-share"
              onClick={() => void share()}
            >
              {t('party.address.share')}
            </Button>
            {maps && (
              <a
                className="pw-btn"
                href={maps}
                target="_blank"
                rel="noopener noreferrer"
                data-testid="party-address-map"
              >
                {t('party.address.openMap')}
              </a>
            )}
          </div>
          {state === 'shared' && (
            <p className="pw-small pw-muted" role="status" data-testid="party-address-shared">
              {t('party.address.shared')}
            </p>
          )}
          {state === 'copied' && (
            <p className="pw-small pw-muted" role="status" data-testid="party-address-copied">
              {t('party.address.copied')}
            </p>
          )}
          {state === 'failed' && (
            <Notice tone="error" testId="party-address-failed">
              <p>{t('party.address.failed')}</p>
            </Notice>
          )}
        </>
      ) : (
        /* NO ADDRESS, so no "share" that would send an empty message. The
           action offered is the one that makes the other possible. */
        <div className="pw-panel-actions">
          <Button
            data-testid="party-address-set"
            onClick={() => onNavigate('experience')}
          >
            {t('party.address.set')}
          </Button>
        </div>
      )}
    </Panel>
  );
}
