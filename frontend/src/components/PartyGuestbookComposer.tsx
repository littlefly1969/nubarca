import { useEffect, useId, useRef, useState } from 'react';
import {
  ApiError,
  PARTY_GUESTBOOK_TEXT_LIMITS,
  isPartyGuestbookAuthorValid,
  isPartyGuestbookBodyValid,
  normalizePartyMessageText,
  normalizePartyMultilineText,
  partyGuestbookAuthorRemaining,
  partyGuestbookBodyRemaining,
  partyGuestbookRefusal,
  submitPartyGuestbookEntry,
  type PartyGuestbookPhoto,
  type PartyGuestbookSubmission,
} from '@nubarca/api-client';
import { effectiveZoom } from '@nubarca/contracts';
import { useI18n } from '../i18n';
import { DEFAULT_CROP_VIEW, type CropView } from '../pages/partyPrintGeometry';
import { PhotoCropFrame } from '../party/PhotoCropFrame';
import { PhotoFramingControls } from '../party/PhotoFramingControls';
import {
  DEFAULT_GUESTBOOK_TEMPLATE,
  PUBLISHABLE_GUESTBOOK_TEMPLATES,
  type GuestbookTemplate,
} from '../party/partyGuestbookTemplates';
import { PartyGuestbookMemoryCard, type GuestbookMemoryView } from './PartyGuestbookMemoryCard';
import { PartyGuestbookPhotoPicker } from './PartyGuestbookPhotoPicker';

// MAKING A MEMORY, in two steps and no more: choose a photograph, then make
// the memory — see it, pick its design, frame it if you like, write, sign,
// publish.
//
// THE DRAFT LIVES HERE until the server has it. Nothing is cleared before the
// publish is confirmed, and a failure clears only what it is about: a
// photograph that is no longer in the album sends the guest back to choose
// another with the words they wrote intact; a network failure keeps
// everything and offers to try again. The draft does not outlive the page —
// a memory half-written on someone else's phone is not something to keep.
//
// The composition is never designed by the guest. They choose a photograph,
// a design among four and, if they want, the framing; the layout is the
// template's.

type Step = 'photo' | 'compose' | 'sent';

type Failure =
  | { kind: 'retry'; message: string }
  | { kind: 'closed'; message: string }
  | { kind: 'rejected'; message: string };

/**
 * The memory as THIS page can draw it right now: the chosen photograph on the
 * chooser's own preview URL, and the words as the server will store them.
 * The composer's preview, and what the success screen shows for a memory that
 * is not public yet — whose entry route is, deliberately, not readable.
 */
function draftMemory(
  photo: PartyGuestbookPhoto, author: string, body: string, template: GuestbookTemplate, view: CropView,
): GuestbookMemoryView {
  return {
    // What the server will store, so the preview never promises a blank line
    // or a stray space the book will not keep.
    authorDisplayName: normalizePartyMessageText(author),
    body: normalizePartyMultilineText(body),
    template: { key: template.key, version: template.version },
    media: { url: photo.previewUrl, width: photo.width, height: photo.height, crop: view },
  };
}

/** The zoom a framing is drawn at in `template`'s frame for `photo`. */
function framedZoom(photo: PartyGuestbookPhoto, template: GuestbookTemplate, view: CropView): number {
  const aspect = photo.width > 0 && photo.height > 0 ? photo.width / photo.height : 1;
  return effectiveZoom(aspect, template.frameAspect(aspect), view.zoom);
}

/** A sensible first framing: centred, nothing enlarged, a tall picture's faces kept. */
function autoFraming(photo: PartyGuestbookPhoto): CropView {
  return { ...DEFAULT_CROP_VIEW, centerY: photo.orientation === 'portrait' ? 0.42 : 0.5 };
}

export function PartyGuestbookComposer({
  token, remaining, onPublished, onClose,
}: {
  token: string;
  /** Memories this guest has left, or null for no limit. */
  remaining: number | null;
  /** The server has the memory: the book behind this composer should reload. */
  onPublished(submission: PartyGuestbookSubmission): void;
  onClose(): void;
}) {
  const { t } = useI18n();
  const [step, setStep] = useState<Step>('photo');
  const [photo, setPhoto] = useState<PartyGuestbookPhoto | null>(null);
  const [view, setView] = useState<CropView>(DEFAULT_CROP_VIEW);
  const [template, setTemplate] = useState<GuestbookTemplate>(DEFAULT_GUESTBOOK_TEMPLATE);
  const [body, setBody] = useState('');
  const [author, setAuthor] = useState('');
  const [repositioning, setRepositioning] = useState(false);
  const [sending, setSending] = useState(false);
  const [failure, setFailure] = useState<Failure | null>(null);
  const [pickerNotice, setPickerNotice] = useState<string | null>(null);
  const [fieldError, setFieldError] = useState<{ author?: string; body?: string }>({});
  const [sent, setSent] = useState<PartyGuestbookSubmission | null>(null);
  // What the success screen draws, fixed at the moment of publishing.
  const [sentMemory, setSentMemory] = useState<GuestbookMemoryView | null>(null);
  const [left, setLeft] = useState<number | null>(remaining);
  const composeHeading = useRef<HTMLHeadingElement>(null);
  const sentHeading = useRef<HTMLHeadingElement>(null);
  const ids = useId();

  // Each step announces itself where focus lands, so a keyboard or screen
  // reader user is never left on a control that just disappeared.
  useEffect(() => {
    if (step === 'compose') composeHeading.current?.focus();
    if (step === 'sent') sentHeading.current?.focus();
    if (step === 'photo') document.getElementById('guestbook-picker-title')?.focus();
  }, [step]);

  const choose = (chosen: PartyGuestbookPhoto) => {
    if (chosen.id !== photo?.id) setView(autoFraming(chosen));
    setPhoto(chosen);
    setPickerNotice(null);
    setFailure(null);
    setRepositioning(false);
    setStep('compose');
  };

  const bodyRemaining = partyGuestbookBodyRemaining(body);
  const authorRemaining = partyGuestbookAuthorRemaining(author);
  const closed = failure?.kind === 'closed';
  const canPublish = photo !== null
    && isPartyGuestbookBodyValid(body)
    && isPartyGuestbookAuthorValid(author)
    && !sending
    && !closed;

  const publish = async () => {
    if (!canPublish || !photo) return;
    setSending(true);
    setFailure(null);
    setFieldError({});
    try {
      const result = await submitPartyGuestbookEntry(token, {
        sourceMediaItemId: photo.id,
        // Sent raw: the SERVER normalises and is the authority on what is
        // stored. Trimming here too would only create a second opinion.
        authorDisplayName: author,
        body,
        templateKey: template.key,
        // The zoom as drawn in THIS design's frame: a photograph zoomed out in
        // one design and moved to a design that frames it closer is sent as
        // that design's contain, never below it.
        crop: { centerX: view.centerX, centerY: view.centerY, zoom: framedZoom(photo, template, view) },
      });
      setSent(result);
      // A memory in the book is drawn as the book returns it. One waiting for
      // approval is NOT readable through its public route — pending content
      // stays private until a manager lets it in — so the guest is shown the
      // draft they just sent, on the photograph URL the chooser already gave
      // them.
      setSentMemory(result.status === 'visible' && result.entry
        ? result.entry
        : draftMemory(photo, author, body, template, view));
      setLeft(result.remaining ?? null);
      setStep('sent');
      onPublished(result);
    } catch (err: unknown) {
      handleRefusal(err);
    } finally {
      setSending(false);
    }
  };

  const handleRefusal = (err: unknown) => {
    const status = err instanceof ApiError ? err.status : 0;
    const code = err instanceof ApiError
      ? (err.body as { error?: unknown } | null)?.error
      : undefined;
    const refusal = status === 404
      ? 'closed'
      : status === 429 ? 'transient' : partyGuestbookRefusal(typeof code === 'string' ? code : null);

    if (refusal === 'photo') {
      // THE PHOTOGRAPH, and only the photograph, is gone. The words stay;
      // the guest chooses another.
      setPhoto(null);
      setView(DEFAULT_CROP_VIEW);
      setRepositioning(false);
      setPickerNotice(t('partyGuestbookComposer.photoUnavailable'));
      setStep('photo');
    } else if (refusal === 'field') {
      if (code === 'guestbook_invalid_author') {
        setFieldError({ author: t('partyGuestbookComposer.nameInvalid') });
      } else if (code === 'guestbook_invalid_body') {
        setFieldError({ body: t('partyGuestbookComposer.bodyInvalid') });
      } else {
        if (code === 'guestbook_invalid_crop') setView(autoFraming(photo!));
        setFailure({ kind: 'rejected', message: t('partyGuestbookComposer.rejected') });
      }
    } else if (refusal === 'closed') {
      setFailure({
        kind: 'closed',
        message: code === 'guestbook_limit_reached'
          ? t('partyGuestbookComposer.limitReached')
          : t('partyGuestbookComposer.disabled'),
      });
    } else {
      setFailure({
        kind: 'retry',
        message: status === 429 ? t('partyGuestbookComposer.tooMany') : t('partyGuestbookComposer.failed'),
      });
    }
  };

  const startAnother = () => {
    // The same guest, so the same signature; everything else is a new memory.
    setPhoto(null);
    setView(DEFAULT_CROP_VIEW);
    setTemplate(DEFAULT_GUESTBOOK_TEMPLATE);
    setBody('');
    setSent(null);
    setSentMemory(null);
    setFailure(null);
    setStep('photo');
  };

  if (step === 'photo') {
    return (
      <PartyGuestbookPhotoPicker
        token={token}
        selectedId={photo?.id ?? null}
        notice={pickerNotice}
        onChoose={choose}
        onCancel={photo ? () => setStep('compose') : onClose}
      />
    );
  }

  if (step === 'sent' && sent) {
    const pending = sent.status === 'pending';
    return (
      <section className="guestbook-sent" data-testid="guestbook-sent" aria-labelledby={`${ids}-sent`}>
        <h2 id={`${ids}-sent`} ref={sentHeading} tabIndex={-1} className="guestbook-step-title">
          {pending ? t('partyGuestbookComposer.sentPendingTitle') : t('partyGuestbookComposer.sentTitle')}
        </h2>
        {pending && <p className="guestbook-sent-help" role="status">{t('partyGuestbookComposer.sentPending')}</p>}
        {sentMemory && (
          <PartyGuestbookMemoryCard memory={sentMemory} loading="eager" testId="guestbook-sent-memory" />
        )}
        <div className="guestbook-sent-actions">
          <button
            type="button"
            className="party-contribution-primary"
            onClick={onClose}
            data-testid="guestbook-sent-done"
          >
            {t('partyGuestbookComposer.done')}
          </button>
          {left !== 0 && (
            <button
              type="button"
              className="party-contribution-secondary"
              onClick={startAnother}
              data-testid="guestbook-sent-another"
            >
              {t('partyGuestbookComposer.another')}
            </button>
          )}
        </div>
      </section>
    );
  }

  if (!photo) return null;

  const photoAspect = photo.width > 0 && photo.height > 0 ? photo.width / photo.height : 1;
  const preview = draftMemory(photo, author, body, template, view);
  const bodyErrorId = `${ids}-body-error`;
  const authorErrorId = `${ids}-author-error`;
  const bodyCounterId = `${ids}-body-counter`;
  const bodyMessage = fieldError.body
    ?? (bodyRemaining < 0
      ? t('partyGuestbookPublic.overLimit', { max: String(PARTY_GUESTBOOK_TEXT_LIMITS.body) })
      : null);
  const authorMessage = fieldError.author
    ?? (authorRemaining < 0
      ? t('partyGuestbookPublic.nameOverLimit', { max: String(PARTY_GUESTBOOK_TEXT_LIMITS.authorDisplayName) })
      : null);

  return (
    <form
      className={repositioning ? 'guestbook-compose is-repositioning' : 'guestbook-compose'}
      data-testid="guestbook-compose"
      aria-labelledby={`${ids}-compose`}
      onSubmit={(e) => { e.preventDefault(); void publish(); }}
    >
      <div className="guestbook-step-head">
        <button
          type="button"
          className="guestbook-icon-button"
          onClick={() => setStep('photo')}
          aria-label={t('partyGuestbookComposer.changePhoto')}
          data-testid="guestbook-compose-back"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 5l-7 7 7 7" /></svg>
        </button>
        <h2 id={`${ids}-compose`} ref={composeHeading} tabIndex={-1} className="guestbook-step-title">
          {t('partyGuestbookComposer.compose')}
        </h2>
      </div>

      {/* 1. THE MEMORY, as it will read — or, while framing, the photograph
             in the very frame the design gives it. */}
      <div className="guestbook-compose-preview" data-testid="guestbook-preview" aria-live="off">
        {repositioning ? (
          <div className="guestbook-reposition" data-testid="guestbook-reposition">
            <PhotoCropFrame
              src={photo.previewUrl}
              aspect={photoAspect}
              slotAspect={template.frameAspect(photoAspect)}
              view={view}
              onChange={setView}
              label={t('partyGuestbookComposer.repositionHelp')}
              testId="guestbook-crop"
              background={template.photoWell}
            />
            <p className="guestbook-hint">{t('partyGuestbookComposer.repositionHelp')}</p>
            <PhotoFramingControls
              aspect={photoAspect}
              slotAspect={template.frameAspect(photoAspect)}
              view={view}
              onChange={setView}
              zoomLabel={t('partyGuestbookComposer.zoom')}
              testId="guestbook"
            />
            <button
              type="button"
              className="party-contribution-secondary"
              onClick={() => setRepositioning(false)}
              data-testid="guestbook-reposition-done"
            >
              {t('partyGuestbookComposer.repositionDone')}
            </button>
          </div>
        ) : (
          <PartyGuestbookMemoryCard
            memory={preview}
            bodyPlaceholder={t('partyGuestbookComposer.previewBody')}
            authorPlaceholder={t('partyGuestbookComposer.previewName')}
            loading="eager"
            testId="guestbook-preview-memory"
          />
        )}
      </div>

      {/* 2. THE DESIGN, one of four, changing the preview at once. */}
      <fieldset className="guestbook-templates" data-testid="guestbook-templates">
        <legend className="guestbook-field-label">{t('partyGuestbookComposer.template')}</legend>
        <div className="guestbook-templates-row">
          {PUBLISHABLE_GUESTBOOK_TEMPLATES.map((option) => (
            <label
              key={option.key}
              className={option.key === template.key ? 'guestbook-template is-selected' : 'guestbook-template'}
              data-template={option.key}
            >
              <input
                type="radio"
                name={`${ids}-template`}
                value={option.key}
                checked={option.key === template.key}
                onChange={() => setTemplate(option)}
                data-testid={`guestbook-template-${option.key}`}
              />
              <span className="guestbook-template-swatch" aria-hidden="true" />
              <span className="guestbook-template-name">{t(option.labelKey)}</span>
            </label>
          ))}
        </div>
      </fieldset>

      {/* 3. FRAMING, secondary: the automatic framing is already sensible. */}
      <div className="guestbook-compose-tools">
        {!repositioning && (
          <button
            type="button"
            className="party-contribution-secondary guestbook-tool"
            onClick={() => setRepositioning(true)}
            data-testid="guestbook-reposition-open"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3" />
            </svg>
            {t('partyGuestbookComposer.reposition')}
          </button>
        )}
        <button
          type="button"
          className="party-contribution-secondary guestbook-tool"
          onClick={() => setStep('photo')}
          data-testid="guestbook-change-photo"
        >
          {t('partyGuestbookComposer.changePhoto')}
        </button>
      </div>

      {/* 4. THE DEDICATION, with its paragraphs. */}
      <label className="party-dedication-field party-dedication-field--text">
        <span className="party-dedication-label">{t('partyGuestbookComposer.bodyLabel')}</span>
        <textarea
          value={body}
          onChange={(e) => { setBody(e.target.value); setFieldError((f) => ({ ...f, body: undefined })); }}
          placeholder={t('partyGuestbookComposer.bodyPlaceholder')}
          rows={5}
          required
          aria-invalid={bodyMessage ? true : undefined}
          aria-describedby={bodyMessage ? `${bodyCounterId} ${bodyErrorId}` : bodyCounterId}
          data-testid="guestbook-body"
        />
      </label>
      <p
        id={bodyCounterId}
        className={bodyRemaining < 0 ? 'party-dedication-counter over' : 'party-dedication-counter'}
        aria-live="polite"
        data-testid="guestbook-counter"
      >
        {t('partyGuestbookPublic.remaining', { count: String(Math.max(0, bodyRemaining)) })}
      </p>
      {bodyMessage && (
        <p id={bodyErrorId} className="party-dedication-error" role="alert">{bodyMessage}</p>
      )}

      {/* 5. THE SIGNATURE. Required: a memory is signed. */}
      <label className="party-dedication-field">
        <span className="party-dedication-label">{t('partyGuestbookComposer.nameLabel')}</span>
        <input
          type="text"
          value={author}
          onChange={(e) => { setAuthor(e.target.value); setFieldError((f) => ({ ...f, author: undefined })); }}
          placeholder={t('partyGuestbookComposer.namePlaceholder')}
          // No maxLength: the browser counts UTF-16 units and would cut an
          // emoji in half at the boundary. The server counts code points.
          autoComplete="name"
          required
          aria-invalid={authorMessage ? true : undefined}
          aria-describedby={authorMessage ? authorErrorId : undefined}
          data-testid="guestbook-name"
        />
      </label>
      {authorMessage && (
        <p id={authorErrorId} className="party-dedication-error" role="alert">{authorMessage}</p>
      )}

      {/* 6. PUBLISH, within a thumb's reach. Sticky in the flow rather than
             fixed, so an open keyboard never hides it for good. */}
      <div className="guestbook-compose-actions">
        {failure && (
          <p
            className={failure.kind === 'retry' ? 'guestbook-notice' : 'party-dedication-error'}
            role="alert"
            data-testid={`guestbook-failure-${failure.kind}`}
          >
            {failure.message}
          </p>
        )}
        {!failure && !canPublish && !sending && (
          <p className="guestbook-hint" data-testid="guestbook-incomplete">
            {t('partyGuestbookComposer.incomplete')}
          </p>
        )}
        {left !== null && !closed && (
          <p className="guestbook-hint" data-testid="guestbook-remaining">
            {t('partyGuestbookPublic.entriesLeft', { count: String(left) })}
          </p>
        )}
        <button
          type="submit"
          className="party-contribution-primary"
          disabled={!canPublish}
          data-testid="guestbook-publish"
        >
          {sending
            ? t('partyGuestbookComposer.publishing')
            : failure?.kind === 'retry'
              ? t('partyGuestbookComposer.retry')
              : t('partyGuestbookComposer.publish')}
        </button>
      </div>
    </form>
  );
}
