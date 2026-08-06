import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  createShareLink,
  listShareLinksForFile,
  revokeShareLink,
  type CreateShareLinkOptions,
  type ShareLinkSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';

interface ShareLinkPanelProps {
  fileId: string;
  fileName: string;
  onClose(): void;
  // Called when the file vanished server-side (404); the parent reloads the
  // folder and shows the banner so this row's panel can close cleanly.
  onFileMissing(): void;
}

// State machine for the bottom area: creating a new link, holding the
// just-shown URL, revoking it, etc. Independent from the existing-links
// listing above so the user can keep browsing history while the create flow
// is in progress.
type PanelState =
  | { kind: 'idle' }
  | { kind: 'creating' }
  | {
      kind: 'created';
      id: string;
      url: string;
      expiresAt: string | null;
      maxDownloads: number | null;
      copy: 'idle' | 'copied' | 'failed';
    }
  | { kind: 'revoking'; url: string }
  | { kind: 'revoked'; url: string }
  | { kind: 'error'; message: string };

type ListState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; items: ShareLinkSummary[] };

type StatusKind = 'active' | 'revoked' | 'expired' | 'exhausted';

function statusOf(link: ShareLinkSummary): StatusKind {
  // Precedence: an explicit revocation overrides natural expiry / exhaustion.
  if (link.isRevoked) return 'revoked';
  if (link.isExpired) return 'expired';
  if (link.isExhausted) return 'exhausted';
  return 'active';
}

const STATUS_LABEL_KEY: Record<StatusKind, MessageKey> = {
  active: 'shares.statusActive',
  revoked: 'shares.statusRevoked',
  expired: 'shares.statusExpired',
  exhausted: 'shares.statusExhausted',
};

// Render-only helper to turn the backend's relative `/s/{token}` URL into an
// absolute same-origin URL that's actually copy-pastable from clipboard.
function absoluteFromRelative(relative: string): string {
  if (typeof window === 'undefined') return relative;
  return new URL(relative, window.location.origin).toString();
}

// Convert a `datetime-local` input value (interpreted as the user's local
// timezone) to an ISO 8601 UTC string for the backend. Returns null when the
// input is empty or unparseable; the caller validates further.
function localInputToIsoUtc(localValue: string): string | null {
  if (localValue.trim().length === 0) return null;
  const d = new Date(localValue);
  if (Number.isNaN(d.getTime())) return null;
  return d.toISOString();
}

// `ShareLinkPanel` owns three things in component state:
//
//   * the just-created link's raw URL + id (transient — never persisted);
//   * the user-controlled inputs for the next create (expires / maxDownloads);
//   * the loaded history of existing links for this file.
//
// The raw token is shown ONCE at creation time and never re-rendered from
// memory or storage afterwards. Existing links from the listing endpoint do
// not carry the raw token; they render with metadata and a Revoke button
// only.
export function ShareLinkPanel({ fileId, fileName, onClose, onFileMissing }: ShareLinkPanelProps) {
  const { invalidateAuth } = useAuth();
  const { t, formatDate } = useI18n();
  const [state, setState] = useState<PanelState>({ kind: 'idle' });
  const [expiresAtLocal, setExpiresAtLocal] = useState('');
  const [maxDownloadsInput, setMaxDownloadsInput] = useState('');
  const [validationError, setValidationError] = useState<string | null>(null);
  const [listState, setListState] = useState<ListState>({ kind: 'loading' });
  const [revokingId, setRevokingId] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const reload = useCallback(
    (signal?: AbortSignal) => {
      setListState({ kind: 'loading' });
      return listShareLinksForFile(fileId, signal)
        .then((items) => {
          setListState({ kind: 'ready', items });
        })
        .catch((err: unknown) => {
          if (err instanceof DOMException && err.name === 'AbortError') return;
          if (err instanceof ApiError && err.status === 401) {
            invalidateAuth();
            return;
          }
          if (err instanceof ApiError && err.status === 404) {
            onFileMissing();
            return;
          }
          setListState({
            kind: 'error',
            message: t('share.loadExistingError'),
          });
        });
    },
    [fileId, invalidateAuth, onFileMissing, t],
  );

  useEffect(() => {
    const controller = new AbortController();
    void reload(controller.signal);
    return () => controller.abort();
  }, [reload]);

  async function onCreate() {
    setValidationError(null);
    const options: CreateShareLinkOptions = {};

    if (expiresAtLocal.trim().length > 0) {
      const iso = localInputToIsoUtc(expiresAtLocal);
      if (iso === null) {
        setValidationError(t('share.expiryInvalid'));
        return;
      }
      if (new Date(iso).getTime() <= Date.now()) {
        setValidationError(t('share.expiryFuture'));
        return;
      }
      options.expiresAt = iso;
    }

    if (maxDownloadsInput.trim().length > 0) {
      const n = Number(maxDownloadsInput);
      if (!Number.isFinite(n) || !Number.isInteger(n) || n <= 0) {
        setValidationError(t('share.maxDownloadsInvalid'));
        return;
      }
      options.maxDownloads = n;
    }

    setState({ kind: 'creating' });
    try {
      const link = await createShareLink(fileId, options);
      setState({
        kind: 'created',
        id: link.id,
        url: absoluteFromRelative(link.url),
        expiresAt: link.expiresAt,
        maxDownloads: link.maxDownloads,
        copy: 'idle',
      });
      // Refresh history so the new link appears in the existing-links list
      // (without its raw token — the URL above is the only place it's shown).
      void reload();
    } catch (err) {
      handleError(err);
    }
  }

  async function onCopy() {
    if (state.kind !== 'created') return;
    try {
      // navigator.clipboard requires a secure context (https or localhost).
      // If unavailable / blocked, we fall through to the manual-select hint.
      await navigator.clipboard.writeText(state.url);
      setState({ ...state, copy: 'copied' });
    } catch {
      setState({ ...state, copy: 'failed' });
      inputRef.current?.select();
    }
  }

  async function onRevokeJustCreated() {
    if (state.kind !== 'created') return;
    const { id, url } = state;
    setState({ kind: 'revoking', url });
    try {
      await revokeShareLink(id);
      setState({ kind: 'revoked', url });
      void reload();
    } catch (err) {
      handleError(err);
    }
  }

  async function onRevokeExisting(id: string) {
    const ok = window.confirm(t('share.confirmRevoke'));
    if (!ok) return;
    setRevokingId(id);
    try {
      await revokeShareLink(id);
      await reload();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
      } else if (err instanceof ApiError && err.status === 404) {
        // Either the link was already gone, or the file vanished. Refresh —
        // if the file is gone the next reload will 404 and bubble up.
        await reload();
      } else {
        setListState({
          kind: 'error',
          message: t('share.revokeError'),
        });
      }
    } finally {
      setRevokingId(null);
    }
  }

  function handleError(err: unknown) {
    if (err instanceof ApiError) {
      if (err.status === 401) {
        invalidateAuth();
        return;
      }
      if (err.status === 404) {
        onFileMissing();
        return;
      }
      if (err.status === 400) {
        const fromBody =
          typeof err.body === 'object' && err.body !== null && 'error' in err.body
            ? (err.body as { error?: unknown }).error
            : undefined;
        setState({
          kind: 'error',
          message:
            typeof fromBody === 'string' && fromBody.length > 0
              ? fromBody
              : t('share.requestRejected'),
        });
        return;
      }
    }
    setState({
      kind: 'error',
      message: t('share.serverError'),
    });
  }

  return (
    <div className="share-panel" role="group" aria-label={t('share.groupAria', { name: fileName })}>
      <ExistingLinksSection
        state={listState}
        revokingId={revokingId}
        onRevoke={onRevokeExisting}
        onRetry={() => void reload()}
      />

      <div className="share-panel-divider" aria-hidden="true" />

      {state.kind === 'idle' && (
        <>
          <div className="share-panel-row">
            <span className="muted share-panel-hint">
              {t('share.createHintPre')}<em>{fileName}</em>{t('share.createHintPost')}
            </span>
          </div>
          <p
            className="muted share-panel-warning"
            role="note"
            data-testid="share-metadata-warning"
          >
            {t('share.metadataWarning')}
          </p>
          <div className="share-panel-options">
            <label className="share-panel-field">
              <span className="share-panel-field-label">{t('share.expiresOptional')}</span>
              <input
                type="datetime-local"
                value={expiresAtLocal}
                onChange={(e) => setExpiresAtLocal(e.target.value)}
                className="share-panel-field-input"
                aria-describedby="share-panel-expiry-hint"
              />
            </label>
            <label className="share-panel-field">
              <span className="share-panel-field-label">
                {t('share.maxDownloadsOptional')}
              </span>
              <input
                type="number"
                min={1}
                step={1}
                inputMode="numeric"
                value={maxDownloadsInput}
                onChange={(e) => setMaxDownloadsInput(e.target.value)}
                placeholder={t('share.unlimitedPlaceholder')}
                className="share-panel-field-input share-panel-field-input-number"
              />
            </label>
          </div>
          <p id="share-panel-expiry-hint" className="muted share-panel-status">
            {t('share.bothEmptyHint')}
          </p>
          {validationError !== null && (
            <p className="row-inline-error" role="alert">
              {validationError}
            </p>
          )}
          <div className="share-panel-row">
            <button
              type="button"
              className="row-action-primary"
              onClick={() => void onCreate()}
            >
              {t('share.createLink')}
            </button>
            <button type="button" className="row-action" onClick={onClose}>
              {t('common.cancel')}
            </button>
          </div>
        </>
      )}

      {state.kind === 'creating' && (
        <div className="share-panel-row">
          <span className="muted">{t('share.creating')}</span>
        </div>
      )}

      {state.kind === 'created' && (
        <>
          <div className="share-panel-row">
            <input
              ref={inputRef}
              type="text"
              readOnly
              value={state.url}
              className="share-panel-url"
              aria-label={t('share.urlAria')}
              onFocus={(e) => e.currentTarget.select()}
            />
            <button
              type="button"
              className="row-action-primary"
              onClick={() => void onCopy()}
            >
              {t('share.copy')}
            </button>
            <button
              type="button"
              className="row-action row-action-destructive"
              onClick={() => void onRevokeJustCreated()}
            >
              {t('shares.revoke')}
            </button>
            <button type="button" className="row-action" onClick={onClose}>
              {t('common.close')}
            </button>
          </div>
          {(state.expiresAt !== null || state.maxDownloads !== null) && (
            <p className="muted share-panel-status" role="status">
              {state.expiresAt !== null && (
                <>{t('share.expiresLine', { date: formatDate(state.expiresAt) })}</>
              )}
              {state.expiresAt !== null && state.maxDownloads !== null && (
                <> · </>
              )}
              {state.maxDownloads !== null && (
                <>{t('share.maxDownloadsLine', { n: state.maxDownloads })}</>
              )}
            </p>
          )}
          {state.copy === 'copied' && (
            <p className="muted share-panel-status" role="status">
              {t('share.copied')}
            </p>
          )}
          {state.copy === 'failed' && (
            <p className="muted share-panel-status">
              {t('share.copyFailed')}
            </p>
          )}
          <p className="muted share-panel-status">
            {t('share.wontShowAgain')}
          </p>
        </>
      )}

      {state.kind === 'revoking' && (
        <div className="share-panel-row">
          <span className="muted">{t('share.revoking')}</span>
        </div>
      )}

      {state.kind === 'revoked' && (
        <div className="share-panel-row">
          <span className="muted">
            {t('share.revokedLine')}
          </span>
          <button type="button" className="row-action" onClick={onClose}>
            {t('common.close')}
          </button>
        </div>
      )}

      {state.kind === 'error' && (
        <div className="share-panel-row">
          <span className="row-inline-error" role="alert">
            {state.message}
          </span>
          <button type="button" className="row-action" onClick={onClose}>
            {t('common.close')}
          </button>
        </div>
      )}
    </div>
  );
}

interface ExistingLinksSectionProps {
  state: ListState;
  revokingId: string | null;
  onRevoke(id: string): void;
  onRetry(): void;
}

function ExistingLinksSection({
  state,
  revokingId,
  onRevoke,
  onRetry,
}: ExistingLinksSectionProps) {
  const { t, formatDate } = useI18n();
  if (state.kind === 'loading') {
    return (
      <p className="muted share-panel-status" role="status">
        {t('share.loadingExisting')}
      </p>
    );
  }

  if (state.kind === 'error') {
    return (
      <div className="share-panel-row">
        <span className="row-inline-error" role="alert">
          {state.message}
        </span>
        <button type="button" className="row-action" onClick={onRetry}>
          {t('common.tryAgain')}
        </button>
      </div>
    );
  }

  if (state.items.length === 0) {
    return (
      <p className="muted share-panel-status">
        {t('share.noExisting')}
      </p>
    );
  }

  return (
    <ul
      className="share-existing-list"
      aria-label={t('share.existingAria')}
    >
      {state.items.map((link) => {
        const status = statusOf(link);
        const canRevoke = status === 'active';
        const isBusy = revokingId === link.id;
        return (
          <li
            key={link.id}
            className={`share-existing-row share-existing-row-${status}`}
          >
            <div className="share-existing-main">
              <span className="share-existing-name">
                {t('share.createdLine', { date: formatDate(link.createdAt) })}
              </span>
              <span
                className={`share-status-badge share-status-${status}`}
              >
                {t(STATUS_LABEL_KEY[status])}
              </span>
            </div>
            <div className="share-existing-meta">
              <span>
                {t('share.downloadsLine', { count: link.downloadCount })}
                {link.maxDownloads !== null ? ` / ${link.maxDownloads}` : ''}
              </span>
              {link.expiresAt !== null && (
                <span>{t('share.expiresLine', { date: formatDate(link.expiresAt) })}</span>
              )}
              {link.lastAccessedAt !== null && (
                <span>
                  {t('share.lastAccessed', { date: formatDate(link.lastAccessedAt) })}
                </span>
              )}
            </div>
            {canRevoke && (
              <div className="share-existing-actions">
                <button
                  type="button"
                  className="row-action row-action-destructive"
                  onClick={() => onRevoke(link.id)}
                  disabled={isBusy}
                >
                  {isBusy ? t('shares.revoking') : t('shares.revoke')}
                </button>
              </div>
            )}
          </li>
        );
      })}
    </ul>
  );
}
