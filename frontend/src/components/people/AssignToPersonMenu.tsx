import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ApiError,
  assignFace,
  ignoreFace,
  removeFaceAssignment,
  type Person,
} from '@nubarca/api-client';
import { useI18n } from '../../i18n';

// Owner-private "Assign to person…" control for a single face. Reusable from face
// chips, the unassigned view, person detail, and the context viewer. Copy is in
// Italian to match the rest of the People UI.
//
// The dialog is rendered through a PORTAL to <body> as a centered modal (above the
// context viewer's z-index 1100), so it is always fully visible and operable — even
// when the trigger sits near the bottom of the screen or inside the viewer's
// overflow container. Escape closes it (without also closing the viewer), focus
// starts on the search input and is trapped within the modal.
//
// Behaviour:
//   * unassigned  → search existing / create new.
//   * assigned    → shows "Già assegnato a: <name>", move (search/create), and
//                   "Rimuovi dalla persona".
// Emits onChanged(personId | null) after a successful assign/move/remove/ignore.
export function AssignToPersonMenu({
  faceId,
  people,
  currentPersonId = null,
  currentPersonName = null,
  allowIgnore = false,
  onChanged,
  invalidateAuth,
}: {
  faceId: string;
  people: Person[];
  currentPersonId?: string | null;
  currentPersonName?: string | null;
  allowIgnore?: boolean;
  onChanged: (personId: string | null) => void;
  invalidateAuth?: () => void;
}) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const searchRef = useRef<HTMLInputElement | null>(null);

  const close = useCallback(() => {
    setOpen(false);
    setQuery('');
    setNewName('');
  }, []);

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    const list = people.filter((p) => p.personId !== currentPersonId);
    if (q.length === 0) return list.slice(0, 8);
    return list.filter((p) => (p.name ?? '').toLowerCase().includes(q)).slice(0, 8);
  }, [people, query, currentPersonId]);

  // Escape (capture phase) closes only this modal and stops the event so an outer
  // viewer's window Escape handler does not also fire. Also focus the search input.
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        e.stopPropagation();
        close();
      }
    }
    window.addEventListener('keydown', onKey, true);
    const id = window.setTimeout(() => searchRef.current?.focus(), 0);
    return () => {
      window.removeEventListener('keydown', onKey, true);
      window.clearTimeout(id);
    };
  }, [open, close]);

  async function run(fn: () => Promise<unknown>, resultPersonId: string | null) {
    setBusy(true);
    try {
      await fn();
      close();
      onChanged(resultPersonId);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth?.();
    } finally {
      setBusy(false);
    }
  }

  // Minimal focus trap: keep Tab within the modal.
  function onDialogKeyDown(e: React.KeyboardEvent) {
    if (e.key !== 'Tab' || !dialogRef.current) return;
    const focusable = dialogRef.current.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    if (focusable.length === 0) return;
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  }

  const triggerLabel = currentPersonId ? t('face.moveOrRemove') : t('face.assignToPerson');

  return (
    <div className="assign-menu">
      <button
        type="button"
        className="assign-menu-trigger"
        aria-label={triggerLabel}
        aria-haspopup="dialog"
        aria-expanded={open}
        disabled={busy}
        onClick={() => setOpen(true)}
      >
        {currentPersonId ? t('face.move') : t('face.assignToPersonEllipsis')}
      </button>

      {open &&
        createPortal(
          <div className="assign-modal-backdrop" onClick={close}>
            <div
              className="assign-modal"
              role="dialog"
              aria-modal="true"
              aria-label={t('face.assignToPerson')}
              ref={dialogRef}
              onClick={(e) => e.stopPropagation()}
              onKeyDown={onDialogKeyDown}
            >
              <h3 className="assign-modal-title">{currentPersonId ? t('face.moveOrRemoveFace') : t('face.assignToPerson')}</h3>
              {currentPersonName && (
                <p className="assign-modal-current muted">
                  {t('face.alreadyAssignedTo')} <strong>{currentPersonName}</strong>
                </p>
              )}

              <label className="assign-modal-field">
                <span className="visually-hidden">{t('face.searchPerson')}</span>
                <input
                  type="text"
                  placeholder={t('face.searchPerson')}
                  aria-label={t('face.searchPerson')}
                  value={query}
                  disabled={busy}
                  ref={searchRef}
                  onChange={(e) => setQuery(e.target.value)}
                />
              </label>

              <ul className="assign-modal-list">
                {matches.length === 0 && query.trim().length > 0 ? (
                  <li className="muted assign-modal-empty">{t('face.noPersonFound')}</li>
                ) : (
                  matches.map((p) => (
                    <li key={p.personId}>
                      <button
                        type="button"
                        className="assign-modal-person"
                        disabled={busy}
                        onClick={() => void run(() => assignFace(faceId, { personId: p.personId }), p.personId)}
                      >
                        {p.name ?? t('people.unnamed')}
                      </button>
                    </li>
                  ))
                )}
              </ul>

              <form
                className="assign-modal-create"
                onSubmit={(e) => {
                  e.preventDefault();
                  const name = newName.trim();
                  if (name.length > 0) void run(() => assignFace(faceId, { name }), null);
                }}
              >
                <input
                  type="text"
                  placeholder={t('face.createNewPerson')}
                  aria-label={t('face.createNewPerson')}
                  value={newName}
                  disabled={busy}
                  onChange={(e) => setNewName(e.target.value)}
                />
                <button type="submit" disabled={busy || newName.trim().length === 0}>{t('face.createAndAssign')}</button>
              </form>

              <div className="assign-modal-actions">
                {currentPersonId && (
                  <button
                    type="button"
                    className="assign-modal-remove"
                    disabled={busy}
                    title={t('face.removeHelp')}
                    onClick={() => void run(() => removeFaceAssignment(faceId), null)}
                  >
                    {t('face.removeFromPerson')}
                  </button>
                )}
                {allowIgnore && (
                  <button
                    type="button"
                    className="assign-modal-ignore"
                    disabled={busy}
                    onClick={() => void run(() => ignoreFace(faceId), null)}
                  >
                    {t('face.ignoreFace')}
                  </button>
                )}
                <button type="button" className="assign-modal-cancel" disabled={busy} onClick={close}>
                  {t('common.cancel')}
                </button>
              </div>

              {currentPersonId && (
                <p className="assign-modal-help muted">
                  {t('face.removeHelp')}
                </p>
              )}
            </div>
          </div>,
          document.body,
        )}
    </div>
  );
}
