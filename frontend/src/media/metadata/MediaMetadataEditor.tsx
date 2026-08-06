import { useId, useState } from 'react';
import type { FormEvent } from 'react';
import {
  ApiError,
  updateFileMetadata,
  type FileMetadata,
  type UpdateFileMetadataRequest,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';
import { useI18n } from '../../i18n';
import { MediaTagEditor } from './MediaTagEditor';

// Editor for the owner-scoped user metadata: title, description, tags, rating,
// favourite, capture date and location. Identical for photos and videos — these
// fields are FileItemUserMetadata, which knows nothing about media kind, so
// there is deliberately no `kind` prop and no second video-specific editor.
//
// The endpoint has FULL-REPLACE semantics (an omitted field is cleared), so the
// form is always seeded from the loaded document and always sends every field.

// Render the stored UTC instant as a datetime-local-ready string and parse it
// back as UTC on save, so the value shown is the value stored (no TZ drift).
export function toLocalInput(iso: string | null): string {
  return iso ? iso.slice(0, 16) : '';
}
export function fromLocalInput(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  return new Date(`${trimmed}:00Z`).toISOString();
}

interface Props {
  data: FileMetadata;
  onCancel(): void;
  onSaved(next: FileMetadata): void;
}

export function MediaMetadataEditor({ data, onCancel, onSaved }: Props) {
  const { invalidateAuth } = useAuth();
  const { t } = useI18n();
  const titleId = useId();
  const descId = useId();
  const ratingId = useId();
  const dateId = useId();
  const locationId = useId();
  const favId = useId();

  const [title, setTitle] = useState(data.user.title ?? '');
  const [description, setDescription] = useState(data.user.description ?? '');
  const [tags, setTags] = useState<string[]>(data.user.tags);
  const [rating, setRating] = useState(data.user.rating != null ? String(data.user.rating) : '');
  const [favorite, setFavorite] = useState(data.user.favorite);
  const [dateTaken, setDateTaken] = useState(toLocalInput(data.user.dateTakenOverride));
  const [location, setLocation] = useState(data.user.locationOverride ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function buildPatch(): UpdateFileMetadataRequest {
    const parsedRating = rating.trim().length === 0 ? null : Number.parseInt(rating, 10);
    return {
      // An empty title clears it, which makes the file name reappear everywhere.
      title: title.trim().length === 0 ? null : title.trim(),
      description: description.trim().length === 0 ? null : description.trim(),
      tags,
      rating: Number.isFinite(parsedRating ?? NaN) ? parsedRating : null,
      favorite,
      dateTakenOverride: fromLocalInput(dateTaken),
      locationOverride: location.trim().length === 0 ? null : location.trim(),
    };
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (saving) return;
    setSaving(true);
    setError(null);
    try {
      const next = await updateFileMetadata(data.id, buildPatch());
      onSaved(next);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        // Surface the server's own validation message (tag limits, lengths)
        // rather than a generic one — nothing is saved partially.
        const body = err.body as { error?: unknown } | null;
        setError(typeof body?.error === 'string' ? body.error : t('gallery.invalidMetadata'));
        return;
      }
      setError(t('gallery.saveError'));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="lightbox-metadata" aria-label={t('mediaMeta.panelAria')}>
      <form
        className="metadata-edit-form"
        onSubmit={onSubmit}
        aria-label={t('gallery.editMetadataAria')}
        noValidate
      >
        <p className="muted metadata-edit-note">{t('gallery.editNote')}</p>

        {/* The original file name is never editable here — a title is a label,
            not a rename — but it stays visible so the user keeps the reference. */}
        <p className="muted metadata-edit-note" data-testid="media-editor-filename">
          {t('mediaMeta.fileName')}: {data.name}
        </p>

        <label htmlFor={titleId}>{t('gallery.editTitle')}</label>
        <input
          id={titleId} type="text" maxLength={255} data-testid="media-editor-title"
          value={title} onChange={(e) => setTitle(e.target.value)}
        />

        <label htmlFor={descId}>{t('gallery.editDescription')}</label>
        <textarea
          id={descId} rows={3} maxLength={2000}
          value={description} onChange={(e) => setDescription(e.target.value)}
        />

        <MediaTagEditor tags={tags} onChange={setTags} disabled={saving} />

        <label htmlFor={ratingId}>
          {t('gallery.editRating')} <span className="muted">{t('gallery.editRatingHint')}</span>
        </label>
        <input
          id={ratingId} type="number" min={0} max={5} step={1}
          value={rating} onChange={(e) => setRating(e.target.value)}
        />

        <label htmlFor={favId}>
          <input
            id={favId} type="checkbox"
            checked={favorite} onChange={(e) => setFavorite(e.target.checked)}
          />{' '}
          {t('gallery.editFavorite')}
        </label>

        <label htmlFor={dateId}>
          {t('gallery.editDateTaken')} <span className="muted">{t('gallery.editDateTakenHint')}</span>
        </label>
        <input
          id={dateId} type="datetime-local"
          value={dateTaken} onChange={(e) => setDateTaken(e.target.value)}
        />

        <label htmlFor={locationId}>
          {t('gallery.editLocation')} <span className="muted">{t('gallery.editLocationHint')}</span>
        </label>
        <input
          id={locationId} type="text" maxLength={512}
          value={location} onChange={(e) => setLocation(e.target.value)}
        />

        {error !== null && <p className="metadata-edit-error" role="alert">{error}</p>}

        <div className="metadata-edit-actions">
          <button type="submit" className="row-action-primary" disabled={saving}>
            {saving ? t('common.saving') : t('common.save')}
          </button>
          <button type="button" className="row-action" onClick={onCancel} disabled={saving}>
            {t('common.cancel')}
          </button>
        </div>
      </form>
    </section>
  );
}
