import { useEffect, useState } from 'react';
import {
  ApiError,
  setPartyGuestContent,
  type PartyGuestContentKind,
  type PartyGuestContentSlot,
  type PartyMediaPresentation,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
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
  const [draft, setDraft] = useState<Draft>(() => fromSlot(slot));
  const [busy, setBusy] = useState(false);
  const [status, setStatus] =
    useState<'idle' | 'saved' | 'conflict' | 'failed' | 'invalidMedia' | 'invalidPresentation'>(
      'idle');

  // Re-seeded when the SERVER's version moves — including after a conflict,
  // which is how the card adopts what actually happened.
  useEffect(() => { setDraft(fromSlot(slot)); }, [slot.version, slot.kind]);

  const set = (key: string, value: string) =>
    setDraft((d) => ({ ...d, content: { ...d.content, [key]: value } }));

  async function save() {
    setBusy(true); setStatus('idle');
    try {
      onSaved(await setPartyGuestContent(partyId, slot.kind, {
        enabled: draft.enabled,
        visibleBefore: draft.visibleBefore,
        visibleLive: draft.visibleLive,
        visibleAfter: draft.visibleAfter,
        content: draft.content,
        mediaFileItemId: draft.mediaFileItemId,
        mediaPresentation: draft.mediaPresentation,
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
      } else {
        setStatus('failed');
      }
    } finally { setBusy(false); }
  }

  return (
    <section className="party-card" data-testid={`party-content-${slot.kind}`}>
      <label className="party-toggle">
        <input
          type="checkbox" checked={draft.enabled} disabled={busy}
          aria-label={t(contentLabelKey(slot.kind))}
          onChange={(e) => setDraft((d) => ({ ...d, enabled: e.target.checked }))}
        />
        <span>{t(contentLabelKey(slot.kind))}</span>
      </label>

      {/* Progressive disclosure: a slot the host has not turned on shows its
          name and nothing else. The form appears when there is a reason for it. */}
      {draft.enabled && (
        <>
          <ContentFields kind={slot.kind} content={draft.content} busy={busy} set={set} />

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
                className="row-action"
                disabled={busy}
                data-testid={`party-poster-lost-inline-${slot.kind}`}
                onClick={() => setDraft((d) => ({ ...d, mediaPresentation: 'inline' }))}
              >
                {t('partyContent.posterMediaLostInline')}
              </button>
            </div>
          )}

          <div className="party-content-visibility">
            {phases.map((phase) => {
              const key = phase === 'before'
                ? 'visibleBefore' : phase === 'live' ? 'visibleLive' : 'visibleAfter';
              return (
                <label className="party-toggle" key={phase}>
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

          <button
            type="button" className="row-action-primary" disabled={busy}
            data-testid={`party-content-save-${slot.kind}`}
            onClick={() => void save()}
          >
            {t('party.overview.save')}
          </button>
        </>
      )}

      {status === 'saved' && <p className="muted" role="status">{t('party.overview.saved')}</p>}
      {status === 'conflict' && (
        <p className="inline-error" role="alert" data-testid={`party-content-conflict-${slot.kind}`}>
          {t('party.overview.conflict')}
        </p>
      )}
      {status === 'invalidMedia' && (
        <p className="inline-error" role="alert">{t('partyContent.imageInvalid')}</p>
      )}
      {status === 'invalidPresentation' && (
        <p className="inline-error" role="alert">{t('partyContent.presentationInvalid')}</p>
      )}
      {status === 'failed' && (
        <p className="inline-error" role="alert">{t('party.overview.saveFailed')}</p>
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
        <p className="muted party-presentation-note">{t('partyContent.presentationTextKept')}</p>
      )}
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
  const field = (key: string, labelKey: Parameters<typeof t>[0], long = false) => (
    <label className="party-field" key={key}>
      <span>{t(labelKey)}</span>
      {long ? (
        <textarea
          rows={3} value={text(content, key)} disabled={busy}
          aria-label={t(labelKey)} onChange={(e) => set(key, e.target.value)}
        />
      ) : (
        <input
          value={text(content, key)} disabled={busy}
          aria-label={t(labelKey)} onChange={(e) => set(key, e.target.value)}
        />
      )}
    </label>
  );

  switch (kind) {
    case 'invitation':
      return <>{field('headline', 'partyContent.headline')}{field('message', 'partyContent.message', true)}</>;
    case 'location':
      return (
        <>
          {field('venueName', 'partyContent.venue')}
          {field('address', 'partyContent.address')}
          {field('note', 'partyContent.note', true)}
        </>
      );
    case 'dress-code':
      return <>{field('headline', 'partyContent.headline')}{field('description', 'partyContent.message', true)}</>;
    case 'menu':
      // The sections are edited as a whole in a later slice; for now the intro
      // is what an owner can write here, and an existing menu's sections are
      // preserved untouched because the draft carries them through.
      return <>{field('intro', 'partyContent.message', true)}</>;
    case 'info':
      return <>{field('title', 'partyContent.headline')}{field('body', 'partyContent.message', true)}</>;
    default:
      return <>{field('headline', 'partyContent.headline')}{field('message', 'partyContent.message', true)}</>;
  }
}

function contentLabelKey(kind: PartyGuestContentKind) {
  return `partyContent.kind.${kind}` as 'partyContent.kind.invitation';
}

function phaseLabelKey(phase: 'before' | 'live' | 'after') {
  return `partyContent.phase.${phase}` as 'partyContent.phase.before';
}
