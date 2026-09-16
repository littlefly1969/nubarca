import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  getAlbumPartySettings,
  listPartyMessages,
  listPartyUploads,
  listPartyGuestContent,
  queryPartyGuestDirectory,
  type AlbumPartyStatus,
  type GuestDirectorySummary,
  type Party,
  type PartyGuestContentSlot,
} from '@nubarca/api-client';
import { mainMediaSource } from '../partyModel';

// Everything the workspace knows about one party, gathered once.
//
// Four reads, each with its own rules about WHEN it is worth making:
//
//   The album's party settings, only once there is an album to ask about. A
//   party with no album must never send an album-scoped request with an
//   invented id.
//
//   The guest content slots, always: they are small, and the experience section
//   and the summary's checklist both need them.
//
//   The guest COUNTS, as a counts-only query (`take: 0`). The summary and the
//   live console need the totals; neither needs a single guest's name, so
//   neither asks for one. It is also a POST whose body carries no search, which
//   is why no personal data reaches a URL from here.
//
//   The moderation queues, only when the host is somewhere that shows what is
//   waiting in them. They are the same lists the queue pages load, so the cost
//   is one the host was going to pay anyway — but only if they look.
//
// Each read fails QUIETLY into "unknown" rather than into zero. A summary that
// says "0 arrivals" because a request failed is worse than one that says
// nothing, because the host would believe it.

export interface PartyFactsExtras {
  albumParty: AlbumPartyStatus | null;
  slots: PartyGuestContentSlot[];
  guests: GuestDirectorySummary | null;
  moderation: { uploads: number; messages: number } | null;
  /** Re-read the counts — the live console's refresh, and every mutation's. */
  refresh(): void;
  setAlbumParty(next: AlbumPartyStatus): void;
  setSlot(next: PartyGuestContentSlot): void;
}

export function usePartyFacts(
  party: Party | null,
  { wantsModeration }: { wantsModeration: boolean },
  onUnauthorized: () => void,
): PartyFactsExtras {
  const [albumParty, setAlbumParty] = useState<AlbumPartyStatus | null>(null);
  const [slots, setSlots] = useState<PartyGuestContentSlot[]>([]);
  const [guests, setGuests] = useState<GuestDirectorySummary | null>(null);
  const [moderation, setModeration] = useState<{ uploads: number; messages: number } | null>(null);
  const [nonce, setNonce] = useState(0);

  const partyId = party?.id ?? null;
  const albumId = party ? mainMediaSource(party)?.albumId ?? null : null;
  // The moderation read is expensive enough to be worth doing once per visit
  // rather than once per render of a section that mentions it.
  const askedModeration = useRef<string | null>(null);

  const refresh = useCallback(() => setNonce((n) => n + 1), []);

  const unauthorized = useCallback((err: unknown) => {
    if (err instanceof ApiError && err.status === 401) { onUnauthorized(); return true; }
    return false;
  }, [onUnauthorized]);

  useEffect(() => {
    if (!albumId) { setAlbumParty(null); return; }
    const ctrl = new AbortController();
    getAlbumPartySettings(albumId, ctrl.signal)
      .then(setAlbumParty)
      .catch((err) => { if (!ctrl.signal.aborted && !unauthorized(err)) setAlbumParty(null); });
    return () => ctrl.abort();
  }, [albumId, nonce, unauthorized]);

  useEffect(() => {
    if (!partyId) { setSlots([]); return; }
    const ctrl = new AbortController();
    listPartyGuestContent(partyId, ctrl.signal)
      .then(setSlots)
      .catch((err) => { if (!ctrl.signal.aborted && !unauthorized(err)) setSlots([]); });
    return () => ctrl.abort();
  }, [partyId, unauthorized]);

  useEffect(() => {
    if (!partyId) { setGuests(null); return; }
    const ctrl = new AbortController();
    // `take: 0` is the counts alone — no card, no person, no name.
    queryPartyGuestDirectory(partyId, { take: 0 }, ctrl.signal)
      .then((page) => setGuests(page.summary))
      .catch((err) => { if (!ctrl.signal.aborted && !unauthorized(err)) setGuests(null); });
    return () => ctrl.abort();
  }, [partyId, nonce, unauthorized]);

  useEffect(() => {
    if (!albumId || !wantsModeration || party?.status === 'draft') return;
    const key = `${albumId}:${nonce}`;
    if (askedModeration.current === key) return;
    askedModeration.current = key;
    const ctrl = new AbortController();
    void Promise.all([
      listPartyUploads(albumId, ctrl.signal).catch(() => null),
      listPartyMessages(albumId, ctrl.signal).catch(() => null),
    ]).then(([uploads, messages]) => {
      if (ctrl.signal.aborted) return;
      if (uploads === null && messages === null) { setModeration(null); return; }
      setModeration({
        uploads: uploads?.items.filter((item) => item.status === 'pending').length ?? 0,
        messages: messages?.items.filter((item) => item.status === 'pending').length ?? 0,
      });
    });
    return () => ctrl.abort();
  }, [albumId, wantsModeration, party?.status, nonce]);

  const setSlot = useCallback((next: PartyGuestContentSlot) => {
    setSlots((current) => current.map((slot) => (slot.kind === next.kind ? next : slot)));
  }, []);

  return { albumParty, slots, guests, moderation, refresh, setAlbumParty, setSlot };
}
