import { useState } from 'react';
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
  context, onOpenPoster,
}: {
  context: PartyGuestContext;
  onOpenPoster?(kind: PartyGuestContentKind): void;
}) {
  const { t, formatDate } = useI18n();
  // The hero is the invitation's photograph only while it is INLINE — which is
  // exactly what the server decided when it built `coverUrl`, so the two cannot
  // disagree about whether that picture is already on screen. A poster
  // invitation is not up there, so its row below must not be suppressed.
  const invitation = context.content.find((s) => s.kind === 'invitation');
  const invitationIsHero = invitation?.mediaPresentation !== 'poster';
  return (
    <div className="party-invitation" data-testid="party-before">
      <header className="party-invitation-hero">
        <InvitationHero src={context.coverUrl} />
        <p className="party-invitation-eyebrow">{t('partyGuest.invited')}</p>
        <h1 className="party-invitation-title">{context.title}</h1>
        {context.eventStartsAt && (
          <p className="party-invitation-date">
            <time dateTime={context.eventStartsAt}>
              {formatDate(context.eventStartsAt, {
                dateStyle: 'long', timeStyle: 'short',
              })}
            </time>
          </p>
        )}
      </header>

      {/* An INLINE invitation photograph IS the hero above, so its section does
          not draw it a second time. A poster one is not the hero and keeps its
          own row, which is how a full-height invitation gets opened whole. */}
      <PartyGuestContentSections
        slots={context.content}
        heroKind={invitationIsHero ? 'invitation' : undefined}
        onOpenPoster={onOpenPoster}
      />

      <p className="party-invitation-footnote">{t('partyGuest.savePage')}</p>
    </div>
  );
}

/**
 * The invitation's hero. The SERVER has already chosen it — the invitation's
 * own photograph, else the album's chosen cover — and with neither, or with a
 * picture that fails to load, it is a composition rather than a broken frame:
 * an invitation with a hole in it is worse than one without a photograph.
 */
function InvitationHero({ src }: { src: string | null }) {
  const [failed, setFailed] = useState<string | null>(null);
  return src && failed !== src ? (
    <img
      className="party-invitation-cover" src={src} alt=""
      data-testid="party-invitation-hero" onError={() => setFailed(src)}
    />
  ) : (
    <div className="party-invitation-cover party-invitation-cover--blank" aria-hidden="true" />
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
      <header className="party-after-hero">
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
