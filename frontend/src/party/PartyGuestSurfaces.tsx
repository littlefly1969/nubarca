import type { ReactNode } from 'react';
import type { PartyGuestContentKind, PartyGuestContext } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PartyContentImage, PartyGuestContentSections, partyThankYou } from './PartyGuestContent';

// The two surfaces the same QR shows outside the party itself.
//
// BEFORE is an invitation, not an empty album: a title, a date, what the host
// wants to say, and the album's own cover when there is one. There is no
// gallery, no contribution card and no capability deck — not because they are
// hidden, but because the server does not offer them and would refuse them.
//
// AFTER changes the tone rather than the layout. A thank-you, and the memories
// for as long as they last.

export function PartyBeforeHome({
  context, onOpenPoster, topBar,
}: {
  context: PartyGuestContext;
  onOpenPoster?(kind: PartyGuestContentKind): void;
  /** The brand row, drawn INSIDE the cover exactly as it is at the party. */
  topBar?: ReactNode;
}) {
  const { t, formatDate } = useI18n();
  return (
    <div className="party-invitation" data-testid="party-before">
      {/* THE SAME COVER AS THE PARTY, drawn the way the party draws its own —
          full-bleed, faded into the page, with the brand row and the headline
          inside it. The SERVER chose the picture: the host's invitation cover,
          else the album's chosen cover. The invitation's own photograph is not
          it, and stays in its section below. A decorative background layer, as
          the party's is: a picture that fails to load leaves the brand colour
          rather than a broken frame. */}
      <header className="party-guest-hub-hero party-invitation-hero">
        <div
          className="party-guest-hub-hero-cover"
          data-testid="party-invitation-hero"
          data-cover={context.coverUrl ? 'photo' : 'fallback'}
          style={context.coverUrl ? { backgroundImage: `url("${context.coverUrl}")` } : undefined}
          aria-hidden="true"
        />
        {topBar}
        {/* "You're invited", the name and the date come FIRST, on the picture,
            and what the host wrote follows below. */}
        <div className="party-guest-hub-headline">
          <p className="party-guest-hub-eyebrow">{t('partyGuest.invited')}</p>
          <h1 className="party-guest-hub-title">{context.title}</h1>
          {context.eventStartsAt && (
            <p className="party-guest-hub-meta party-invitation-date">
              <time dateTime={context.eventStartsAt}>
                {formatDate(context.eventStartsAt, {
                  dateStyle: 'long', timeStyle: 'short',
                })}
              </time>
            </p>
          )}
        </div>
      </header>

      <div className="party-invitation-body">
        {/* The invitation's own photograph is part of what it SAYS: it sits in
            its section above the words, or opens whole from its row, by the
            same rules as every other slot — never up in the cover. */}
        <PartyGuestContentSections slots={context.content} onOpenPoster={onOpenPoster} />

        <p className="party-invitation-footnote">{t('partyGuest.savePage')}</p>
      </div>
    </div>
  );
}

export function PartyAfterHome({
  context, onOpenMemories, onOpenPoster,
}: {
  context: PartyGuestContext;
  onOpenMemories(): void;
  onOpenPoster?(kind: PartyGuestContentKind): void;
}) {
  const { t, formatDate } = useI18n();
  const thankYou = partyThankYou(context.content);
  const libraryOnly = context.accessMode === 'library-only';

  return (
    <div className="party-after" data-testid="party-after" data-access={context.accessMode}>
      <header className="party-after-hero" data-align={thankYou.textAlign}>
        <PartyContentImage src={thankYou.mediaUrl} className="party-after-cover" />
        <h1 className="party-after-title">
          {/* The host's own words when they wrote them, and the product's when
              they did not — an After surface is never blank. */}
          {thankYou.headline ?? t('partyGuest.thankYouHeadline')}
        </h1>
        <p className="party-after-message">
          {thankYou.message ?? t('partyGuest.thankYouMessage')}
        </p>
        <p className="party-after-party">{context.title}</p>
      </header>

      {context.library.available ? (
        <div className="party-after-memories">
          <button
            type="button" className="party-after-cta" data-testid="party-memories-cta"
            onClick={onOpenMemories}
          >
            {t('partyGuest.memories')}
          </button>
          {context.library.accessEndsAt && (
            <p className="party-after-until">
              {t('partyGuest.memoriesUntil').replace(
                '{date}',
                formatDate(context.library.accessEndsAt, { dateStyle: 'long' }))}
            </p>
          )}
        </div>
      ) : (
        // Never a dead CTA. When the memories have closed the page says so
        // instead of offering a button that leads nowhere.
        <p className="party-after-until" data-testid="party-memories-closed">
          {t('partyGuest.memoriesClosed')}
        </p>
      )}

      {/* Library-only is a deliberate, minimal surface: the greeting and the
          photographs. Nothing the host wrote for the party is carried into it,
          because the visit is no longer a visit to the party. */}
      {!libraryOnly && (
        <PartyGuestContentSections slots={context.content} onOpenPoster={onOpenPoster} />
      )}
    </div>
  );
}

/**
 * The banner that appears when the party moves under a guest who is reading.
 *
 * The page is never torn away mid-sentence — polling notices the change and
 * OFFERS it. A reload lands on the new surface directly, which is why this is a
 * courtesy rather than the mechanism.
 */
export function PartyPhaseChangeBanner({
  phase, onEnter,
}: {
  phase: 'live' | 'after';
  onEnter(): void;
}) {
  const { t } = useI18n();
  return (
    <div className="party-phase-banner" role="status" data-testid={`party-moved-${phase}`}>
      <p>{phase === 'live' ? t('partyGuest.startedTitle') : t('partyGuest.endedTitle')}</p>
      <button type="button" onClick={onEnter} data-testid="party-phase-enter">
        {phase === 'live' ? t('partyGuest.startedCta') : t('partyGuest.endedCta')}
      </button>
    </div>
  );
}
