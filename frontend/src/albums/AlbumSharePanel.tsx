import { useCallback, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import {
  ApiError,
  ASSIGNABLE_ALBUM_ROLES,
  inviteAlbumMember,
  listAlbumMembers,
  resolveAlbumRecipient,
  revokeAlbumMember,
  setAlbumMemberDownload,
  setAlbumMemberRole,
  type AlbumMember,
  type AssignableAlbumRole,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';

// SHARE-ALBUM-01: the OWNER's "who is this album shared with" panel.
//
// Inviting is deliberately TWO steps. Step 1 resolves an exact email to a
// display name so the owner can see who they are about to share with before
// anything is sent; step 2 sends. NubArca has no public handle — the account's
// email is the only unique identifier — so there is no browsable directory and
// no prefix search to offer here, by design.
//
// Nothing this panel renders is a security control. The server re-checks album
// ownership on every call and re-checks the member's grant on every request
// they make; hiding a control is UX.

interface Props {
  albumId: string;
  albumName: string;
  onClose(): void;
  returnFocusRef?: React.RefObject<HTMLButtonElement | null>;
}

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; members: AlbumMember[] }
  | { kind: 'error'; message: string };

// The invite flow's own small state machine, kept separate from the member list
// so a failed lookup never blanks the people already invited.
type InviteStep =
  | { kind: 'idle' }
  | { kind: 'resolving' }
  | { kind: 'confirm'; displayName: string; email: string }
  | { kind: 'sending'; displayName: string; email: string };

export function AlbumSharePanel({ albumId, albumName, onClose, returnFocusRef }: Props) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const dialogRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [email, setEmail] = useState('');
  const [allowDownload, setAllowDownload] = useState(false);
  // SHARE-ALBUM-02. The options come from ASSIGNABLE_ALBUM_ROLES, which
  // excludes `editor` by type — so the value cannot appear in this menu, in a
  // payload, or as a keyboard-reachable option, even by mistake. The backend
  // refuses it regardless; this is UX, not the gate.
  const [inviteRole, setInviteRole] = useState<AssignableAlbumRole>('viewer');
  const [step, setStep] = useState<InviteStep>({ kind: 'idle' });
  const [inviteError, setInviteError] = useState<string | null>(null);
  const [busyMembership, setBusyMembership] = useState<string | null>(null);
  const [memberError, setMemberError] = useState<string | null>(null);

  const load = useCallback(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    listAlbumMembers(albumId, ctrl.signal)
      .then((members) => setStatus({ kind: 'ready', members }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus({ kind: 'error', message: t('albumShare.loadError') });
      });
  }, [albumId, invalidateAuth, t]);

  useEffect(() => {
    load();
    return () => abortRef.current?.abort();
  }, [load]);

  useEffect(() => {
    dialogRef.current?.querySelector<HTMLElement>('input, button')?.focus();
    return () => returnFocusRef?.current?.focus();
  }, [returnFocusRef]);

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') { e.stopPropagation(); onClose(); return; }
    if (e.key !== 'Tab') return;
    const list = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(
      'input, textarea, button, a[href], [tabindex]:not([tabindex="-1"])',
    ) ?? []);
    if (list.length === 0) return;
    const first = list[0], last = list[list.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  }

  async function resolve() {
    const trimmed = email.trim();
    if (!trimmed) { setInviteError(t('albumShare.emailRequired')); return; }
    setStep({ kind: 'resolving' });
    setInviteError(null);
    try {
      const { displayName } = await resolveAlbumRecipient(albumId, trimmed);
      setStep({ kind: 'confirm', displayName, email: trimmed });
    } catch (err) {
      setStep({ kind: 'idle' });
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // Unknown address, disabled account and the owner's own address all land
      // here with the same message — the server does not distinguish them and
      // neither does this.
      if (err instanceof ApiError && err.status === 404) {
        setInviteError(t('albumShare.recipientUnavailable'));
        return;
      }
      setInviteError(t('albumShare.resolveError'));
    }
  }

  async function send(displayName: string, recipientEmail: string) {
    setStep({ kind: 'sending', displayName, email: recipientEmail });
    setInviteError(null);
    try {
      await inviteAlbumMember(albumId, recipientEmail, {
        role: inviteRole,
        allowOriginalDownload: allowDownload,
      });
      setEmail('');
      setAllowDownload(false);
      setInviteRole('viewer');
      setStep({ kind: 'idle' });
      load();
    } catch (err) {
      setStep({ kind: 'confirm', displayName, email: recipientEmail });
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 409) {
        setInviteError(t('albumShare.alreadyInvited'));
        return;
      }
      if (err instanceof ApiError && err.status === 404) {
        setInviteError(t('albumShare.recipientUnavailable'));
        return;
      }
      if (err instanceof ApiError && err.status === 400) {
        setInviteError(t('albumShare.selfOrInvalid'));
        return;
      }
      setInviteError(t('albumShare.inviteError'));
    }
  }

  // Confirmations and screen-reader labels name the member the way the list
  // does — display name plus the masked hint — so an owner with two identically
  // named members is never asked to confirm an ambiguous action.
  // One mapping for all three roles. A ternary here silently rendered `editor`
  // with the Contributor label — the option VALUE was right, so only a label
  // assertion (or a browser) catches it.
  function roleLabel(role: AssignableAlbumRole): string {
    if (role === 'editor') return t('albumRole.editor');
    if (role === 'contributor') return t('albumRole.contributor');
    return t('albumRole.viewer');
  }

  function roleHelp(role: AssignableAlbumRole): string {
    if (role === 'editor') return t('albumRole.editorHelp');
    if (role === 'contributor') return t('albumRole.contributorHelp');
    return t('albumRole.viewerHelp');
  }

  function memberLabel(member: AlbumMember): string {
    return member.maskedEmail
      ? `${member.displayName} (${member.maskedEmail})`
      : member.displayName;
  }

  async function changeRole(member: AlbumMember, next: AssignableAlbumRole) {
    setBusyMembership(member.membershipId);
    setMemberError(null);
    try {
      await setAlbumMemberRole(albumId, member.membershipId, next);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // 404 means the membership is gone (revoked elsewhere, album deleted).
      // Reload rather than leave a control the server no longer honours.
      if (err instanceof ApiError && err.status === 404) { load(); return; }
      setMemberError(t('albumShare.roleChangeError'));
    } finally {
      setBusyMembership(null);
    }
  }

  async function toggleDownload(member: AlbumMember, next: boolean) {
    setBusyMembership(member.membershipId);
    try {
      await setAlbumMemberDownload(albumId, member.membershipId, next);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setBusyMembership(null);
    }
  }

  async function revoke(member: AlbumMember) {
    const question = member.state === 'pending'
      ? t('albumShare.confirmCancel', { name: memberLabel(member) })
      : t('albumShare.confirmRevoke', { name: memberLabel(member) });
    if (!window.confirm(question)) return;
    setBusyMembership(member.membershipId);
    try {
      await revokeAlbumMember(albumId, member.membershipId);
      load();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally {
      setBusyMembership(null);
    }
  }

  // Revoked and declined rows are history, not access. They stay visible so the
  // owner can see they once shared with somebody (and re-invite), but they are
  // grouped apart from the people who actually have access.
  const active = status.kind === 'ready'
    ? status.members.filter((m) => m.state === 'pending' || m.state === 'accepted')
    : [];
  const past = status.kind === 'ready'
    ? status.members.filter((m) => m.state === 'declined' || m.state === 'revoked')
    : [];

  return (
    <div
      className="ws-sheet-backdrop"
      data-testid="album-share-backdrop"
      onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}
    >
      <div
        ref={dialogRef}
        className="ws-sheet album-share-panel"
        role="dialog"
        aria-modal="true"
        aria-label={t('albumShare.title', { name: albumName })}
        data-testid="album-share-panel"
        onKeyDown={onKeyDown}
      >
        <header className="ws-sheet-head">
          <h2 className="ws-sheet-title">{t('albumShare.title', { name: albumName })}</h2>
          <button
            type="button"
            className="ws-icon-button"
            aria-label={t('common.close')}
            data-testid="album-share-close"
            onClick={onClose}
          >
            ✕
          </button>
        </header>

        <div className="ws-sheet-body">
          <fieldset className="ws-filter-section">
            <legend>{t('albumShare.inviteLegend')}</legend>
            <p className="muted">{t('albumShare.inviteHelp')}</p>

            <label>
              {t('albumShare.emailLabel')}
              <input
                type="email"
                inputMode="email"
                autoComplete="off"
                data-testid="album-share-email"
                value={email}
                placeholder={t('albumShare.emailPlaceholder')}
                disabled={step.kind === 'resolving' || step.kind === 'sending'}
                onChange={(e) => {
                  setEmail(e.target.value);
                  if (step.kind === 'confirm') setStep({ kind: 'idle' });
                  setInviteError(null);
                }}
                onKeyDown={(e) => { if (e.key === 'Enter' && step.kind === 'idle') void resolve(); }}
              />
            </label>

            <label>
              {t('albumShare.roleLabel')}
              <select
                data-testid="album-share-role"
                value={inviteRole}
                disabled={step.kind === 'sending'}
                onChange={(e) => setInviteRole(e.target.value as AssignableAlbumRole)}
              >
                {ASSIGNABLE_ALBUM_ROLES.map((role) => (
                  <option key={role} value={role}>
                    {roleLabel(role)}{' — '}{roleHelp(role)}
                  </option>
                ))}
              </select>
            </label>

            <label className="album-tv-label">
              <input
                type="checkbox"
                data-testid="album-share-allow-download"
                checked={allowDownload}
                disabled={step.kind === 'sending'}
                onChange={(e) => setAllowDownload(e.target.checked)}
              />
              <span>{t('albumShare.allowDownload')}</span>
            </label>
            <p className="muted">{t('albumShare.allowDownloadHelp')}</p>
            <p className="muted">{t('albumShare.revokeDownloadNote')}</p>

            {inviteError && <p className="inline-error" role="alert">{inviteError}</p>}

            {step.kind === 'confirm' || step.kind === 'sending' ? (
              <div className="album-share-confirm" data-testid="album-share-confirm">
                <p>{t('albumShare.confirmRecipient', { name: step.displayName })}</p>
                <div className="album-share-confirm-actions">
                  <button
                    type="button"
                    className="row-action-primary"
                    data-testid="album-share-send"
                    disabled={step.kind === 'sending'}
                    onClick={() => void send(step.displayName, step.email)}
                  >
                    {step.kind === 'sending' ? t('albumShare.sending') : t('albumShare.send')}
                  </button>
                  <button
                    type="button"
                    className="row-action"
                    disabled={step.kind === 'sending'}
                    onClick={() => setStep({ kind: 'idle' })}
                  >
                    {t('common.cancel')}
                  </button>
                </div>
              </div>
            ) : (
              <button
                type="button"
                className="row-action-primary"
                data-testid="album-share-resolve"
                disabled={step.kind === 'resolving' || email.trim().length === 0}
                onClick={() => void resolve()}
              >
                {step.kind === 'resolving' ? t('albumShare.checking') : t('albumShare.check')}
              </button>
            )}
          </fieldset>

          <fieldset className="ws-filter-section">
            <legend>{t('albumShare.membersLegend')}</legend>

            {status.kind === 'loading' && <p>{t('common.loading')}</p>}
            {status.kind === 'error' && (
              <p className="inline-error" role="alert">{status.message}</p>
            )}
            {memberError && <p className="inline-error" role="alert">{memberError}</p>}

            {status.kind === 'ready' && active.length === 0 && (
              <p className="empty-state" data-testid="album-share-empty">
                {t('albumShare.noMembers')}
              </p>
            )}

            {status.kind === 'ready' && active.length > 0 && (
              <ul className="album-share-members" data-testid="album-share-members">
                {active.map((member) => (
                  <li key={member.membershipId} className="album-share-member" data-testid="album-share-member">
                    <div className="album-share-member-identity">
                      <span className="album-share-member-name">{member.displayName}</span>
                      {/* Display names are not unique. The masked address is
                          what lets the owner tell two identically-named members
                          apart before revoking one of them. */}
                      {member.maskedEmail && (
                        <span className="album-share-member-hint" data-testid="album-share-hint">
                          {member.maskedEmail}
                        </span>
                      )}
                      <span
                        className={`album-badge album-share-state album-share-state-${member.state}`}
                        data-testid="album-share-state"
                      >
                        {member.state === 'pending'
                          ? t('albumShare.statePending')
                          : t('albumShare.stateAccepted')}
                      </span>
                      <label className="album-share-member-role">
                        <span className="visually-hidden">
                          {t('albumShare.changeRoleAria', { name: memberLabel(member) })}
                        </span>
                        <select
                          data-testid="album-share-member-role"
                          value={ASSIGNABLE_ALBUM_ROLES.includes(member.role)
                            ? member.role : 'viewer'}
                          disabled={busyMembership === member.membershipId}
                          aria-label={t('albumShare.changeRoleAria', { name: memberLabel(member) })}
                          onChange={(e) =>
                            void changeRole(member, e.target.value as AssignableAlbumRole)}
                        >
                          {ASSIGNABLE_ALBUM_ROLES.map((role) => (
                            <option key={role} value={role}>
                              {roleLabel(role)}
                            </option>
                          ))}
                        </select>
                      </label>
                    </div>
                    <p className="muted album-share-member-when">
                      {member.state === 'accepted' && member.acceptedAt
                        ? t('albumShare.acceptedAt', { date: formatDate(member.acceptedAt) })
                        : t('albumShare.invitedAt', { date: formatDate(member.invitedAt) })}
                    </p>
                    <label className="album-tv-label">
                      <input
                        type="checkbox"
                        checked={member.allowOriginalDownload}
                        disabled={busyMembership === member.membershipId}
                        aria-label={t('albumShare.memberDownloadAria', { name: memberLabel(member) })}
                        onChange={(e) => void toggleDownload(member, e.target.checked)}
                      />
                      <span>{t('albumShare.allowDownload')}</span>
                    </label>
                    <button
                      type="button"
                      className="btn-danger"
                      data-testid="album-share-revoke"
                      disabled={busyMembership === member.membershipId}
                      onClick={() => void revoke(member)}
                    >
                      {member.state === 'pending' ? t('albumShare.cancel') : t('albumShare.revoke')}
                    </button>
                  </li>
                ))}
              </ul>
            )}

            {status.kind === 'ready' && past.length > 0 && (
              <details className="album-share-past" data-testid="album-share-past">
                <summary>{t('albumShare.pastLegend', { count: past.length })}</summary>
                <ul className="album-share-members">
                  {past.map((member) => (
                    <li key={member.membershipId} className="album-share-member album-share-member-past">
                      <span className="album-share-member-name">{member.displayName}</span>
                      {member.maskedEmail && (
                        <span className="album-share-member-hint">{member.maskedEmail}</span>
                      )}
                      <span className="album-badge">
                        {member.state === 'declined'
                          ? t('albumShare.stateDeclined')
                          : t('albumShare.stateRevoked')}
                      </span>
                    </li>
                  ))}
                </ul>
              </details>
            )}
          </fieldset>
        </div>
      </div>
    </div>
  );
}
