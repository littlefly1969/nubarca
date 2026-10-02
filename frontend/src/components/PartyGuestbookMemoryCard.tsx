import type { CSSProperties, Ref } from 'react';
import { useI18n } from '../i18n';
import { placePhoto } from '@nubarca/contracts';
import { photoPlacementStyle } from '../party/PhotoCropFrame';
import { guestbookTemplateFor } from '../party/partyGuestbookTemplates';
import '../pages/PartyGuestbook.css';

// ONE MEMORY, DRAWN: the photograph framed as its author framed it, the
// dedication, and the signature — in the design the memory was published with.
//
// It is handed a VIEW MODEL and nothing else. It does not fetch, does not know
// which token or which page it is on, and does not know whether the memory is
// already in the book or still being composed: the guest page, the composer's
// preview and the manager's queue all draw it through here, and a television
// could too. That is the point of keeping it this small.
//
// Plain DOM and CSS. The composition is never rasterised: the photograph is a
// derived preview the server already stripped, and the words are text.

export interface GuestbookMemoryView {
  authorDisplayName: string;
  /** Plain text with LF paragraphs. Rendered as text, never as markup. */
  body: string;
  template: { key: string; version: number };
  media: {
    url: string;
    width: number;
    height: number;
    crop: { centerX: number; centerY: number; zoom: number };
  };
}

/**
 * The television's composition of the SAME memory: the photograph beside the
 * words instead of above them, at a size the stage decided. Everything that
 * makes it this memory — template, crop, words, signature — is unchanged.
 */
export interface GuestbookMemoryTvLayout {
  /** The photograph's box, in pixels. Its aspect is the template's frame. */
  photoWidth: number;
  photoHeight: number;
  /** The dedication's type size, in pixels. */
  fontPx: number;
  /** The words column's box, in pixels: what they must fit inside. */
  wordsWidth: number;
  wordsHeight: number;
}

export function PartyGuestbookMemoryCard({
  memory,
  bodyPlaceholder,
  authorPlaceholder,
  loading = 'lazy',
  testId = 'guestbook-memory',
  tv,
  wordsRef,
}: {
  memory: GuestbookMemoryView;
  /** Shown, muted, while a draft has no words yet. */
  bodyPlaceholder?: string;
  authorPlaceholder?: string;
  loading?: 'lazy' | 'eager';
  testId?: string;
  /** Draw it for a television (see GuestbookMemoryTvLayout). */
  tv?: GuestbookMemoryTvLayout;
  /** The words column, for a stage that measures whether they fit. */
  wordsRef?: Ref<HTMLElement>;
}) {
  const { t } = useI18n();
  const template = guestbookTemplateFor(memory.template.key, memory.template.version);
  const { width, height } = memory.media;
  const photoAspect = width > 0 && height > 0 ? width / height : 1;
  const frameAspect = template.frameAspect(photoAspect);
  // Where the photograph sits in the design's frame — zoomed out, on the
  // design's photo well (.guestbook-memory-photo).
  const placed = placePhoto(photoAspect, frameAspect, memory.media.crop);

  const body = memory.body.length > 0 ? memory.body : bodyPlaceholder ?? '';
  const author = memory.authorDisplayName.length > 0 ? memory.authorDisplayName : authorPlaceholder ?? '';

  const photoStyle: CSSProperties = tv
    ? { width: `${tv.photoWidth}px`, height: `${tv.photoHeight}px` }
    : { aspectRatio: `${frameAspect}` };
  const wordsStyle = tv
    // The width may SHRINK (see .guestbook-memory--tv): a design's own frame —
    // a paper border, a gradient rim — takes its room from the words rather
    // than pushing them past the edge of the card.
    ? ({
      '--guestbook-tv-font': `${tv.fontPx}px`, width: `${tv.wordsWidth}px`, height: `${tv.wordsHeight}px`,
    } as CSSProperties)
    : undefined;

  return (
    <figure
      className={`guestbook-memory ${template.className}${tv ? ' guestbook-memory--tv' : ''}`}
      data-template={template.key}
      data-template-version={template.version}
      data-testid={testId}
    >
      <div className="guestbook-memory-photo" style={photoStyle}>
        <img
          src={memory.media.url}
          alt={memory.authorDisplayName
            ? t('partyGuestbookMemory.photoAlt', { name: memory.authorDisplayName })
            : t('partyGuestbookMemory.photoAltDraft')}
          loading={loading}
          decoding="async"
          draggable={false}
          style={photoPlacementStyle(placed)}
        />
      </div>
      {/* The decoration, where a design has any, is drawn by the stylesheet
          and says nothing to a screen reader. */}
      <span className="guestbook-memory-ornament" aria-hidden="true" />
      <figcaption className="guestbook-memory-words" style={wordsStyle} ref={wordsRef}>
        {/* TEXT, always: never dangerouslySetInnerHTML and never a Markdown
            renderer. Paragraphs survive through `white-space: pre-wrap`. */}
        <p
          className={memory.body.length > 0 ? 'guestbook-memory-body' : 'guestbook-memory-body is-placeholder'}
          data-testid={`${testId}-body`}
        >
          {body}
        </p>
        <p
          className={memory.authorDisplayName.length > 0
            ? 'guestbook-memory-author'
            : 'guestbook-memory-author is-placeholder'}
          data-testid={`${testId}-author`}
        >
          <span aria-hidden="true">— </span>
          {author}
        </p>
      </figcaption>
    </figure>
  );
}
