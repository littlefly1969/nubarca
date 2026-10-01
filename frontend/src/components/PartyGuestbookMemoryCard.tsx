import { useI18n } from '../i18n';
import { cropFor } from '../pages/partyPrintGeometry';
import { cropImageStyle } from '../party/PhotoCropFrame';
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

export function PartyGuestbookMemoryCard({
  memory,
  bodyPlaceholder,
  authorPlaceholder,
  loading = 'lazy',
  testId = 'guestbook-memory',
}: {
  memory: GuestbookMemoryView;
  /** Shown, muted, while a draft has no words yet. */
  bodyPlaceholder?: string;
  authorPlaceholder?: string;
  loading?: 'lazy' | 'eager';
  testId?: string;
}) {
  const { t } = useI18n();
  const template = guestbookTemplateFor(memory.template.key, memory.template.version);
  const { width, height } = memory.media;
  const photoAspect = width > 0 && height > 0 ? width / height : 1;
  const frameAspect = template.frameAspect(photoAspect);
  const crop = cropFor(photoAspect, frameAspect, {
    zoom: memory.media.crop.zoom,
    centerX: memory.media.crop.centerX,
    centerY: memory.media.crop.centerY,
  });

  const body = memory.body.length > 0 ? memory.body : bodyPlaceholder ?? '';
  const author = memory.authorDisplayName.length > 0 ? memory.authorDisplayName : authorPlaceholder ?? '';

  return (
    <figure
      className={`guestbook-memory ${template.className}`}
      data-template={template.key}
      data-template-version={template.version}
      data-testid={testId}
    >
      <div className="guestbook-memory-photo" style={{ aspectRatio: `${frameAspect}` }}>
        <img
          src={memory.media.url}
          alt={memory.authorDisplayName
            ? t('partyGuestbookMemory.photoAlt', { name: memory.authorDisplayName })
            : t('partyGuestbookMemory.photoAltDraft')}
          loading={loading}
          decoding="async"
          draggable={false}
          style={cropImageStyle(crop)}
        />
      </div>
      {/* The decoration, where a design has any, is drawn by the stylesheet
          and says nothing to a screen reader. */}
      <span className="guestbook-memory-ornament" aria-hidden="true" />
      <figcaption className="guestbook-memory-words">
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
