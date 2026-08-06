import { originalDownloadUrl, privacySafeDownloadUrl, type FileMetadata } from '@nubarca/api-client';
import { formatSize } from '../../components/format';
import { Icon } from '../../components/icons/Icon';
import { useI18n } from '../../i18n';
import type { MediaKind } from './MediaMetadataPanel';

// Read-only metadata body shared by both galleries.
//
// The detail rows are unchanged. The ACTION area is now grouped by intent
// instead of being one flat row of buttons plus an unreadable inline album
// select:
//
//   Metadata  — Edit metadata, Write Date Taken (jpeg + override only)
//   Organize  — Add to album (opens the shared album picker)
//   Discover  — Find similar in Library / Explore similar photos (photos only)
//   File      — Download original (+ the privacy-safe copy where supported)
//
// Strip/remove-metadata is deliberately NOT offered any more: it rewrites the
// bytes into a new blob, which is far too destructive to sit one click away in a
// viewer drawer. The backend capability is untouched — only this UI entry point
// is gone.

type Row = [string, string];

// Formats a fractional-seconds duration as H:MM:SS (or M:SS under an hour).
function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  const hours = Math.floor(s / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  const seconds = s % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

interface Props {
  data: FileMetadata;
  kind: MediaKind;
  onEdit(): void;
  onWriteDateTaken(): void;
  writing: boolean;
  writeError: string | null;
  onAddToAlbum(): void;
  // Photos only. Applies the library's similar-image anchor filter and returns
  // the user to the filtered Library.
  onFindSimilarInLibrary?(): void;
  // Photos only. Navigates to the dedicated Similar Photos Explorer.
  onExploreSimilar?(): void;
}

export function MediaMetadataView({
  data,
  kind,
  onEdit,
  onWriteDateTaken,
  writing,
  writeError,
  onAddToAlbum,
  onFindSimilarInLibrary,
  onExploreSimilar,
}: Props) {
  const { t, tn, formatDate } = useI18n();
  const { blob, user, effective } = data;
  const e = blob.embedded;

  const rows: Row[] = [];

  // Identity first: the title (when set) and ALWAYS the original file name, so
  // the name used for downloads and diagnostics never becomes unreachable.
  if (user.title) rows.push([t('mediaMeta.title'), user.title]);
  rows.push([t('mediaMeta.fileName'), data.name]);

  const dateLabel =
    effective.dateTakenSource === 'user' ? t('gallery.mdDateTakenOverride')
      : effective.dateTakenSource === 'embedded' ? t('gallery.mdDateTaken')
      : t('gallery.mdUploaded');
  rows.push([dateLabel, formatDate(effective.dateTaken)]);

  if (blob.width !== null && blob.height !== null) {
    rows.push([t('gallery.mdDimensions'), `${blob.width}×${blob.height}`]);
  }
  rows.push([t('gallery.mdFileSize'), formatSize(data.sizeBytes)]);

  // --- kind-specific technical blocks --------------------------------------
  if (kind === 'image' && e) {
    const camera = [e.cameraMake, e.cameraModel].filter(Boolean).join(' ');
    if (camera) rows.push([t('gallery.mdCamera'), camera]);
    if (e.lensModel) rows.push([t('gallery.mdLens'), e.lensModel]);
    if (e.iso != null) rows.push(['ISO', String(e.iso)]);
    if (e.aperture != null) rows.push([t('gallery.mdAperture'), `f/${e.aperture}`]);
    if (e.exposureTime) rows.push([t('gallery.mdExposure'), e.exposureTime]);
    if (e.focalLength != null) rows.push([t('gallery.mdFocalLength'), `${e.focalLength} mm`]);
    if (e.colorSpace) rows.push([t('gallery.mdColorSpace'), e.colorSpace]);
    if (e.orientation != null) rows.push([t('gallery.mdOrientation'), String(e.orientation)]);
    // Privacy: presence only. Coordinates are never exposed here.
    rows.push([t('gallery.gps'), e.hasGps ? t('gallery.gpsPresent') : t('gallery.gpsNoneValue')]);
  }

  const v = blob.video;
  if (kind === 'video' && v) {
    if (v.durationSeconds != null) {
      rows.push([t('gallery.mdDuration'), formatDuration(v.durationSeconds)]);
    }
    if (v.videoCodec) rows.push([t('gallery.mdVideoCodec'), v.videoCodec]);
    if (v.audioCodec) rows.push([t('gallery.mdAudioCodec'), v.audioCodec]);
    if (v.frameRate != null) {
      rows.push([t('gallery.mdFrameRate'), t('gallery.mdFps', { value: v.frameRate.toFixed(2) })]);
    }
    if (v.videoBitrate != null) {
      rows.push([
        t('gallery.mdBitrate'),
        t('gallery.mdMbps', { value: (v.videoBitrate / 1_000_000).toFixed(1) }),
      ]);
    }
    rows.push([
      t('gallery.mdAudio'),
      v.hasAudio ? t('mediaViewer.audioPresent') : t('mediaViewer.noAudio'),
    ]);
    if (v.audioChannels != null) {
      rows.push([t('gallery.mdAudioChannelsLabel'), tn(v.audioChannels, 'gallery.mdAudioChannels')]);
    }
    if (v.audioSampleRate != null) {
      rows.push([t('gallery.mdSampleRate'), t('gallery.mdHz', { value: v.audioSampleRate })]);
    }
    if (v.rotation != null && v.rotation !== 0) {
      rows.push([t('gallery.mdRotation'), `${v.rotation}°`]);
    }
  }

  // --- user annotations -----------------------------------------------------
  if (user.description) rows.push([t('gallery.mdDescription'), user.description]);
  if (user.tags.length > 0) rows.push([t('gallery.mdTags'), user.tags.join(', ')]);
  if (user.rating != null) rows.push([t('gallery.mdRating'), `${user.rating}/5`]);
  if (user.favorite) rows.push([t('gallery.mdFavorite'), t('gallery.yes')]);
  if (effective.location) rows.push([t('gallery.mdLocation'), effective.location]);

  const detected = blob.detectedContentType?.toLowerCase();
  // Baking a DateTaken override into the bytes only applies to a JPEG that
  // actually has an override to write. `kind === 'image'` is the primary gate so
  // a video can never reach it even if its detected type looked image-ish.
  const canWriteDate = kind === 'image' && detected === 'image/jpeg' && user.dateTakenOverride !== null;
  // The stripped-copy download re-encodes on the fly and never mutates the
  // file; it is offered next to the original so the two are not confusable.
  const canPrivacySafe = kind === 'image' && (detected === 'image/jpeg' || detected === 'image/png');
  const canDiscover = kind === 'image' && (onFindSimilarInLibrary || onExploreSimilar);

  const noTechnicalDetails = kind === 'image' ? e === null : v === null;

  return (
    <section className="lightbox-metadata" aria-label={t('mediaMeta.panelAria')}>
      {noTechnicalDetails && (
        <p className="muted">
          {kind === 'video' ? t('mediaMeta.noVideoDetails') : t('gallery.noEmbedded')}
        </p>
      )}
      <dl className="metadata-list">
        {rows.map(([label, value]) => (
          <div key={label} className="metadata-row">
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>

      <div className="metadata-action-groups">
        <MetadataActionGroup title={t('mediaMeta.groupMetadata')}>
          <button type="button" className="metadata-action" data-testid="media-edit-metadata" onClick={onEdit}>
            <Icon name="edit" />
            <span>{t('gallery.editMetadata')}</span>
          </button>
          {canWriteDate && (
            <button
              type="button"
              className="metadata-action"
              data-testid="media-write-datetaken"
              onClick={onWriteDateTaken}
              disabled={writing}
            >
              <Icon name="calendar" />
              <span>{writing ? t('gallery.writing') : t('gallery.writeBtn')}</span>
            </button>
          )}
          {writeError !== null && <p className="metadata-edit-error" role="alert">{writeError}</p>}
        </MetadataActionGroup>

        <MetadataActionGroup title={t('mediaMeta.groupOrganize')}>
          {/* Opens the shared album picker used by the bulk selection bar —
              replacing the inline native select that was unreadable. */}
          <button
            type="button"
            className="metadata-action"
            data-testid="add-to-album-btn"
            onClick={onAddToAlbum}
          >
            <Icon name="album-add" />
            <span>{t('gallery.addToAlbum')}</span>
          </button>
        </MetadataActionGroup>

        {canDiscover && (
          <MetadataActionGroup title={t('mediaMeta.groupDiscover')}>
            {/* Two DIFFERENT destinations, named so the difference is obvious:
                one filters the Library in place, the other opens the dedicated
                explorer. */}
            {onFindSimilarInLibrary && (
              <button
                type="button"
                className="metadata-action"
                data-testid="viewer-find-similar"
                onClick={onFindSimilarInLibrary}
              >
                <Icon name="similar" />
                <span>{t('mediaWs.findSimilarInLibrary')}</span>
              </button>
            )}
            {onExploreSimilar && (
              <button
                type="button"
                className="metadata-action"
                data-testid="viewer-explore-similar"
                onClick={onExploreSimilar}
              >
                <Icon name="explore" />
                <span>{t('mediaWs.exploreSimilar')}</span>
              </button>
            )}
          </MetadataActionGroup>
        )}

        <MetadataActionGroup title={t('mediaMeta.groupFile')}>
          {/* The immutable original blob, under its original file name. Never a
              preview, thumbnail, poster, resized artifact or rewritten copy. */}
          <a
            className="metadata-action"
            href={originalDownloadUrl(data.id)}
            data-testid="download-original"
          >
            <Icon name="download" />
            <span>{t('gallery.downloadOriginal')}</span>
          </a>
          {canPrivacySafe && (
            <a
              className="metadata-action"
              href={privacySafeDownloadUrl(data.id)}
              data-testid="privacy-safe-download"
            >
              <Icon name="download" />
              <span>{t('gallery.downloadPrivacySafe')}</span>
            </a>
          )}
        </MetadataActionGroup>
      </div>
    </section>
  );
}

function MetadataActionGroup({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="metadata-action-group">
      <h4 className="metadata-action-group__title">{title}</h4>
      <div className="metadata-action-group__items">{children}</div>
    </div>
  );
}
