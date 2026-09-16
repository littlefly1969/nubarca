import { describe, expect, it, vi } from 'vitest';
import { countsPhrase, formatWhen, historyLine, invitationStatusLine, peopleInReadingOrder } from './guestConsoleFormat';
import { copyWhenReady, openExternal } from './guestShare';
import type { GuestDirectoryPerson, PartyInvitationDeliveryView } from '@nubarca/api-client';

// What a card says, and what the browser will actually do with a link — the two
// places where "shared" must never be allowed to read as "sent".

const t = ((key: string, params?: Record<string, string | number>) =>
  (params ? `${key}(${Object.entries(params).map(([k, v]) => `${k}=${v}`).join(',')})` : key)) as never;
const formatDate = ((iso: string, options?: Intl.DateTimeFormatOptions) =>
  (options?.timeStyle ? `T:${iso.slice(11, 16)}` : `D:${iso.slice(0, 10)}`)) as never;

const view = (over: Partial<PartyInvitationDeliveryView> = {}): PartyInvitationDeliveryView => ({
  state: 'not_sent', lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null,
  lastSentAt: null, lastAttemptChannel: null, ...over,
});

const person = (over: Partial<GuestDirectoryPerson> = {}): GuestDirectoryPerson => ({
  guestId: 'g', name: 'Chi', isAdditionalGuest: false, rsvpStatus: 'pending',
  checkedInAt: null, checkInSource: null, matched: false, ...over,
});

describe('what a card says about an invitation', () => {
  const now = new Date('2027-06-02T09:00:00Z');

  it('names the channel of the latest delivery, and never calls a share a send', () => {
    expect(invitationStatusLine(view(), t, formatDate, now))
      .toEqual({ text: 'party.console.line.not_shared', problem: false });

    const shared = invitationStatusLine(
      view({
        state: 'shared', lastAttemptChannel: 'whatsapp', lastAttemptStatus: 'shared',
        lastAttemptKind: 'initial', lastAttemptAt: '2027-06-02T08:10:00Z',
      }), t, formatDate, now);
    expect(shared.text).toContain('party.console.line.whatsapp_shared');
    expect(shared.text).toContain('party.console.when.today');
    expect(shared.problem).toBe(false);
  });

  it('marks an email that failed or was never confirmed as something to look at', () => {
    expect(invitationStatusLine(
      view({ lastAttemptChannel: 'email', lastAttemptStatus: 'failed', lastAttemptKind: 'resend', lastAttemptAt: '2027-06-01T18:42:00Z' }),
      t, formatDate, now).problem).toBe(true);
    expect(invitationStatusLine(
      view({ lastAttemptChannel: 'email', lastAttemptStatus: 'pending', lastAttemptKind: 'initial', lastAttemptAt: '2027-06-02T08:00:00Z' }),
      t, formatDate, now).problem).toBe(true);
  });

  it('reads yesterday as yesterday, and anything older as its day', () => {
    // CALENDAR days in the reader's own timezone — "yesterday at 23:40" is
    // yesterday however few hours ago it was — so the fixtures are local
    // instants rather than a fixed offset from UTC.
    const at = (year: number, month: number, day: number, hour: number, minute: number) =>
      new Date(year, month, day, hour, minute).toISOString();
    const today = new Date(2027, 5, 2, 9, 0);

    expect(formatWhen(at(2027, 5, 2, 7, 30), t, formatDate, today)).toContain('party.console.when.today');
    expect(formatWhen(at(2027, 5, 1, 23, 40), t, formatDate, today)).toContain('party.console.when.yesterday');
    expect(formatWhen(at(2027, 4, 20, 12, 0), t, formatDate, today)).toMatch(/^D:/);
    expect(formatWhen('not a date', t, formatDate, today)).toBe('');
  });

  it('describes one history entry by its own channel', () => {
    expect(historyLine(
      { channel: 'copy', kind: 'resend', status: 'shared', createdAt: '2027-06-02T08:00:00Z' }, t, formatDate, now).text)
      .toContain('party.console.line.link_copied');
  });

  it('counts only what is worth saying, and falls back to how many people there are', () => {
    expect(countsPhrase({ attending: 2, pending: 1, declined: 0, arrived: 0 }, 3, t))
      .toBe('party.console.card.counts.attending(count=2) · party.console.card.counts.pending(count=1)');
    expect(countsPhrase({ attending: 0, pending: 0, declined: 0, arrived: 0 }, 4, t))
      .toBe('party.console.card.counts.people(count=4)');
  });

  it('puts whoever was searched for first, then whoever is still expected', () => {
    const people = [
      person({ guestId: 'a', name: 'Anna', rsvpStatus: 'declined' }),
      person({ guestId: 'b', name: 'Bruno', rsvpStatus: 'attending' }),
      person({ guestId: 'c', name: 'Carla', rsvpStatus: 'attending', matched: true }),
      person({ guestId: 'd', name: 'Dino', rsvpStatus: 'attending', checkedInAt: '2027-06-02T08:00:00Z' }),
    ];
    expect(peopleInReadingOrder(people, true).map((p) => p.guestId)).toEqual(['c', 'b', 'a', 'd']);
    // Before the party nobody has arrived, so the list keeps the host's order.
    expect(peopleInReadingOrder(people, false).map((p) => p.guestId)).toEqual(['c', 'a', 'b', 'd']);
  });
});

describe('handing a link over', () => {
  it('opens a new tab and drops the opener', () => {
    const opened: { opener: unknown } = { opener: {} };
    vi.stubGlobal('open', vi.fn(() => opened));
    expect(openExternal('https://wa.me/39?text=x')).toBe(true);
    expect(opened.opener).toBeNull();
    vi.unstubAllGlobals();
  });

  it('says so when the browser refuses', () => {
    vi.stubGlobal('open', vi.fn(() => null));
    expect(openExternal('https://wa.me/39?text=x')).toBe(false);
    vi.stubGlobal('open', vi.fn(() => { throw new Error('blocked'); }));
    expect(openExternal('https://wa.me/39?text=x')).toBe(false);
    vi.unstubAllGlobals();
  });

  it('copies what the request resolves to, and never leaves a rejection unhandled', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    await expect(copyWhenReady(Promise.resolve('https://x/party/invite/T'))).resolves.toBe(true);
    expect(writeText).toHaveBeenCalledWith('https://x/party/invite/T');

    // A refused share, a refused clipboard, and no clipboard at all: all "tell
    // the host to copy it", never an unhandled rejection.
    await expect(copyWhenReady(Promise.reject(new Error('409')))).resolves.toBe(false);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) }, configurable: true,
    });
    await expect(copyWhenReady(Promise.resolve('x'))).resolves.toBe(false);
    Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: true });
    await expect(copyWhenReady(Promise.resolve('x'))).resolves.toBe(false);
  });
});
