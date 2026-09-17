import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  type AlbumPartyStatus,
  type GuestDirectorySummary,
  type Party,
  type PartyGuestContentSlot,
} from '@nubarca/api-client';
import { mainMediaSource } from '../partyModel';
import { usePartyApi } from './partyApi';
import type { Loaded } from './partyWorkspaceModel';

// Everything the workspace knows about one party, gathered once.
//
// Four reads, each with its own rules about WHEN it is worth making:
//
//   The album's party settings, only once there is an album to ask about. A
//   party with no album must never send an album-scoped request with an
//   invented id — and its settings are then `ready` with `null`, because
//   having no album is a FACT, not a missing answer.
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
//   waiting in them, and SEPARATELY: one list failing must not report the
//   other's count as the whole truth, and must never report its own as zero.
//
// EVERY READ HAS THREE STATES, not two. `null` used to mean both "has not
// arrived" and "the request failed", and the surfaces above could not tell
// them apart: a failed settings read left the summary showing a placeholder for
// ever, and a failed content read made the checklist say the invitation had not
// been written. Unknown is not zero, and it is not "not configured" either.
//
// A 401 is never swallowed. It is the one failure that is not about this party
// at all, and the application has to hear about it wherever it happens.

export interface PartyFactsExtras {
  albumParty: Loaded<AlbumPartyStatus | null>;
  slots: Loaded<readonly PartyGuestContentSlot[]>;
  guests: Loaded<GuestDirectorySummary | null>;
  moderation: { uploads: Loaded<number>; messages: Loaded<number> };
  /** Read everything again — the live console's refresh, and a retry. */
  refresh(): void;
  /**
   * Adopt counts the SERVER just answered a mutation with.
   *
   * This is the targeted invalidation a guest mutation needs, taken to its
   * end: the console already holds the party's new summary, so the workspace
   * adopts it instead of asking for a number it has already been given. No
   * request at all, and the two surfaces cannot disagree about it.
   */
  adoptGuests(next: GuestDirectorySummary): void;
  setAlbumParty(next: AlbumPartyStatus): void;
  setSlot(next: PartyGuestContentSlot): void;
}

const LOADING = { status: 'loading' } as const;
const FAILED = { status: 'error' } as const;
const ready = <T>(value: T): Loaded<T> => ({ status: 'ready', value });

export function usePartyFacts(
  party: Party | null,
  { wantsModeration }: { wantsModeration: boolean },
  onUnauthorized: () => void,
): PartyFactsExtras {
  // Host or collaborator: the same four reads, a different family of routes.
  const api = usePartyApi();
  const [albumParty, setAlbumPartyState] =
    useState<Loaded<AlbumPartyStatus | null>>(LOADING);
  const [slots, setSlots] = useState<Loaded<readonly PartyGuestContentSlot[]>>(LOADING);
  const [guests, setGuests] = useState<Loaded<GuestDirectorySummary | null>>(LOADING);
  const [uploads, setUploads] = useState<Loaded<number>>(LOADING);
  const [messages, setMessages] = useState<Loaded<number>>(LOADING);
  const [nonce, setNonce] = useState(0);

  const partyId = party?.id ?? null;
  const partyLoaded = party !== null;
  const albumId = party ? mainMediaSource(party)?.albumId ?? null : null;
  // The moderation read is expensive enough to be worth doing once per visit
  // rather than once per render of a section that mentions it.
  const askedModeration = useRef<string | null>(null);

  const refresh = useCallback(() => {
    askedModeration.current = null;
    setNonce((n) => n + 1);
  }, []);
  const adoptGuests = useCallback(
    (next: GuestDirectorySummary) => setGuests(ready(next)), []);

  /** True when the failure was a 401 — handed to the application, not to a panel. */
  const unauthorized = useCallback((err: unknown) => {
    if (err instanceof ApiError && err.status === 401) { onUnauthorized(); return true; }
    return false;
  }, [onUnauthorized]);

  useEffect(() => {
    // THE PARTY ITSELF IS STILL COMING. Nothing is known yet — not even whether
    // there is an album — so this stays `loading`. Reporting `ready` with no
    // value here would be the same mistake in a new place: the live console
    // would draw "presenze registrate 0" for a frame before the real counts
    // arrived, which is a number the product had not been told.
    if (!partyLoaded) { setAlbumPartyState(LOADING); return; }
    // The party is loaded and has no album: a known absence, not a pending
    // answer, and nothing album-scoped may be asked for with an invented id.
    if (!albumId) { setAlbumPartyState(ready(null)); return; }
    const ctrl = new AbortController();
    setAlbumPartyState(LOADING);
    api.getAlbumPartySettings(albumId, ctrl.signal)
      .then((value) => { if (!ctrl.signal.aborted) setAlbumPartyState(ready(value)); })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        unauthorized(err);
        setAlbumPartyState(FAILED);
      });
    return () => ctrl.abort();
  }, [partyLoaded, albumId, nonce, unauthorized, api]);

  useEffect(() => {
    if (!partyId) { setSlots(LOADING); return; }
    const ctrl = new AbortController();
    setSlots(LOADING);
    api.listPartyGuestContent(partyId, ctrl.signal)
      .then((value) => { if (!ctrl.signal.aborted) setSlots(ready(value)); })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        unauthorized(err);
        setSlots(FAILED);
      });
    return () => ctrl.abort();
  }, [partyId, nonce, unauthorized, api]);

  useEffect(() => {
    if (!partyId) { setGuests(LOADING); return; }
    const ctrl = new AbortController();
    // THE COUNTS ALONE — no card, no person, no name. A surface with no guest
    // list still shows how many are expected and how many arrived, because
    // somebody running the evening needs that and it is not personal data. The
    // adapter decides where the numbers come from: the host's own directory, or
    // a crew route that answers the summary and nothing else.
    api.getGuestCounts(partyId, ctrl.signal)
      .then((counts) => { if (!ctrl.signal.aborted) setGuests(ready(counts.summary)); })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        unauthorized(err);
        setGuests(FAILED);
      });
    return () => ctrl.abort();
  }, [partyId, nonce, unauthorized, api]);

  useEffect(() => {
    if (!albumId || !wantsModeration || party?.status === 'draft') return;
    const key = `${albumId}:${nonce}`;
    if (askedModeration.current === key) return;
    askedModeration.current = key;
    const ctrl = new AbortController();
    setUploads(LOADING);
    setMessages(LOADING);

    // Two reads, two answers. `Promise.all` with a shared catch would have let
    // one failure decide for both; each settles on its own.
    void api.listPartyUploads(albumId, ctrl.signal)
      .then((list) => {
        if (!ctrl.signal.aborted) {
          setUploads(ready(list.items.filter((i) => i.status === 'pending').length));
        }
      })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        unauthorized(err);
        setUploads(FAILED);
      });
    void api.listPartyMessages(albumId, ctrl.signal)
      .then((list) => {
        if (!ctrl.signal.aborted) {
          setMessages(ready(list.items.filter((i) => i.status === 'pending').length));
        }
      })
      .catch((err) => {
        if (ctrl.signal.aborted) return;
        unauthorized(err);
        setMessages(FAILED);
      });
    return () => ctrl.abort();
  }, [albumId, wantsModeration, party?.status, nonce, unauthorized, api]);

  const setSlot = useCallback((next: PartyGuestContentSlot) => {
    setSlots((current) => (current.status === 'ready'
      ? ready(current.value.map((slot) => (slot.kind === next.kind ? next : slot)))
      : current));
  }, []);

  const setAlbumParty = useCallback(
    (next: AlbumPartyStatus) => setAlbumPartyState(ready(next)), []);

  return {
    albumParty,
    slots,
    guests,
    moderation: { uploads, messages },
    refresh,
    adoptGuests,
    setAlbumParty,
    setSlot,
  };
}
