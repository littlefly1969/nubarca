import { useEffect, useState } from 'react';
import {
  ApiError,
  type PartyGuestContentKind,
  type PartyGuestContentSlot,
  type PartyMediaPresentation,
  type PartyMediaCrop,
  type PartyMediaOrientation,
  type PartyTextAlign,
  type PartyTextPlacement,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { usePartyApi } from './workspace/partyApi';
import { DEFAULT_CROP_VIEW, MAX_ZOOM } from '../pages/partyPrintGeometry';
import { defaultTextAlign, SLOT_FRAME_ASPECT } from './PartyGuestContent';
import { PhotoCropFrame } from './PhotoCropFrame';
import { PartySlotImageField } from './PartyImageField';

// The owner's typed editors: six named shapes, one card each.
//
// Deliberately NOT a page builder. There is no block palette, no drag handle,
// no HTML field and no slug — a party has a handful of things to say, and each
// of them has a form that knows what it is. The server validates every payload
// against the same shape, so this file is a convenience rather than the rule.
//
// Every card is its own draft with its OWN version: editing the menu never
// contends with renaming the party, and a conflict refreshes just that card.
// Choosing, replacing or removing the slot's photograph is an edit like any
// other, saved with the rest of the card.

type Draft = {
  enabled: boolean;
  visibleBefore: boolean;
  visibleLive: boolean;
  visibleAfter: boolean;
  content: Record<string, unknown>;
  mediaFileItemId: string | null;
  /** How the host previews the chosen photograph. Never sent. */
  mediaPreviewUrl: string | null;
  mediaPresentation: PartyMediaPresentation;
  /** The host's alignment, or null while they have not chosen one. */
  textAlign: PartyTextAlign | null;
  /** The inline photograph's frame: null is the whole photograph. */
  mediaOrientation: PartyMediaOrientation | null;
  mediaCrop: PartyMediaCrop | null;
  /** The words on the photograph, or null for below it. */
  textPlacement: 'overlay' | null;
  version: number;
};

const fromSlot = (slot: PartyGuestContentSlot): Draft => ({
  enabled: slot.enabled,
  visibleBefore: slot.visibleBefore,
  visibleLive: slot.visibleLive,
  visibleAfter: slot.visibleAfter,
  content: { ...(slot.content ?? {}) },
  mediaFileItemId: slot.mediaFileItemId ?? null,
  mediaPreviewUrl: slot.mediaUrl ?? null,
  mediaPresentation: slot.mediaPresentation ?? 'inline',
  textAlign: slot.textAlign ?? null,
  // Only the two formats the editor knows; anything else is the whole photo.
  mediaOrientation: slot.mediaOrientation === 'portrait' || slot.mediaOrientation === 'landscape'
    ? slot.mediaOrientation : null,
  mediaCrop: slot.mediaCrop ?? null,
  textPlacement: slot.textPlacement === 'overlay' ? 'overlay' : null,
  version: slot.version,
});

const text = (content: Record<string, unknown>, key: string): string => {
  const value = content[key];
  return typeof value === 'string' ? value : '';
};

export function PartyContentCard({
  slot, partyId, albumId, phases, onSaved,
}: {
  slot: PartyGuestContentSlot;
  partyId: string;
  /** The party's album, offered as a place to choose a photograph from — or null. */
  albumId: string | null;
  /** Which surfaces this card offers a visibility switch for. */
  phases: readonly ('before' | 'live' | 'after')[];
  onSaved(next: PartyGuestContentSlot): void;
}) {
  const { t } = useI18n();
  const api = usePartyApi();
  const [draft, setDraft] = useState<Draft>(() => fromSlot(slot));
  const [busy, setBusy] = useState(false);
  const [status, setStatus] =
    useState<'idle' | 'saved' | 'conflict' | 'failed' | 'invalidMedia' | 'invalidPresentation'
      | 'invalidContent'>('idle');

  // Re-seeded when the SERVER's version moves — including after a conflict,
  // which is how the card adopts what actually happened.
  useEffect(() => { setDraft(fromSlot(slot)); }, [slot.version, slot.kind]);

  const set = (key: string, value: string) =>
    setDraft((d) => ({ ...d, content: { ...d.content, [key]: value } }));

  async function save() {
    setBusy(true); setStatus('idle');
    try {
      onSaved(await api.setPartyGuestContent(partyId, slot.kind, {
        enabled: draft.enabled,
        visibleBefore: draft.visibleBefore,
        visibleLive: draft.visibleLive,
        visibleAfter: draft.visibleAfter,
        content: draft.content,
        mediaFileItemId: draft.mediaFileItemId,
        mediaPresentation: draft.mediaPresentation,
        // null until the host picks, which the server reads as "unchanged".
        textAlign: draft.textAlign,
        // Always stated: "auto" is the whole photograph, and a fixed frame
        // carries where it sits — centred until the host moves it.
        mediaOrientation: draft.mediaOrientation ?? 'auto',
        mediaCrop: draft.mediaOrientation ? draft.mediaCrop ?? DEFAULT_CROP_VIEW : null,
        textPlacement: draft.textPlacement ?? 'below',
        version: draft.version,
      }));
      setStatus('saved');
    } catch (err) {
      const body = (err as ApiError).body as
        { content?: PartyGuestContentSlot; error?: string } | undefined;
      if (err instanceof ApiError && err.status === 409 && body?.content) {
        onSaved(body.content);
        setStatus('conflict');
      } else if (err instanceof ApiError && err.status === 400 && body?.error === 'invalid_media') {
        setStatus('invalidMedia');
      } else if (err instanceof ApiError && err.status === 400
        && body?.error === 'invalid_presentation') {
        setStatus('invalidPresentation');
      } else if (err instanceof ApiError && err.status === 400
        && body?.error === 'invalid_content') {
        // A required field left empty. Said plainly: "could not save" under a
        // form that looks complete is what made a host give up on a slot.
        setStatus('invalidContent');
      } else {
        setStatus('failed');
      }
    } finally { setBusy(false); }
  }

  // What the card says about itself before it is opened: whether the guests
  // see this at all, and when. It is the STATE — never the switch's label,
  // which says what pressing it would do.
  const shownIn = phases.filter((phase) => draft[
    phase === 'before' ? 'visibleBefore' : phase === 'live' ? 'visibleLive' : 'visibleAfter'
  ]);
  const stateLine = !draft.enabled
    ? t('partyContent.stateOff')
    : shownIn.length === 0
      ? t('partyContent.stateNowhere')
      : t('partyContent.stateOn', {
        phases: shownIn.map((phase) => t(phaseLabelKey(phase))).join(' · '),
      });

  return (
    <section className="pw-panel" data-testid={`party-content-${slot.kind}`}>
      <div className="pw-panel-head">
        <div>
          <h3 className="pw-panel-title">{t(contentLabelKey(slot.kind))}</h3>
          <p className="pw-panel-note" data-testid={`party-content-state-${slot.kind}`}>{stateLine}</p>
        </div>
        <button
          type="button" role="switch" className="pw-switch"
          aria-checked={draft.enabled} disabled={busy}
          aria-label={t(contentLabelKey(slot.kind))}
          data-testid={`party-content-enable-${slot.kind}`}
          onClick={() => setDraft((d) => ({ ...d, enabled: !d.enabled }))}
        />
      </div>

      {/* Progressive disclosure: a slot the host has not turned on shows its
          name and nothing else. The form appears when there is a reason for it. */}
      {draft.enabled && (
        <div className="pw-panel-body">
          <ContentFields kind={slot.kind} content={draft.content} busy={busy} set={set} />

          <TextAlignChoice
            kind={slot.kind}
            value={draft.textAlign ?? defaultTextAlign(slot.kind)}
            disabled={busy}
            onChange={(textAlign) => setDraft((d) => ({ ...d, textAlign }))}
          />

          <PartySlotImageField
            kind={slot.kind}
            albumId={albumId}
            fileItemId={draft.mediaFileItemId}
            previewUrl={draft.mediaPreviewUrl}
            disabled={busy}
            onChange={(next) => setDraft((d) => ({
              ...d,
              mediaFileItemId: next?.fileItemId ?? null,
              mediaPreviewUrl: next?.previewUrl ?? null,
              // Removing the photograph takes the choice with it: "full screen"
              // with nothing to show is not a state, and the server refuses it.
              // Choosing a DIFFERENT photograph keeps the current answer, which
              // is what makes replacing a poster one click rather than three.
              mediaPresentation: next ? d.mediaPresentation : 'inline',
              // A crop belongs to one picture: a new one starts centred.
              mediaCrop: null,
            }))}
          />

          {/* Only once there IS a photograph: there is nothing to decide about
              an image that is not there. */}
          {draft.mediaFileItemId && (
            <PresentationChoice
              kind={slot.kind}
              value={draft.mediaPresentation}
              disabled={busy}
              onChange={(mediaPresentation) => setDraft((d) => ({ ...d, mediaPresentation }))}
            />
          )}

          {/* How the photograph sits in its section — only for one that IS in
              the section: a poster is opened whole and needs no frame. */}
          {draft.mediaFileItemId && draft.mediaPresentation === 'inline' && (
            <PhotoFrameChoice
              kind={slot.kind}
              previewUrl={draft.mediaPreviewUrl}
              orientation={draft.mediaOrientation}
              crop={draft.mediaCrop}
              disabled={busy}
              onChange={(mediaOrientation, mediaCrop) =>
                setDraft((d) => ({ ...d, mediaOrientation, mediaCrop }))}
            />
          )}

          {/* Where the words sit — only where there is a picture in the page to
              put them on. The menu is a card with a list, not a caption. */}
          {draft.mediaFileItemId && draft.mediaPresentation === 'inline' && slot.kind !== 'menu' && (
            <TextPlacementChoice
              kind={slot.kind}
              value={draft.textPlacement ?? 'below'}
              disabled={busy}
              onChange={(placement) => setDraft((d) => ({
                ...d, textPlacement: placement === 'overlay' ? 'overlay' : null,
              }))}
            />
          )}

          {/* A poster whose photograph was permanently deleted. The foreign key
              is ON DELETE SET NULL, so this row is legitimate rather than
              corrupt — and the server deliberately does NOT rewrite it to
              inline, because that would publish the words the host replaced
              with a picture. It is theirs to resolve, so it is stated plainly
              and given both ways out. */}
          {!draft.mediaFileItemId && draft.mediaPresentation === 'poster' && (
            <div className="party-presentation-lost" data-testid={`party-poster-lost-${slot.kind}`}>
              <p role="status">{t('partyContent.posterMediaLost')}</p>
              <button
                type="button"
                className="pw-btn"
                disabled={busy}
                data-testid={`party-poster-lost-inline-${slot.kind}`}
                onClick={() => setDraft((d) => ({ ...d, mediaPresentation: 'inline' }))}
              >
                {t('partyContent.posterMediaLostInline')}
              </button>
            </div>
          )}

          {/* WHEN the guests see it. A set of choices, not a row of anonymous
              boxes: each says which moment of the evening it is about. */}
          <fieldset className="pw-choice-set" data-testid={`party-content-phases-${slot.kind}`}>
            <legend className="pw-field-label">{t('partyContent.visibleWhen')}</legend>
            <div className="pw-choice-row">
              {phases.map((phase) => {
                const key = phase === 'before'
                  ? 'visibleBefore' : phase === 'live' ? 'visibleLive' : 'visibleAfter';
                return (
                  <label className="pw-choice" key={phase} data-checked={draft[key]}>
                    <input
                      type="checkbox" checked={draft[key]} disabled={busy}
                      aria-label={`${t(contentLabelKey(slot.kind))} — ${t(phaseLabelKey(phase))}`}
                      onChange={(e) => setDraft((d) => ({ ...d, [key]: e.target.checked }))}
                    />
                    <span>{t(phaseLabelKey(phase))}</span>
                  </label>
                );
              })}
            </div>
          </fieldset>

          <div className="pw-form-foot">
            <button
              type="button" className="pw-btn pw-btn--primary" disabled={busy} aria-busy={busy || undefined}
              data-testid={`party-content-save-${slot.kind}`}
              onClick={() => void save()}
            >
              {t('party.overview.save')}
            </button>
            <span aria-live="polite">
              {status === 'saved' && (
                <span className="pw-small pw-muted" role="status">{t('party.overview.saved')}</span>
              )}
            </span>
          </div>
        </div>
      )}

      {status === 'conflict' && (
        <p className="pw-field-error" role="alert" data-testid={`party-content-conflict-${slot.kind}`}>
          {t('party.overview.conflict')}
        </p>
      )}
      {status === 'invalidMedia' && (
        <p className="pw-field-error" role="alert">{t('partyContent.imageInvalid')}</p>
      )}
      {status === 'invalidPresentation' && (
        <p className="pw-field-error" role="alert">{t('partyContent.presentationInvalid')}</p>
      )}
      {status === 'invalidContent' && (
        <p className="pw-field-error" role="alert" data-testid={`party-content-invalid-${slot.kind}`}>
          {t('partyContent.invalidContent')}
        </p>
      )}
      {status === 'failed' && (
        <p className="pw-field-error" role="alert">{t('party.overview.saveFailed')}</p>
      )}
    </section>
  );
}

/**
 * How this slot's photograph is presented, as a plain two-way choice.
 *
 * Radio buttons rather than a toggle because the two options are not on and
 * off: each is a different thing the guest surface does, and each is worth a
 * sentence. The poster option names the row the guest will actually see, built
 * from the same localized kind label the surface renders — so what the host
 * reads here is what appears there.
 */
function PresentationChoice({
  kind, value, disabled, onChange,
}: {
  kind: PartyGuestContentKind;
  value: PartyMediaPresentation;
  disabled: boolean;
  onChange(next: PartyMediaPresentation): void;
}) {
  const { t } = useI18n();
  const rowLabel = t(`partyGuest.poster.${kind}` as 'partyGuest.poster.invitation');
  return (
    <fieldset className="party-presentation" data-testid={`party-presentation-${kind}`}>
      <legend>{t('partyContent.presentation')}</legend>
      {(['inline', 'poster'] as const).map((option) => (
        <label className="party-presentation-option" key={option}>
          <input
            type="radio"
            name={`presentation-${kind}`}
            value={option}
            checked={value === option}
            disabled={disabled}
            data-testid={`party-presentation-${kind}-${option}`}
            onChange={() => onChange(option)}
          />
          <span>
            <span className="party-presentation-title">
              {t(option === 'inline'
                ? 'partyContent.presentationInline'
                : 'partyContent.presentationPoster')}
            </span>
            <span className="party-presentation-help">
              {option === 'inline'
                ? t('partyContent.presentationInlineHelp')
                : t('partyContent.presentationPosterHelp').replace('{label}', rowLabel)}
            </span>
          </span>
        </label>
      ))}
      {/* The words are not deleted, and saying so is the difference between a
          presentation choice and losing an evening's typing. */}
      {value === 'poster' && (
        <p className="pw-small pw-muted party-presentation-note">{t('partyContent.presentationTextKept')}</p>
      )}
    </fieldset>
  );
}

/**
 * Where the slot's words sit: at the left edge, or centred.
 *
 * Offered on every slot that has words, as a plain two-way choice. What it
 * shows selected is what the guest sees today — the surface's own default until
 * the host picks — so opening a card and saving it changes nothing.
 */
function TextAlignChoice({
  kind, value, disabled, onChange,
}: {
  kind: PartyGuestContentKind;
  value: PartyTextAlign;
  disabled: boolean;
  onChange(next: PartyTextAlign): void;
}) {
  const { t } = useI18n();
  return (
    <fieldset className="party-presentation" data-testid={`party-text-align-${kind}`}>
      <legend>{t('partyContent.textAlign')}</legend>
      {(['left', 'center'] as const).map((option) => (
        <label className="party-presentation-option" key={option}>
          <input
            type="radio"
            name={`text-align-${kind}`}
            value={option}
            checked={value === option}
            disabled={disabled}
            data-testid={`party-text-align-${kind}-${option}`}
            onChange={() => onChange(option)}
          />
          <span className="party-presentation-title">
            {t(option === 'left' ? 'partyContent.textAlignLeft' : 'partyContent.textAlignCenter')}
          </span>
        </label>
      ))}
    </fieldset>
  );
}

/**
 * How a section photograph is framed: the whole picture, or a fixed portrait or
 * landscape frame the host places it in — the party print's own crop editor,
 * dragged or moved with the arrow keys, with a zoom beside it.
 */
function PhotoFrameChoice({
  kind, previewUrl, orientation, crop, disabled, onChange,
}: {
  kind: PartyGuestContentKind;
  previewUrl: string | null;
  orientation: PartyMediaOrientation | null;
  crop: PartyMediaCrop | null;
  disabled: boolean;
  onChange(orientation: PartyMediaOrientation | null, crop: PartyMediaCrop | null): void;
}) {
  const { t } = useI18n();
  // The photograph's shape, learned from the preview as it loads.
  const [aspect, setAspect] = useState(1);
  const view = crop ?? DEFAULT_CROP_VIEW;
  return (
    <fieldset className="party-presentation" data-testid={`party-frame-${kind}`}>
      <legend>{t('partyContent.frame')}</legend>
      {(['whole', 'portrait', 'landscape'] as const).map((option) => (
        <label className="party-presentation-option" key={option}>
          <input
            type="radio"
            name={`frame-${kind}`}
            value={option}
            checked={(orientation ?? 'whole') === option}
            disabled={disabled}
            data-testid={`party-frame-${kind}-${option}`}
            // A new format starts centred: a crop is only meaningful in the
            // frame it was made for.
            onChange={() => onChange(option === 'whole' ? null : option, null)}
          />
          <span className="party-presentation-title">
            {t(`partyContent.frame.${option}` as 'partyContent.frame.whole')}
          </span>
        </label>
      ))}
      {orientation && previewUrl && (
        <div className="party-frame-editor">
          <PhotoCropFrame
            src={previewUrl}
            aspect={aspect}
            slotAspect={SLOT_FRAME_ASPECT[orientation]}
            view={view}
            label={t('partyContent.frameHelp')}
            onAspect={(width, height) => { if (width > 0 && height > 0) setAspect(width / height); }}
            onChange={(next) => onChange(orientation, next)}
            testId={`party-frame-crop-${kind}`}
          />
          <p className="pw-small pw-muted">{t('partyContent.frameHelp')}</p>
          <label className="pw-field">
            <span>{t('partyContent.frameZoom')}</span>
            <input
              type="range" min={1} max={MAX_ZOOM} step={0.05} value={view.zoom}
              disabled={disabled} aria-label={t('partyContent.frameZoom')}
              onChange={(event) => onChange(orientation, { ...view, zoom: Number(event.target.value) })}
            />
          </label>
          <button
            type="button" className="pw-btn" disabled={disabled}
            onClick={() => onChange(orientation, null)}
          >
            {t('partyContent.frameReset')}
          </button>
        </div>
      )}
    </fieldset>
  );
}

/** The words below the photograph, or on it — across its lower part, like the cover. */
function TextPlacementChoice({
  kind, value, disabled, onChange,
}: {
  kind: PartyGuestContentKind;
  value: PartyTextPlacement;
  disabled: boolean;
  onChange(next: PartyTextPlacement): void;
}) {
  const { t } = useI18n();
  return (
    <fieldset className="party-presentation" data-testid={`party-text-placement-${kind}`}>
      <legend>{t('partyContent.textPlacement')}</legend>
      {(['below', 'overlay'] as const).map((option) => (
        <label className="party-presentation-option" key={option}>
          <input
            type="radio"
            name={`text-placement-${kind}`}
            value={option}
            checked={value === option}
            disabled={disabled}
            data-testid={`party-text-placement-${kind}-${option}`}
            onChange={() => onChange(option)}
          />
          <span className="party-presentation-title">
            {t(option === 'below'
              ? 'partyContent.textPlacementBelow'
              : 'partyContent.textPlacementOverlay')}
          </span>
        </label>
      ))}
    </fieldset>
  );
}

function ContentFields({
  kind, content, busy, set,
}: {
  kind: PartyGuestContentKind;
  content: Record<string, unknown>;
  busy: boolean;
  set(key: string, value: string): void;
}) {
  const { t } = useI18n();
  // `required` mirrors the SERVER's rule for this kind, so the form says up
  // front what a save would be refused for rather than after the fact.
  const field = (
    key: string,
    labelKey: Parameters<typeof t>[0],
    { long = false, required = false }: { long?: boolean; required?: boolean } = {},
  ) => (
    <label className="pw-field" key={key}>
      <span className="pw-field-label">{t(labelKey)}{required && <span aria-hidden="true"> *</span>}</span>
      {long ? (
        <textarea
          rows={3} value={text(content, key)} disabled={busy} required={required}
          aria-label={t(labelKey)} onChange={(e) => set(key, e.target.value)}
        />
      ) : (
        <input
          value={text(content, key)} disabled={busy} required={required}
          aria-label={t(labelKey)} onChange={(e) => set(key, e.target.value)}
        />
      )}
    </label>
  );

  switch (kind) {
    case 'invitation':
      return <>{field('headline', 'partyContent.headline')}{field('message', 'partyContent.message', { long: true })}</>;
    case 'location':
      return (
        <>
          {field('venueName', 'partyContent.venue', { required: true })}
          {field('address', 'partyContent.address', { required: true })}
          {field('note', 'partyContent.note', { long: true })}
        </>
      );
    case 'dress-code':
      return (
        <>
          {field('headline', 'partyContent.headline', { required: true })}
          {field('description', 'partyContent.message', { long: true })}
        </>
      );
    case 'menu':
      // The sections are edited as a whole in a later slice; for now the intro
      // is what an owner can write here, and an existing menu's sections are
      // preserved untouched because the draft carries them through.
      return <>{field('intro', 'partyContent.message', { long: true })}</>;
    case 'info':
      return <>{field('title', 'partyContent.headline')}{field('body', 'partyContent.message', { long: true })}</>;
    default:
      return <>{field('headline', 'partyContent.headline')}{field('message', 'partyContent.message', { long: true })}</>;
  }
}

function contentLabelKey(kind: PartyGuestContentKind) {
  return `partyContent.kind.${kind}` as 'partyContent.kind.invitation';
}

function phaseLabelKey(phase: 'before' | 'live' | 'after') {
  return `partyContent.phase.${phase}` as 'partyContent.phase.before';
}
