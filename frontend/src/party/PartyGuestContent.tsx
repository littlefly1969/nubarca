import type { PartyGuestContentSlot } from '@nubarca/api-client';
import { useI18n } from '../i18n';

// The six typed slots, rendered as TEXT.
//
// There is no HTML here and no Markdown engine, because there is none in the
// data either: each shape is a handful of named strings the server validated,
// so "a party cannot inject anything into its own page" is a property of the
// model rather than a promise about this file.
//
// The ORDER is the server's — it sends the slots in product order — and this
// renders them in the order it receives. An absent optional field renders
// nothing rather than an empty line: the server nulls what trims to nothing.

type Payload = Record<string, unknown>;

const str = (payload: Payload, key: string): string | null => {
  const value = payload[key];
  return typeof value === 'string' && value.trim() !== '' ? value : null;
};

export function PartyGuestContentSections({ slots }: { slots: readonly PartyGuestContentSlot[] }) {
  if (slots.length === 0) return null;
  return (
    <div className="party-content" data-testid="party-content">
      {slots.map((slot) => (
        <PartyGuestContentSection key={slot.kind} slot={slot} />
      ))}
    </div>
  );
}

function PartyGuestContentSection({ slot }: { slot: PartyGuestContentSlot }) {
  const { t } = useI18n();
  const payload = slot.content ?? {};

  switch (slot.kind) {
    case 'invitation': {
      const headline = str(payload, 'headline');
      const message = str(payload, 'message');
      if (!headline && !message) return null;
      return (
        <section className="party-content-block" data-content="invitation">
          {headline && <h2 className="party-content-headline">{headline}</h2>}
          {message && <p className="party-content-body">{message}</p>}
        </section>
      );
    }

    case 'location': {
      const venue = str(payload, 'venueName');
      const address = str(payload, 'address');
      const note = str(payload, 'note');
      if (!venue && !address) return null;
      // The maps link is BUILT from the address rather than stored: an
      // arbitrary external URL kept as authority would be somebody else's page
      // one QR code away.
      const maps = address
        ? `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(
          [venue, address].filter(Boolean).join(' '))}`
        : null;
      return (
        <section className="party-content-block" data-content="location">
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
      if (!headline) return null;
      return (
        <section className="party-content-block" data-content="dress-code">
          <h3>{t('partyGuest.dressCode')}</h3>
          <p className="party-content-strong">{headline}</p>
          {description && <p className="party-content-body">{description}</p>}
        </section>
      );
    }

    case 'menu': {
      const intro = str(payload, 'intro');
      const sections = Array.isArray(payload.sections) ? payload.sections : [];
      if (!intro && sections.length === 0) return null;
      return (
        <section className="party-content-block" data-content="menu">
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
        </section>
      );
    }

    case 'info': {
      const title = str(payload, 'title');
      const body = str(payload, 'body');
      if (!title && !body) return null;
      return (
        <section className="party-content-block" data-content="info">
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

/** The thank-you, or the product's own words when the host wrote none. */
export function partyThankYou(
  slots: readonly PartyGuestContentSlot[],
): { headline: string | null; message: string | null } {
  const slot = slots.find((s) => s.kind === 'thank-you');
  const payload = (slot?.content ?? {}) as Payload;
  return { headline: str(payload, 'headline'), message: str(payload, 'message') };
}
