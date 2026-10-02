import {
  DEFAULT_PARTY_GUESTBOOK_TEMPLATE,
  PARTY_GUESTBOOK_TEMPLATE_KEYS,
  type PartyGuestbookTemplateKey,
} from '@nubarca/contracts';
import type { MessageKey } from '../i18n';

// THE DESIGNS A MEMORY IS DRAWN WITH, declared once.
//
// A memory stores a template KEY and the VERSION the server gave it. This is
// the table that turns the pair into a layout, so no component asks "if
// polaroid…": the renderer looks the pair up and draws what the entry says.
//
// Two rules keep a published memory looking the way its author saw it:
//   * a version is never edited — a redesign is a new entry (`polaroid@2`)
//     beside the old one, and `PUBLISHABLE` moves to it;
//   * no version is removed while a memory may still name it.
//
// What a template decides is deliberately small: the photograph's frame, and
// the class the stylesheet draws the rest with. The guest chooses the memory,
// not the layout.

export interface GuestbookTemplate {
  key: PartyGuestbookTemplateKey;
  version: number;
  labelKey: MessageKey;
  /**
   * The photograph's frame, width / height, for a photograph of `photoAspect`.
   * The framing a guest chose — centre and zoom — is placed INSIDE this frame
   * by the shared photo placement, so it never depends on a screen's pixels.
   */
  frameAspect(photoAspect: number): number;
  /** The CSS modifier the memory card is drawn with. */
  className: string;
  /**
   * The photo well: what the frame shows beside a photograph zoomed out. The
   * card's stylesheet draws it (.guestbook-memory-photo); this is the same
   * colour for the framing editor, and the Fire TV's TEMPLATE_LOOK carries it.
   */
  photoWell: string;
}

/** The well every design has unless it says otherwise. */
const DARK_WELL = 'rgb(10 15 26 / 70%)';

/** A photograph that is wider than it is tall, by any margin. */
const isLandscape = (aspect: number) => aspect > 1.02;
const isPortrait = (aspect: number) => aspect < 0.98;

const TEMPLATES: readonly GuestbookTemplate[] = [
  {
    key: 'nubarca',
    version: 1,
    labelKey: 'partyGuestbookTemplate.nubarca',
    // The photograph leads: tall for a tall picture, wide for a wide one.
    frameAspect: (aspect) => (isLandscape(aspect) ? 4 / 3 : isPortrait(aspect) ? 4 / 5 : 1),
    className: 'guestbook-memory--nubarca-1',
    photoWell: DARK_WELL,
  },
  {
    key: 'polaroid',
    version: 1,
    labelKey: 'partyGuestbookTemplate.polaroid',
    // The instant print's square, whatever the photograph's shape.
    frameAspect: () => 1,
    className: 'guestbook-memory--polaroid-1',
    photoWell: '#d9d6cf',
  },
  {
    key: 'editorial',
    version: 1,
    labelKey: 'partyGuestbookTemplate.editorial',
    // A magazine's proportions: a wide band, or a column.
    frameAspect: (aspect) => (isLandscape(aspect) ? 3 / 2 : isPortrait(aspect) ? 3 / 4 : 1),
    className: 'guestbook-memory--editorial-1',
    photoWell: DARK_WELL,
  },
  {
    key: 'celebration',
    version: 1,
    labelKey: 'partyGuestbookTemplate.celebration',
    frameAspect: (aspect) => (isLandscape(aspect) ? 4 / 3 : isPortrait(aspect) ? 4 / 5 : 1),
    className: 'guestbook-memory--celebration-1',
    photoWell: DARK_WELL,
  },
];

const BY_KEY_AND_VERSION = new Map<string, GuestbookTemplate>(
  TEMPLATES.map((t) => [`${t.key}@${t.version}`, t]),
);

/**
 * What a guest may choose NOW, one per key, in the order the composer offers
 * them — the current version of each. The server decides the version it
 * stores; this only has to draw the same one in the preview.
 */
export const PUBLISHABLE_GUESTBOOK_TEMPLATES: readonly GuestbookTemplate[] =
  PARTY_GUESTBOOK_TEMPLATE_KEYS.map((key) => {
    const current = TEMPLATES.filter((t) => t.key === key)
      .reduce((latest, t) => (t.version > latest.version ? t : latest));
    return current;
  });

/** The template a new memory starts with. */
export const DEFAULT_GUESTBOOK_TEMPLATE: GuestbookTemplate =
  PUBLISHABLE_GUESTBOOK_TEMPLATES.find((t) => t.key === DEFAULT_PARTY_GUESTBOOK_TEMPLATE)
  ?? PUBLISHABLE_GUESTBOOK_TEMPLATES[0];

/** The current design for a key the composer offers. */
export function publishableGuestbookTemplate(key: string): GuestbookTemplate {
  return PUBLISHABLE_GUESTBOOK_TEMPLATES.find((t) => t.key === key) ?? DEFAULT_GUESTBOOK_TEMPLATE;
}

/**
 * The design a PUBLISHED memory is drawn with: exactly the version it names.
 *
 * A pair this client does not know — a memory published by a newer server,
 * before this page was reloaded — is drawn with the closest design it does
 * know (the same key's current one, else the default) rather than not at all:
 * a memory in a slightly older frame is still the memory.
 *
 * PROVISIONAL while every design is at version 1, where the fallback can never
 * change a composition. Before the first `@2` ships, decide this explicitly:
 * a client that falls back to a DIFFERENT version of the same key draws a
 * memory differently from how its author saw it, which is the very thing the
 * stored version exists to prevent.
 */
export function guestbookTemplateFor(key: string, version: number): GuestbookTemplate {
  return BY_KEY_AND_VERSION.get(`${key}@${version}`)
    ?? PUBLISHABLE_GUESTBOOK_TEMPLATES.find((t) => t.key === key)
    ?? DEFAULT_GUESTBOOK_TEMPLATE;
}
