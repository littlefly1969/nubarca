import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  addMediaListItems,
  createMediaList,
  listMediaLists,
  type MediaListSummary,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Modal } from '../components/Overlay';

// "Add the selected media to a list" — the list counterpart of the album picker.
//
// A list holds references: adding never copies a file, the same photo may sit in
// any number of lists, and adding what is already there is a no-op the summary
// reports as "already there". Creating a list here adds the picks to it in the
// same step, which is also how the current selection is saved under a name.
export interface ListPickerModalProps {
  fileItemIds: string[];
  // A list the picks already come from (a list page): not offered as a target.
  excludeListId?: string;
  onClose(): void;
  onAdded(summary: string): void;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; lists: MediaListSummary[] }
  | { kind: 'error' };

export function ListPickerModal({ fileItemIds, excludeListId, onClose, onAdded }: ListPickerModalProps) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const lists = await listMediaLists(signal);
      setState({ kind: 'ready', lists: lists.filter((l) => l.id !== excludeListId) });
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setState({ kind: 'error' });
    }
  }, [excludeListId, invalidateAuth]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  const lists = state.kind === 'ready' ? state.lists : [];
  const selected = lists.find((l) => l.id === selectedId) ?? null;
  const name = newName.trim();

  const describe = (added: number, requested: number, listName: string) => {
    const parts = [t('mediaLists.added', { added, list: listName })];
    if (requested - added > 0) parts.push(tn(requested - added, 'mediaLists.alreadyThere'));
    return parts.join(' ');
  };

  async function run(action: () => Promise<string>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onAdded(await action());
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      if (err instanceof ApiError && err.status === 404) {
        setError(t('mediaLists.gone'));
        setSelectedId(null);
        void load();
        return;
      }
      setError(t('mediaLists.addError'));
    } finally {
      setBusy(false);
    }
  }

  const addToSelected = () => selected && run(async () => {
    const r = await addMediaListItems(selected.id, fileItemIds);
    return describe(r.succeeded, r.requested, selected.name ?? '');
  });

  const createWithPicks = () => name.length > 0 && run(async () => {
    const created = await createMediaList(name, fileItemIds);
    return describe(created.count, fileItemIds.length, created.name ?? name);
  });

  return (
    <Modal
      title={tn(fileItemIds.length, 'mediaLists.pickerTitle')}
      onClose={onClose}
      dismissable={!busy}
      testId="list-picker"
      ownsKeyboard
      layer="workspace"
      footer={(
        <>
          <button type="button" className="row-action" onClick={onClose} disabled={busy} data-testid="list-picker-cancel">
            {t('common.close')}
          </button>
          <button
            type="button"
            className="row-action-primary"
            data-testid="list-picker-add"
            disabled={busy || selected === null}
            onClick={() => void addToSelected()}
          >
            {t('mediaLists.add')}
          </button>
        </>
      )}
    >
      {state.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
      {state.kind === 'error' && <p className="inline-error" role="alert">{t('mediaLists.loadError')}</p>}
      {state.kind === 'ready' && (
        <div className="album-picker">
          {lists.length === 0 && <p className="muted" data-testid="list-picker-empty">{t('mediaLists.none')}</p>}
          {lists.length > 0 && (
            <ul className="album-picker-list">
              {lists.map((l) => (
                <li key={l.id}>
                  <button
                    type="button"
                    className={`album-dest${l.id === selectedId ? ' is-selected' : ''}`}
                    data-testid="list-picker-destination"
                    data-list-id={l.id}
                    aria-pressed={l.id === selectedId}
                    disabled={busy}
                    onClick={() => setSelectedId(l.id)}
                  >
                    <span className="album-dest-text">
                      <span className="album-dest-name">{l.name}</span>
                      <span className="album-dest-meta muted">{tn(l.count, 'mediaLists.count')}</span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          <div className="album-picker-create">
            <input
              type="text"
              placeholder={t('mediaLists.newName')}
              aria-label={t('mediaLists.newName')}
              value={newName}
              maxLength={120}
              disabled={busy}
              onChange={(e) => setNewName(e.target.value)}
              data-testid="list-picker-new-name"
            />
            <button
              type="button"
              className="row-action"
              disabled={busy || name.length === 0}
              onClick={() => void createWithPicks()}
              data-testid="list-picker-create"
            >
              {t('mediaLists.createWith')}
            </button>
          </div>
        </div>
      )}
      {error && <p className="inline-error" role="alert" data-testid="list-picker-error">{error}</p>}
    </Modal>
  );
}
