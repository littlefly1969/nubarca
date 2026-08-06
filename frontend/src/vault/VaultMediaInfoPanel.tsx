import { useEffect, useState } from 'react';
import { ApiError, getVaultMediaInfo, type VaultFile, type VaultMediaInfo } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { formatDate, formatSize } from '../components/format';

// Read-only sanitized detail for a vault file. Fetches the sanitized info DTO
// (no storage path / blob id / hash / embedding / face data) and renders the
// curated fields. There is NO metadata editing in this slice and NO request to
// the normal `/api/files` endpoints (which are unauthorized for vault content).

export function VaultMediaInfoPanel({
  token,
  file,
  onExpired,
}: {
  token: string;
  file: VaultFile;
  onExpired: () => void;
}) {
  const { t } = useI18n();
  const [info, setInfo] = useState<VaultMediaInfo | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setStatus('loading');
    setInfo(null);
    getVaultMediaInfo(token, file.id, controller.signal)
      .then((data) => {
        if (cancelled) return;
        setInfo(data);
        setStatus('ready');
      })
      .catch((err: unknown) => {
        if (cancelled || controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) {
          onExpired();
          return;
        }
        setStatus('error');
      });
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [token, file.id, onExpired]);

  const kindLabel =
    file.mediaKind === 'image'
      ? t('vault.badgePhoto')
      : file.mediaKind === 'video'
        ? t('vault.badgeVideo')
        : t('vault.badgeFile');

  return (
    <aside className="vault-info" aria-label={t('vault.detailsTitle')}>
      <h3 className="vault-info-title">{t('vault.detailsTitle')}</h3>
      {status === 'loading' && <p className="muted">{t('common.loading')}</p>}
      {status === 'error' && (
        <p className="folder-error" role="alert">
          {t('vault.loadError')}
        </p>
      )}
      {status === 'ready' && info && (
        <dl className="vault-info-list">
          {info.title && (
            <Row label={t('vault.fieldTitle')} value={info.title} />
          )}
          <Row label={t('vault.fieldFilename')} value={info.name} />
          <Row label={t('vault.fieldType')} value={`${kindLabel} · ${info.mimeType}`} />
          {info.width != null && info.height != null && (
            <Row label={t('vault.fieldDimensions')} value={`${info.width} × ${info.height}`} />
          )}
          <Row label={t('vault.fieldSize')} value={formatSize(info.sizeBytes)} />
          <Row label={t('vault.fieldDate')} value={formatDate(info.createdAt)} />
          {info.takenAt && <Row label={t('vault.fieldTaken')} value={formatDate(info.takenAt)} />}
          {info.description && <Row label={t('vault.fieldDescription')} value={info.description} />}
          {info.tags.length > 0 && (
            <Row label={t('vault.fieldTags')} value={info.tags.join(', ')} />
          )}
          {info.rating != null && (
            <Row label={t('vault.fieldRating')} value={'★'.repeat(info.rating)} />
          )}
          <Row
            label={t('vault.fieldFavorite')}
            value={info.favorite ? t('vault.favoriteYes') : t('vault.favoriteNo')}
          />
          {info.location && <Row label={t('vault.fieldLocation')} value={info.location} />}
        </dl>
      )}
    </aside>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="vault-info-row">
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}
