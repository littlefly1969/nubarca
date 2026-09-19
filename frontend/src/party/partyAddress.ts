/* WHERE THE PARTY IS, as words and as a map link.
 *
 * Both halves used to live inside the guest surface's location slot, which is
 * the only place that needed them — until a host could share the address
 * without inviting anybody. Two implementations of "the map link for this
 * venue" would be two answers, so there is one, here.
 *
 * THE MAP LINK IS BUILT, NEVER STORED. An arbitrary external URL kept as
 * authority is somebody else's page one QR code away, which is why
 * `PartyLocationContent` holds an ADDRESS and no map field at all.
 */

export interface PartyVenue {
  venueName?: string | null;
  address?: string | null;
  note?: string | null;
}

/**
 * A search link for this venue, or null when there is no address to search for.
 *
 * A venue NAME alone does not produce one: "Cascina Rosa" resolves to whichever
 * Cascina Rosa the map service prefers, and sending somebody to the wrong one
 * is worse than sending them nothing.
 */
export function partyVenueMapUrl(venue: PartyVenue): string | null {
  const address = venue.address?.trim();
  if (!address) return null;
  const query = [venue.venueName?.trim(), address].filter(Boolean).join(' ');
  return `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(query)}`;
}

/**
 * The message a host sends when somebody asks where the party is.
 *
 * Composed in the READER's language by the client, from facts the server
 * stated — the same division the product uses for error codes, and the reason
 * a fourth language is not a server change.
 *
 * It carries NO URL but the map link: not the guest link, not an invitation
 * token, not the upload or print capability. An address travels through chats
 * and gets forwarded; a capability must not travel with it.
 */
export function partyAddressShareText(
  share: { title: string } & PartyVenue,
  labels: { when?: string | null } = {},
): string {
  const lines: string[] = [share.title.trim()];
  if (labels.when) lines.push(labels.when);

  const venue = share.venueName?.trim();
  const address = share.address?.trim();
  const note = share.note?.trim();
  if (venue) lines.push(venue);
  if (address) lines.push(address);
  if (note) lines.push(note);

  const maps = partyVenueMapUrl(share);
  if (maps) lines.push(maps);

  return lines.join('\n');
}
