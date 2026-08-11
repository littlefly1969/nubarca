import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  ApiError,
  assignFace,
  ignoreFace,
  removeFaceAssignment,
  unignoreFace,
  type Person,
} from '@nubarca/api-client';
import { isModalOwnedKey } from '../keyboardOwnership';
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
//   * ignored     → offers "Ripristina" instead of "Ignora volto": the viewer
//                   opens from the Ignorati tab too, and an Ignore action on an
//                   already-ignored face is an action that does nothing.
//
// Emits onChanged(personId | null) after a successful assign/move/remove.
// Ignore and restore are reported through their OWN callbacks when the caller
// supplies them, because "this face is gone from the pool" and "this face moved
// to another person" are different events for the surface underneath: a viewer
// has to drop the face from its sequence, a grid has to drop the card. Callers
// that do not care fall back to onChanged(null), which is what they always got.
export function AssignToPersonMenu({
  faceId,
  people,
  currentPersonId = null,
  currentPersonName = null,
  allowIgnore = false,
  isIgnored = false,
  onChanged,
  onIgnored,
  onRestored,
  invalidateAuth,
}: {
  faceId: string;
  people: Person[];
  currentPersonId?: string | null;
  currentPersonName?: string | null;
  allowIgnore?: boolean;
  isIgnored?: boolean;
  onChanged: (personId: string | null) => void;
  onIgnored?: (faceId: string) => void;
  onRestored?: (faceId: string) => void;
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

  // While this modal is open it is the TOPMOST surface, so it owns the keyboard.
  // Escape closes only this modal, and the navigation keys are consumed here
  // rather than reaching the viewer's `window` shortcuts underneath — a right
  // arrow typed while correcting the search text must move the caret, not the
  // photo behind the modal.
  //
  // Capture phase on `window`, because `stopPropagation` from a listener on the
  // same target does not stop a SIBLING listener. Nothing is preventDefault-ed:
  // caret movement, selection and typing are the browser's business and stay
  // exactly as they are.
  useEffect(() => {
    if (!open) return;
    function onKey(e: KeyboardEvent) {
      if (!isModalOwnedKey(e.key)) return;
      e.stopPropagation();
      if (e.key === 'Escape') close();
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

  // Ignore / restore: `notify` is the specific callback when the caller wants
  // it, and onChanged(null) otherwise — never both, so a surface that handles
  // the specific event does not also get a generic refresh it did not ask for.
  async function runIgnoreChange(fn: () => Promise<unknown>, notify?: (faceId: string) => void) {
    setBusy(true);
    try {
      await fn();
      close();
      if (notify) notify(faceId);
      else onChanged(null);
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
                {allowIgnore && (isIgnored ? (
                  <button
                    type="button"
                    className="assign-modal-restore"
                    disabled={busy}
                    onClick={() => void runIgnoreChange(() => unignoreFace(faceId), onRestored)}
                  >
                    {t('face.restore')}
                  </button>
                ) : (
                  <button
                    type="button"
                    className="assign-modal-ignore"
                    disabled={busy}
                    onClick={() => void runIgnoreChange(() => ignoreFace(faceId), onIgnored)}
                  >
                    {t('face.ignoreFace')}
                  </button>
                ))}
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
