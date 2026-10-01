import { describe, expect, it } from 'vitest';
import { PARTY_GUESTBOOK_TEMPLATE_KEYS } from '@nubarca/contracts';
import itDict from '../i18n/it';
import {
  DEFAULT_GUESTBOOK_TEMPLATE,
  PUBLISHABLE_GUESTBOOK_TEMPLATES,
  guestbookTemplateFor,
  publishableGuestbookTemplate,
} from './partyGuestbookTemplates';

/**
 * The table that turns a memory's (template key, version) into a design. What
 * it defends: the composer offers exactly the four designs the server accepts,
 * in order, at the version the server stores; a published memory is drawn
 * with the version it NAMES; and a pair this client has never heard of still
 * draws something rather than nothing.
 */
describe('guest book templates', () => {
  it('offers the four designs the server accepts, each at version 1', () => {
    expect(PUBLISHABLE_GUESTBOOK_TEMPLATES.map((t) => `${t.key}@${t.version}`))
      .toEqual(['nubarca@1', 'polaroid@1', 'editorial@1', 'celebration@1']);
    expect(PUBLISHABLE_GUESTBOOK_TEMPLATES.map((t) => t.key)).toEqual([...PARTY_GUESTBOOK_TEMPLATE_KEYS]);
    expect(DEFAULT_GUESTBOOK_TEMPLATE.key).toBe('nubarca');
  });

  it('names every design in the canonical dictionary, and versions its stylesheet class', () => {
    for (const template of PUBLISHABLE_GUESTBOOK_TEMPLATES) {
      expect(itDict[template.labelKey]).toBeTruthy();
      expect(template.className).toBe(`guestbook-memory--${template.key}-${template.version}`);
    }
  });

  it('frames by the photograph’s shape, never by a screen', () => {
    const nubarca = publishableGuestbookTemplate('nubarca');
    expect(nubarca.frameAspect(1600 / 1200)).toBeCloseTo(4 / 3);
    expect(nubarca.frameAspect(1200 / 1600)).toBeCloseTo(4 / 5);
    expect(nubarca.frameAspect(1)).toBe(1);
    // The instant print is square, whatever the photograph.
    expect(publishableGuestbookTemplate('polaroid').frameAspect(16 / 9)).toBe(1);
  });

  it('draws a published memory with the version it names, or the closest one it knows', () => {
    expect(guestbookTemplateFor('editorial', 1).key).toBe('editorial');
    // A newer version than this client knows: the same design's current one.
    expect(guestbookTemplateFor('polaroid', 7)).toBe(publishableGuestbookTemplate('polaroid'));
    // A design this client has never heard of: the default, never nothing.
    expect(guestbookTemplateFor('canva', 1)).toBe(DEFAULT_GUESTBOOK_TEMPLATE);
  });
});
