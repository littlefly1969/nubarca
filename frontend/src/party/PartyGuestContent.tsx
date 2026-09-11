import { useState } from 'react';
import type { PartyGuestContentKind, PartyGuestContentView } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// The six typed slots, rendered as TEXT — and, where the host chose one, ONE
// photograph each.
//
// There is no HTML here and no Markdown engine, because there is none in the
// data either: each shape is a handful of named strings the server validated,
// so "a party cannot inject anything into its own page" is a property of the
// model rather than a promise about this file.
//
// The photograph is an ADDRESS the server built on this guest's own token, and
// the server sends one only for a file it will serve. It is shown and nothing
// else: no link to it, no download, no zoom — a picture on the menu is not a way
// into the owner's library.
//
// The ORDER is the server's — it sends the slots in product order — and this
// renders them in the order it receives. An absent optional field renders
// nothing rather than an empty line: the server nulls what trims to nothing.

type Payload = Record<string, unknown>;

const str = (payload: Payload, key: string): string | null => {
  const value = payload[key];
  return typeof value === 'string' && value.trim() !== '' ? value : null;
};

/**
 * A slot's one photograph, or nothing at all.
 *
 * The server offers an address only for a file it will serve, but a file can
 * go to Trash between the page loading and the picture arriving — so a failed
 * load removes the frame instead of leaving a broken-image icon where it was.
 */
export function PartyContentImage({
  src, className = 'party-content-media',
}: {
  src: string | null;
  className?: string;
}) {
  const [failed, setFailed] = useState<string | null>(null);
  if (!src || failed === src) return null;
  return (
    <img
      className={className} src={src} alt="" loading="lazy" decoding="async"
      data-testid="party-content-media"
      onError={() => setFailed(src)}
    />
  );
}

/**
 * Is this slot presented as a poster the guest can open?
 *
 * Both halves matter. `poster` is the host's CHOICE and is never rewritten by
 * the client — but a poster whose photograph stopped being servable has nothing
 * to present, so it is not offered. It does not silently become `inline`
 * either: that would publish words the host chose to replace with a picture.
 */
export function isOpenablePoster(slot: PartyGuestContentView): boolean {
  return slot.mediaPresentation === 'poster' && !!slot.mediaUrl;
}

export function PartyGuestContentSections({
  slots, heroKind, onOpenPoster,
}: {
  slots: readonly PartyGuestContentView[];
  /**
   * The kind whose photograph this surface already shows as its hero — the
   * invitation's, before the party — so it is not drawn a second time below.
   * Only ever an INLINE slot: a poster is never a hero.
   */
  heroKind?: PartyGuestContentKind;
  /** Opens a poster full-screen. Absent on a surface that offers no viewer. */
  onOpenPoster?(kind: PartyGuestContentKind): void;
}) {
  if (slots.length === 0) return null;
  return (
    <div className="party-content" data-testid="party-content">
      {slots.map((slot) => (slot.mediaPresentation === 'poster' ? (
        <PartyPosterRow key={slot.kind} slot={slot} onOpen={onOpenPoster} />
      ) : (
        <PartyGuestContentSection
          key={slot.kind}
          slot={slot}
          mediaUrl={slot.kind === heroKind ? null : slot.mediaUrl ?? null}
        />
      )))}
    </div>
  );
}

/**
 * A poster slot, as a navigation affordance.
 *
 * The label is the KIND's, localized by the product — there is no
 * `posterTitle`, no `customCta` and no `buttonText`, because a party that could
 * name its own buttons is a page builder with extra steps. For `info` that
 * means the row says "Informazioni" even when the payload carries a title of
 * its own: one configuration fewer, and a surface whose rows all read alike.
 *
 * The slot's typed text is NOT rendered here. In poster mode the photograph is
 * what the host chose to say; the words stay in `ContentJson` and come back
 * untouched the moment they switch back.
 */
function PartyPosterRow({
  slot, onOpen,
}: {
  slot: PartyGuestContentView;
  onOpen?(kind: PartyGuestContentKind): void;
}) {
  const { t } = useI18n();
  // Never a dead CTA: a poster with no servable photograph is simply absent.
  if (!isOpenablePoster(slot) || !onOpen) return null;
  return (
    <section className="party-content-block party-content-block--poster" data-content={slot.kind}>
      <button
        type="button"
        className="party-poster-row"
        data-testid={`party-poster-open-${slot.kind}`}
        onClick={() => onOpen(slot.kind)}
      >
        <span className="party-poster-row-label">{t(posterLabelKey(slot.kind))}</span>
        <span className="party-poster-row-chevron" aria-hidden="true">›</span>
      </button>
    </section>
  );
}

/** The product's own name for a kind, which is the only label a poster row has. */
export function posterLabelKey(kind: PartyGuestContentKind) {
  return `partyGuest.poster.${kind}` as 'partyGuest.poster.invitation';
}

function PartyGuestContentSection({
  slot, mediaUrl,
}: {
  slot: PartyGuestContentView;
  mediaUrl: string | null;
}) {
  const { t } = useI18n();
  const payload = slot.content ?? {};

  switch (slot.kind) {
    case 'invitation': {
      const headline = str(payload, 'headline');
      const message = str(payload, 'message');
      if (!headline && !message && !mediaUrl) return null;
      return (
        <section className="party-content-block" data-content="invitation">
          <PartyContentImage src={mediaUrl} />
          {headline && <h2 className="party-content-headline">{headline}</h2>}
          {message && <p className="party-content-body">{message}</p>}
        </section>
      );
    }

    case 'location': {
      const venue = str(payload, 'venueName');
      const address = str(payload, 'address');
      const note = str(payload, 'note');
      if (!venue && !address && !mediaUrl) return null;
      // The maps link is BUILT from the address rather than stored: an
      // arbitrary external URL kept as authority would be somebody else's page
      // one QR code away.
      const maps = address
        ? `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(
          [venue, address].filter(Boolean).join(' '))}`
        : null;
      return (
        <section className="party-content-block" data-content="location">
          <PartyContentImage src={mediaUrl} />
          <h3>{t('partyGuest.location')}</h3>
          {venue && <p className="party-content-strong">{venue}</p>}
          {address && <p className="party-content-body">{address}</p>}
          {note && <p className="party-content-note">{note}</p>}
          {maps && (
            <p>
              <a href={maps} target="_blank" rel="noopener noreferrer">
                {t('partyGuest.openMaps')}
              </a>
            </p>
          )}
        </section>
      );
    }

    case 'dress-code': {
      const headline = str(payload, 'headline');
      const description = str(payload, 'description');
      if (!headline && !mediaUrl) return null;
      return (
        <section className="party-content-block" data-content="dress-code">
          <PartyContentImage src={mediaUrl} />
          <h3>{t('partyGuest.dressCode')}</h3>
          {headline && <p className="party-content-strong">{headline}</p>}
          {description && <p className="party-content-body">{description}</p>}
        </section>
      );
    }

    case 'menu': {
      const intro = str(payload, 'intro');
      const sections = Array.isArray(payload.sections) ? payload.sections : [];
      if (!intro && sections.length === 0 && !mediaUrl) return null;
      // A card rather than a list: the photograph is its lid when there is one,
      // and the courses read as a menu either way.
      return (
        <section className="party-content-block party-content-block--menu" data-content="menu">
          <PartyContentImage src={mediaUrl} className="party-content-media party-content-media--menu" />
          <div className="party-menu-body">
            <h3>{t('partyGuest.menu')}</h3>
            {intro && <p className="party-content-body">{intro}</p>}
            {sections.map((raw, index) => {
              const section = (raw ?? {}) as Payload;
              const title = str(section, 'title');
              const items = Array.isArray(section.items) ? section.items : [];
              if (!title && items.length === 0) return null;
              return (
                <div className="party-menu-section" key={`${title ?? ''}-${index}`}>
                  {title && <p className="party-content-strong">{title}</p>}
                  {items.length > 0 && (
                    <ul>
                      {items.map((item, i) => (
                        <li key={`${String(item)}-${i}`}>{String(item)}</li>
                      ))}
                    </ul>
                  )}
                </div>
              );
            })}
          </div>
        </section>
      );
    }

    case 'info': {
      const title = str(payload, 'title');
      const body = str(payload, 'body');
      if (!title && !body && !mediaUrl) return null;
      return (
        <section className="party-content-block" data-content="info">
          <PartyContentImage src={mediaUrl} />
          {title && <h3>{title}</h3>}
          {body && <p className="party-content-body">{body}</p>}
        </section>
      );
    }

    default: {
      // thank-you: the After hero renders it, so it is not a section of its own.
      return null;
    }
  }
}

/**
 * The thank-you, or the product's own words when the host wrote none.
 *
 * A POSTER thank-you contributes neither. Its photograph is a document to be
 * read whole, not a band across the top of the After page, and its words are
 * what the host replaced with that picture — so the hero falls back to the
 * product's greeting and the poster is offered separately, below. The stored
 * text is untouched and returns the moment the host switches back.
 */
export function partyThankYou(
  slots: readonly PartyGuestContentView[],
): { headline: string | null; message: string | null; mediaUrl: string | null } {
  const slot = slots.find((s) => s.kind === 'thank-you');
  if (slot && slot.mediaPresentation === 'poster') {
    return { headline: null, message: null, mediaUrl: null };
  }
  const payload = (slot?.content ?? {}) as Payload;
  return {
    headline: str(payload, 'headline'),
    message: str(payload, 'message'),
    mediaUrl: slot?.mediaUrl ?? null,
  };
}
