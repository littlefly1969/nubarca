import { useEffect, useRef, useState } from 'react';
import {
  ApiError, listAlbumItems, uploadRootFile, type AlbumItemSummary,
} from '@nubarca/api-client';
import { smallThumbnailUrl } from '../components/files/types';
import { useI18n } from '../i18n';
import './PartyImageField.css';

// Choosing the ONE photograph a Party feature shows — a menu's, an activity's.
//
// It is a REFERENCE to one of the owner's ordinary files: never a copy, never an
// album membership. So there are two places to find one. The party's own album,
// for a photograph that is already there. And a NEW upload through the ordinary
// library upload, which makes the file one of the owner's own — quota,
// deduplication, thumbnails, Trash — and files it in no album. Choosing a
// photograph here must never put it in the slideshow, which is why nothing in
// this file calls add-to-album.

/** A chosen photograph: the owner's file, and how to preview it to the owner. */
export interface PartyImageChoice {
  fileItemId: string;
  previewUrl: string;
}

export type PartyImageUploadError = 'duplicate' | 'failed';

/** "Upload new": the ordinary owner upload, and nothing after it. */
export function PartyImageUploadButton({
  disabled, onUploaded, onError, testId,
}: {
  disabled?: boolean;
  onUploaded(choice: PartyImageChoice): void;
  onError(error: PartyImageUploadError): void;
  testId: string;
}) {
  const { t } = useI18n();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);

  async function upload(file: File) {
    setBusy(true);
    try {
      const created = await uploadRootFile(file);
      onUploaded({ fileItemId: created.id, previewUrl: smallThumbnailUrl(created.id) });
    } catch (err) {
      // The library's own rule: two files cannot share a name in one folder.
      onError(err instanceof ApiError && err.status === 409 ? 'duplicate' : 'failed');
    } finally {
      setBusy(false);
      if (input.current) input.current.value = '';
    }
  }

  return (
    <>
      <button
        type="button" disabled={disabled || busy} data-testid={testId}
        onClick={() => input.current?.click()}
      >
        {busy ? t('partyContent.imageUploading') : t('partyContent.imageUpload')}
      </button>
      <input
        ref={input} type="file" accept="image/*" hidden data-testid={`${testId}-input`}
        onChange={(e) => {
          const file = e.target.files?.[0];
          if (file) void upload(file);
        }}
      />
    </>
  );
}

export function partyImageUploadErrorKey(error: PartyImageUploadError) {
  return error === 'duplicate' ? 'partyContent.imageDuplicate' : 'partyContent.imageUploadFailed';
}

/**
 * A slot's photograph: a preview, and Choose / Upload / Remove.
 *
 * A `fileItemId` with no `previewUrl` is a reference the server no longer
 * serves — the photograph went to Trash or into the Private Vault — and the
 * field says so rather than drawing a broken frame.
 */
export function PartySlotImageField({
  kind, albumId, fileItemId, previewUrl, disabled, onChange,
}: {
  kind: string;
  /** The party's album, when it has one: the place to choose an existing photo from. */
  albumId: string | null;
  fileItemId: string | null;
  previewUrl: string | null;
  disabled?: boolean;
  onChange(next: PartyImageChoice | null): void;
}) {
  const { t } = useI18n();
  const [picking, setPicking] = useState(false);
  const [error, setError] = useState<PartyImageUploadError | null>(null);

  return (
    <div className="party-image-field" data-testid={`party-image-${kind}`}>
      <span className="party-image-field-label">{t('partyContent.image')}</span>
      {fileItemId === null ? (
        <p className="muted">{t('partyContent.imageNone')}</p>
      ) : previewUrl ? (
        <img
          className="party-image-field-preview" src={previewUrl} alt=""
          data-testid={`party-image-preview-${kind}`}
        />
      ) : (
        <p className="muted" data-testid={`party-image-unavailable-${kind}`}>
          {t('partyContent.imageUnavailable')}
        </p>
      )}

      <div className="party-image-field-actions">
        {albumId && (
          <button
            type="button" disabled={disabled} aria-expanded={picking}
            data-testid={`party-image-choose-${kind}`}
            onClick={() => setPicking((open) => !open)}
          >
            {t('partyContent.imageChooseAlbum')}
          </button>
        )}
        <PartyImageUploadButton
          disabled={disabled} testId={`party-image-upload-${kind}`}
          onUploaded={(choice) => { setError(null); setPicking(false); onChange(choice); }}
          onError={setError}
        />
        {fileItemId !== null && (
          <button
            type="button" disabled={disabled} data-testid={`party-image-remove-${kind}`}
            onClick={() => onChange(null)}
          >
            {t('partyContent.imageRemove')}
          </button>
        )}
      </div>
      <p className="party-image-field-help">{t('partyContent.imageHelp')}</p>

      {picking && albumId && (
        <PartyAlbumPhotoGrid
          albumId={albumId} selected={fileItemId}
          onPick={(choice) => { setPicking(false); onChange(choice); }}
        />
      )}
      {error && <p className="inline-error" role="alert">{t(partyImageUploadErrorKey(error))}</p>}
    </div>
  );
}

/** The party album's photographs, asked for only once the host chooses to look. */
function PartyAlbumPhotoGrid({
  albumId, selected, onPick,
}: {
  albumId: string;
  selected: string | null;
  onPick(choice: PartyImageChoice): void;
}) {
  const { t } = useI18n();
  const [items, setItems] = useState<AlbumItemSummary[] | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    listAlbumItems(albumId, ctrl.signal)
      .then((members) => setItems(members.filter((m) => m.thumbnailUrl)))
      .catch(() => { if (!ctrl.signal.aborted) setItems([]); });
    return () => ctrl.abort();
  }, [albumId]);

  if (items === null) return <p className="muted" role="status">{t('common.loading')}</p>;
  if (items.length === 0) return <p className="muted">{t('partyContent.imageAlbumEmpty')}</p>;
  return (
    <ul className="party-image-grid" data-testid="party-image-grid">
      {items.map((item) => (
        <li key={item.fileItemId}>
          <button
            type="button" aria-pressed={selected === item.fileItemId} aria-label={item.name}
            onClick={() => onPick({ fileItemId: item.fileItemId, previewUrl: item.thumbnailUrl! })}
          >
            <img src={item.thumbnailUrl!} alt="" loading="lazy" />
          </button>
        </li>
      ))}
    </ul>
  );
}
