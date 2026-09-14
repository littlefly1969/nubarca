import { useState, type ReactNode } from 'react';
import type {
  PartyGuestContentKind, PartyGuestContentView, PartyMediaCrop, PartyMediaOrientation,
  PartyTextAlign,
} from '@nubarca/api-client';
import { cropFor, DEFAULT_CROP_VIEW } from '../pages/partyPrintGeometry';
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
 * Where a slot's words sit until the host chooses: at the left edge in a
 * section, centred in the thank-you, which is the After page's hero. The one
 * place both the editor and the guest surface ask, so what the editor shows
 * selected is exactly what the guest sees.
 */
export function defaultTextAlign(kind: PartyGuestContentKind): PartyTextAlign {
  return kind === 'thank-you' ? 'center' : 'left';
}

/**
 * The two fixed frames a section photograph may be placed in, as width / height:
 * portrait 4:5 — a phone-shaped picture that does not tower over the words —
 * and landscape 3:2, the band every section used before the choice existed.
 */
export const SLOT_FRAME_ASPECT: Record<PartyMediaOrientation, number> = {
  portrait: 4 / 5,
  landscape: 3 / 2,
};

/**
 * A slot's one photograph, or nothing at all.
 *
 * The server offers an address only for a file it will serve, but a file can
 * go to Trash between the page loading and the picture arriving — so a failed
 * load removes the frame instead of leaving a broken-image icon where it was.
 */
export function PartyContentImage({
  src, className = 'party-content-media', frame, overlay,
}: {
  src: string | null;
  className?: string;
  /** The host's frame. No orientation is the whole photograph. */
  frame?: { mediaOrientation?: PartyMediaOrientation | null; mediaCrop?: PartyMediaCrop | null };
  /**
   * Words to lay across the lower part of the picture, the way the cover carries
   * the party's name. A picture that is not there, or fails to load, leaves them
   * in the page as ordinary text rather than floating over nothing.
   */
  overlay?: ReactNode;
}) {
  const [failed, setFailed] = useState<string | null>(null);
  // The photograph's own shape, learned when it loads: a fixed frame needs it to
  // place the crop exactly where the host put it.
  const [aspect, setAspect] = useState<number | null>(null);
  if (!src || failed === src) return overlay ? <>{overlay}</> : null;

  const words = overlay
    ? <div className="party-content-overlay-text">{overlay}</div>
    : null;

  const orientation = frame?.mediaOrientation ?? null;
  if (!orientation) {
    // The WHOLE photograph at its own proportions — the default, so a portrait
    // picture is never cut into a landscape band nobody asked for.
    const picture = (
      <img
        className={className} src={src} alt="" loading="lazy" decoding="async"
        data-testid="party-content-media" data-frame="whole"
        onError={() => setFailed(src)}
      />
    );
    return words ? <div className="party-content-overlay">{picture}{words}</div> : picture;
  }

  const slotAspect = SLOT_FRAME_ASPECT[orientation];
  const crop = aspect ? cropFor(aspect, slotAspect, frame?.mediaCrop ?? DEFAULT_CROP_VIEW) : null;
  return (
    <div
      className={className} data-frame="fixed" data-orientation={orientation}
      style={{ aspectRatio: `${slotAspect}` }}
    >
      <img
        className="party-content-frame-img" src={src} alt="" loading="lazy" decoding="async"
        data-testid="party-content-media"
        onLoad={(event) => {
          const { naturalWidth, naturalHeight } = event.currentTarget;
          if (naturalWidth > 0 && naturalHeight > 0) setAspect(naturalWidth / naturalHeight);
        }}
        onError={() => setFailed(src)}
        // Until its shape is known the frame is simply filled; the host's exact
        // crop — the party print's own maths — follows the moment it arrives.
        style={crop ? {
          width: `${100 / crop.cropWidth}%`,
          height: `${100 / crop.cropHeight}%`,
          left: `${(-crop.cropX * 100) / crop.cropWidth}%`,
          top: `${(-crop.cropY * 100) / crop.cropHeight}%`,
        } : { width: '100%', height: '100%', left: 0, top: 0 }}
      />
      {words}
    </div>
  );
}

/**
 * A section's photograph and its words, in the order the host chose: the words
 * below the picture — the default — or on it, across its lower part. Without a
 * picture the words simply follow, whatever was chosen.
 */
function SlotPhoto({
  slot, mediaUrl, words,
}: {
  slot: PartyGuestContentView;
  mediaUrl: string | null;
  words: ReactNode;
}) {
  const onPhoto = slot.textPlacement === 'overlay' && !!mediaUrl && !!words;
  return onPhoto
    ? <PartyContentImage src={mediaUrl} frame={slot} overlay={words} />
    : <><PartyContentImage src={mediaUrl} frame={slot} />{words}</>;
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

/**
 * Is there anything for this surface to actually SHOW?
 *
 * Not `slots.length > 0`. A poster whose photograph stopped being servable
 * renders nothing at all — correctly, since offering a dead row would be worse
 * — so counting slots makes the dock offer an "Info" button that scrolls to an
 * empty container. The question the dock is really asking is whether any slot
 * would draw something, and that is this.
 *
 * An inline slot is presentable on its words alone (its photograph is optional
 * and was always allowed to be absent); a poster slot is presentable only while
 * it can still be opened.
 */
export function hasPresentableContent(
  slots: readonly PartyGuestContentView[],
): boolean {
  return slots.some((slot) => (slot.mediaPresentation === 'poster'
    ? isOpenablePoster(slot)
    : true));
}

export function PartyGuestContentSections({
  slots, onOpenPoster,
}: {
  slots: readonly PartyGuestContentView[];
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
          mediaUrl={slot.mediaUrl ?? null}
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
  const align = slot.textAlign ?? defaultTextAlign(slot.kind);

  switch (slot.kind) {
    case 'invitation': {
      const headline = str(payload, 'headline');
      const message = str(payload, 'message');
      if (!headline && !message && !mediaUrl) return null;
      const words = headline || message ? (
        <>
          {headline && <h2 className="party-content-headline">{headline}</h2>}
          {message && <p className="party-content-body">{message}</p>}
        </>
      ) : null;
      return (
        <section className="party-content-block" data-content="invitation" data-align={align}>
          <SlotPhoto slot={slot} mediaUrl={mediaUrl} words={words} />
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
      const words = venue || address ? (
        <>
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
        </>
      ) : null;
      // The section's TITLE comes first, above its photograph: it is what says a
      // new part of the page begins.
      return (
        <section className="party-content-block" data-content="location" data-align={align}>
          <h3>{t('partyGuest.location')}</h3>
          <SlotPhoto slot={slot} mediaUrl={mediaUrl} words={words} />
        </section>
      );
    }

    case 'dress-code': {
      const headline = str(payload, 'headline');
      const description = str(payload, 'description');
      if (!headline && !mediaUrl) return null;
      const words = headline || description ? (
        <>
          {headline && <p className="party-content-strong">{headline}</p>}
          {description && <p className="party-content-body">{description}</p>}
        </>
      ) : null;
      return (
        <section className="party-content-block" data-content="dress-code" data-align={align}>
          <h3>{t('partyGuest.dressCode')}</h3>
          <SlotPhoto slot={slot} mediaUrl={mediaUrl} words={words} />
        </section>
      );
    }

    case 'menu': {
      const intro = str(payload, 'intro');
      const sections = Array.isArray(payload.sections) ? payload.sections : [];
      if (!intro && sections.length === 0 && !mediaUrl) return null;
      // A card rather than a list: the photograph is its lid when there is one,
      // and the courses read as a menu either way. The title sits above the
      // card, like every section's.
      return (
        <section className="party-content-block" data-content="menu" data-align={align}>
          <h3>{t('partyGuest.menu')}</h3>
          <div className="party-menu-card">
            <PartyContentImage src={mediaUrl} frame={slot} className="party-content-media party-content-media--menu" />
            <div className="party-menu-body">
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
          </div>
        </section>
      );
    }

    case 'info': {
      const title = str(payload, 'title');
      const body = str(payload, 'body');
      if (!title && !body && !mediaUrl) return null;
      return (
        <section className="party-content-block" data-content="info" data-align={align}>
          {title && <h3>{title}</h3>}
          <SlotPhoto
            slot={slot} mediaUrl={mediaUrl}
            words={body ? <p className="party-content-body">{body}</p> : null}
          />
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
): {
  headline: string | null; message: string | null; mediaUrl: string | null;
  textAlign: PartyTextAlign;
  mediaOrientation: PartyMediaOrientation | null; mediaCrop: PartyMediaCrop | null;
  textPlacement: 'overlay' | null;
} {
  const slot = slots.find((s) => s.kind === 'thank-you');
  // The host's alignment governs the hero's words even when they are the
  // product's own greeting: it is a choice about the place, not the sentence.
  const textAlign = slot?.textAlign ?? defaultTextAlign('thank-you');
  if (slot && slot.mediaPresentation === 'poster') {
    return {
      headline: null, message: null, mediaUrl: null, textAlign,
      mediaOrientation: null, mediaCrop: null, textPlacement: null,
    };
  }
  const payload = (slot?.content ?? {}) as Payload;
  return {
    headline: str(payload, 'headline'),
    message: str(payload, 'message'),
    mediaUrl: slot?.mediaUrl ?? null,
    textAlign,
    mediaOrientation: slot?.mediaOrientation ?? null,
    mediaCrop: slot?.mediaCrop ?? null,
    textPlacement: slot?.textPlacement === 'overlay' ? 'overlay' : null,
  };
}
