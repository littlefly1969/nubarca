import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ApiError,
  bulkAddAlbumItems,
  bulkContributeToSharedAlbum,
  createAlbum,
  listAlbums,
  listSharedAlbums,
  type AlbumCoverItem,
  type BulkAlbumItemsResult,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Modal } from '../components/Overlay';
import { AlbumCoverMosaic } from '../albums/AlbumCoverMosaic';

// The ONE destination picker for "add the selected media to an album".
//
// Media is always chosen the same way — in the Media Library, with its normal
// multi-selection — so the only question left is WHERE it goes. Two kinds of
// destination answer that, and they are deliberately kept visually apart rather
// than flattened into one ambiguous list:
//
//   * an album the caller OWNS         → ordinary bulk membership;
//   * a shared album the caller may     → a linked, revocable CONTRIBUTION: no
//     contribute to (Contributor/Editor)  copy, no transfer of ownership, the
//                                         media stays in the caller's library.
//
// The endpoint distinction is ours to hide; the ownership distinction is not,
// which is why a shared row states whose album it is and what role the caller
// holds. Viewer albums are not offered at all: a destination that would be
// refused is worse than one that is absent, and the server refuses it anyway.
export interface AlbumPickerModalProps {
  fileItemIds: string[];
  // Pre-selects a destination when the picker was opened with one in mind —
  // arriving in the Library from a shared album's "Add from library".
  preselectedAlbumId?: string;
  onClose(): void;
  onAdded?(result: BulkAlbumItemsResult, albumName: string): void;
}

// One row of either section. `coverItems` is the mosaic the server already
// returns for the album card — nothing here requests a thumbnail of its own.
type Destination =
  | {
      kind: 'owned';
      id: string;
      name: string;
      photoCount: number;
      videoCount: number;
      itemCount: number;
      coverItems: AlbumCoverItem[];
    }
  | {
      kind: 'shared';
      id: string;
      name: string;
      ownerDisplayName: string;
      role: 'contributor' | 'editor';
      itemCount: number;
      coverItems: AlbumCoverItem[];
    };

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; owned: Destination[]; shared: Destination[] }
  | { kind: 'error' };

export function AlbumPickerModal({
  fileItemIds, preselectedAlbumId, onClose, onAdded,
}: AlbumPickerModalProps) {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [selectedId, setSelectedId] = useState<string | null>(preselectedAlbumId ?? null);
  const [query, setQuery] = useState('');
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    // Settled, not all: the two lists are independent. A shared-album read that
    // fails must not cost the caller the ability to file media into their OWN
    // albums, which is the common case and needs nothing from the other list.
    const [ownedResult, sharedResult] = await Promise.allSettled([
      listAlbums(signal),
      listSharedAlbums(signal),
    ]);
    if (signal?.aborted) return;

    if (ownedResult.status === 'rejected') {
      const err = ownedResult.reason;
      if ((err as Error)?.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setState({ kind: 'error' });
      return;
    }

    const owned = ownedResult.value.map<Destination>((a) => ({
      kind: 'owned',
      id: a.id,
      name: a.name,
      photoCount: a.photoCount,
      videoCount: a.videoCount,
      itemCount: a.itemCount,
      coverItems: a.coverItems,
    }));

    const shared = sharedResult.status === 'fulfilled'
      ? sharedResult.value
          .filter((a) => a.role === 'contributor' || a.role === 'editor')
          .map<Destination>((a) => ({
            kind: 'shared',
            id: a.albumId,
            name: a.name,
            ownerDisplayName: a.ownerDisplayName,
            role: a.role as 'contributor' | 'editor',
            itemCount: a.itemCount,
            coverItems: a.coverItems,
          }))
      : [];

    setState({ kind: 'ready', owned, shared });
  }, [invalidateAuth]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  const all = useMemo(
    () => (state.kind === 'ready' ? [...state.owned, ...state.shared] : []),
    [state],
  );
  const selected = all.find((d) => d.id === selectedId) ?? null;

  // A preselected album that turns out not to be an eligible destination (the
  // role was lost, or the album is gone) must not stay silently "chosen" —
  // dropping it here is what keeps the Add button honest.
  useEffect(() => {
    if (state.kind !== 'ready' || selectedId === null) return;
    if (!all.some((d) => d.id === selectedId)) setSelectedId(null);
  }, [state.kind, selectedId, all]);

  const matches = useCallback(
    (d: Destination) => d.name.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()),
    [query],
  );
  const ownedShown = state.kind === 'ready' ? state.owned.filter(matches) : [];
  const sharedShown = state.kind === 'ready' ? state.shared.filter(matches) : [];

  async function add() {
    if (!selected || busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      // The only place the two endpoints differ. The caller chose an album, not
      // a protocol.
      const result = selected.kind === 'owned'
        ? await bulkAddAlbumItems(selected.id, fileItemIds)
        : await bulkContributeToSharedAlbum(selected.id, fileItemIds);
      const parts = [t('albumPicker.successAdded', { added: result.succeeded, album: selected.name })];
      if (result.skipped > 0) {
        parts.push(t('albumPicker.successSkipped', { skipped: result.skipped }));
      }
      setMessage(parts.join(' '));
      onAdded?.(result, selected.name);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // 403: the role changed under us — a demotion to Viewer mid-session.
      // 404: the album is gone, or the membership ended. Both mean the list on
      // screen no longer describes what this caller may do, so refresh it rather
      // than leaving a destination that cannot work.
      if (err instanceof ApiError && (err.status === 403 || err.status === 404)) {
        setError(err.status === 403
          ? t('albumPicker.accessChanged')
          : t('albumPicker.albumGone'));
        setSelectedId(null);
        void load();
        return;
      }
      setError(t('albumPicker.error'));
    } finally {
      setBusy(false);
    }
  }

  // A new album is always the caller's OWN — there is no such thing as creating
  // an album somebody else owns — so it joins "I tuoi album" and is selected,
  // ready for the same single Add action as any other destination.
  async function createAndSelect() {
    const name = newName.trim();
    if (name.length === 0 || busy) return;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const album = await createAlbum(name);
      setState((s) => (s.kind === 'ready'
        ? {
            ...s,
            owned: [...s.owned, {
              kind: 'owned',
              id: album.id,
              name: album.name,
              // Freshly created: no members yet, and no cover to derive from one.
              photoCount: 0,
              videoCount: 0,
              itemCount: 0,
              coverItems: [],
            }],
          }
        : s));
      setSelectedId(album.id);
      setNewName('');
      setCreating(false);
      setQuery('');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(err instanceof ApiError && err.status === 409
        ? t('albumPicker.duplicateName')
        : t('albumPicker.createError'));
    } finally {
      setBusy(false);
    }
  }

  function renderRow(d: Destination) {
    const isSelected = d.id === selectedId;
    return (
      <li key={`${d.kind}:${d.id}`}>
        <button
          type="button"
          className={`album-dest${isSelected ? ' is-selected' : ''}`}
          data-testid="album-picker-destination"
          data-album-id={d.id}
          data-kind={d.kind}
          aria-pressed={isSelected}
          disabled={busy}
          onClick={() => setSelectedId(d.id)}
        >
          <AlbumCoverMosaic items={d.coverItems} name={d.name} />
          <span className="album-dest-text">
            <span className="album-dest-name">{d.name}</span>
            <span className="album-dest-meta muted">
              {d.kind === 'owned'
                ? describeOwned(d)
                : (
                  <>
                    {t('albumPicker.sharedBy', { owner: d.ownerDisplayName })}
                    {' · '}
                    <span className="album-dest-role" data-testid="album-picker-role">
                      {t(d.role === 'editor' ? 'albumRole.editor' : 'albumRole.contributor')}
                    </span>
                  </>
                )}
            </span>
          </span>
        </button>
      </li>
    );
  }

  // "84 foto · 7 video" when the album has both kinds; a plain item count when
  // it has neither (empty, or only members outside the library).
  function describeOwned(d: Extract<Destination, { kind: 'owned' }>): string {
    const parts: string[] = [];
    if (d.photoCount > 0) parts.push(t('albums.photoCount', { count: d.photoCount }));
    if (d.videoCount > 0) parts.push(t('albums.videoCount', { count: d.videoCount }));
    return parts.length > 0 ? parts.join(' · ') : tn(d.itemCount, 'albums.itemsCount');
  }

  const nothingToShow = state.kind === 'ready'
    && ownedShown.length === 0 && sharedShown.length === 0;

  return (
    <Modal
      title={tn(fileItemIds.length, 'albumPicker.titleCount')}
      onClose={onClose}
      dismissable={!busy}
      testId="album-picker"
      // Opened from the media viewer's details drawer, which listens for Escape
      // on the same target. As the topmost surface this one owns the key.
      exclusiveEscape
      footer={(
        <>
          <button
            type="button"
            className="row-action"
            data-testid="album-picker-cancel"
            onClick={onClose}
            disabled={busy}
          >
            {t('common.close')}
          </button>
          <button
            type="button"
            className="row-action-primary"
            data-testid="album-picker-add"
            disabled={busy || selected === null}
            onClick={() => void add()}
          >
            {busy ? t('albumPicker.adding') : t('albumPicker.add')}
          </button>
        </>
      )}
    >
      {state.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
      {state.kind === 'error' && (
        <p className="inline-error" role="alert">{t('albumPicker.loadError')}</p>
      )}

      {state.kind === 'ready' && (
        <div className="album-picker">
          <input
            type="search"
            className="album-picker-search"
            placeholder={t('albumPicker.search')}
            aria-label={t('albumPicker.search')}
            value={query}
            disabled={busy}
            onChange={(e) => setQuery(e.target.value)}
            data-testid="album-picker-search"
          />

          {nothingToShow && (
            <p className="muted" data-testid="album-picker-empty">
              {query.trim().length > 0 ? t('albumPicker.noMatches') : t('albumPicker.noAlbums')}
            </p>
          )}

          {ownedShown.length > 0 && (
            <section className="album-picker-section" data-testid="album-picker-owned">
              <h3 className="album-picker-section-title">{t('albumPicker.ownedSection')}</h3>
              <ul className="album-picker-list">{ownedShown.map(renderRow)}</ul>
            </section>
          )}

          {sharedShown.length > 0 && (
            <section className="album-picker-section" data-testid="album-picker-shared">
              <h3 className="album-picker-section-title">{t('albumPicker.sharedSection')}</h3>
              {/* States the contract once, where the decision is made: a
                  contribution is a link, and the media stays where it is. */}
              <p className="muted album-picker-section-note">{t('albumPicker.sharedNote')}</p>
              <ul className="album-picker-list">{sharedShown.map(renderRow)}</ul>
            </section>
          )}

          {/* Secondary throughout: choosing an existing destination is the
              common case, and a create field competing with it was most of what
              made the old dialog hard to read. */}
          <div className="album-picker-create">
            {creating ? (
              <>
                <input
                  type="text"
                  placeholder={t('albumPicker.newAlbumName')}
                  aria-label={t('albumPicker.newAlbumName')}
                  value={newName}
                  maxLength={255}
                  disabled={busy}
                  onChange={(e) => setNewName(e.target.value)}
                  data-testid="album-picker-new-name"
                />
                <button
                  type="button"
                  className="row-action"
                  disabled={busy || newName.trim().length === 0}
                  onClick={() => void createAndSelect()}
                  data-testid="album-picker-create-confirm"
                >
                  {t('albumPicker.create')}
                </button>
                <button
                  type="button"
                  className="row-action"
                  disabled={busy}
                  onClick={() => { setCreating(false); setNewName(''); }}
                >
                  {t('common.cancel')}
                </button>
              </>
            ) : (
              <button
                type="button"
                className="row-action album-picker-create-toggle"
                disabled={busy}
                onClick={() => setCreating(true)}
                data-testid="album-picker-create"
              >
                {t('albumPicker.createNew')}
              </button>
            )}
          </div>
        </div>
      )}

      {message && (
        <p className="muted" role="status" data-testid="album-picker-message">{message}</p>
      )}
      {error && <p className="inline-error" role="alert" data-testid="album-picker-error">{error}</p>}
    </Modal>
  );
}
