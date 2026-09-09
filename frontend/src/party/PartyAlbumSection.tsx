import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import {
  ApiError,
  createAlbum,
  listAlbums,
  setPartyMainMediaSource,
  type AlbumSummary,
  type Party,
} from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { mainMediaSource } from './partyModel';

// Where the party gets its photographs.
//
// A party with no album is an ordinary, expected state — the event exists
// before the photographs — so this renders an invitation rather than an error,
// and nothing album-scoped is called until there is a real album id to call it
// with. It is a CONFIGURATION that is not finished yet, never a permission
// problem, and it is deliberately not drawn as a row of greyed-out tiles.
//
// Album creation is the album product's, unchanged: `createAlbum` is the same
// function /albums uses. This component governs the LINK and nothing else.

type Mode = 'idle' | 'pick' | 'create';

export function PartyAlbumSection({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const main = mainMediaSource(party);
  const [mode, setMode] = useState<Mode>('idle');
  const [albums, setAlbums] = useState<AlbumSummary[] | null>(null);
  const [chosen, setChosen] = useState('');
  const [newName, setNewName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // The owner's albums are fetched only when they ask to choose one: a party
  // that already has its album never pays for a list nobody opened.
  useEffect(() => {
    if (mode !== 'pick' || albums !== null) return;
    const ctrl = new AbortController();
    listAlbums(ctrl.signal)
      .then(setAlbums)
      .catch(() => { if (!ctrl.signal.aborted) setAlbums([]); });
    return () => ctrl.abort();
  }, [mode, albums]);

  async function link(albumId: string) {
    setBusy(true); setError(null);
    try {
      onPartyUpdated(await setPartyMainMediaSource(party.id, { albumId, version: party.version }));
      setMode('idle');
      setNewName(''); setChosen('');
    } catch (err) {
      // The two 409s mean different things to the host and say so: one is "this
      // party has already handed out a QR", the other "that album belongs to a
      // different evening".
      if (err instanceof ApiError && err.status === 409) {
        const code = (err.body as { error?: string } | undefined)?.error;
        setError(code === 'album_already_in_use' ? t('party.album.inUse') : t('party.album.locked'));
      } else {
        setError(t('party.album.failed'));
      }
    } finally { setBusy(false); }
  }

  // Two steps, and the album product owns the first one: `createAlbum` is the
  // same function /albums calls, so a party never grows its own album creator.
  async function createAndLink() {
    const name = newName.trim();
    if (!name) return;
    setBusy(true); setError(null);
    let album;
    try {
      album = await createAlbum(name);
    } catch {
      setError(t('party.album.failed'));
      setBusy(false);
      return;
    }
    await link(album.id);
  }

  if (main) {
    return (
      <section className="party-card" data-testid="party-album">
        <h3>{t('party.album.heading')}</h3>
        <p className="party-album-name" data-testid="party-album-name">{main.albumName}</p>
        <p className="party-card-actions">
          <Link to={`/albums/${main.albumId}`}>{t('party.album.open')}</Link>
        </p>
        {!party.canChangeMainMediaSource && (
          <p className="muted" data-testid="party-album-locked">{t('party.album.locked')}</p>
        )}
        {party.canChangeMainMediaSource && mode === 'idle' && (
          <button type="button" className="row-action" onClick={() => setMode('pick')}>
            {t('party.album.change')}
          </button>
        )}
        {party.canChangeMainMediaSource && mode !== 'idle' && (
          <AlbumPicker
            albums={albums} chosen={chosen} setChosen={setChosen} busy={busy}
            onLink={() => void link(chosen)} onCancel={() => setMode('idle')}
            excludeAlbumId={main.albumId}
          />
        )}
        {error && <p className="inline-error" role="alert">{error}</p>}
      </section>
    );
  }

  return (
    <section className="party-card party-card--empty" data-testid="party-album-empty">
      <h3>{t('party.album.heading')}</h3>
      <p className="muted">{t('party.album.help')}</p>

      {mode === 'idle' && (
        <div className="party-card-actions">
          <button type="button" className="row-action-primary" onClick={() => setMode('pick')}>
            {t('party.album.useExisting')}
          </button>
          <button type="button" className="row-action" onClick={() => setMode('create')}>
            {t('party.album.createNew')}
          </button>
        </div>
      )}

      {mode === 'pick' && (
        <AlbumPicker
          albums={albums} chosen={chosen} setChosen={setChosen} busy={busy}
          onLink={() => void link(chosen)} onCancel={() => setMode('idle')}
        />
      )}

      {mode === 'create' && (
        <div className="party-inline-form">
          <label>
            <span className="visually-hidden">{t('party.album.newNamePlaceholder')}</span>
            <input
              value={newName} disabled={busy}
              placeholder={t('party.album.newNamePlaceholder')}
              aria-label={t('party.album.newNamePlaceholder')}
              onChange={(e) => setNewName(e.target.value)}
            />
          </label>
          <button
            type="button" className="row-action-primary"
            disabled={busy || newName.trim() === ''} onClick={() => void createAndLink()}
          >
            {t('party.album.createNew')}
          </button>
          <button type="button" className="row-action" onClick={() => setMode('idle')}>
            {t('party.album.cancel')}
          </button>
        </div>
      )}

      {error && <p className="inline-error" role="alert">{error}</p>}
    </section>
  );
}

function AlbumPicker({
  albums, chosen, setChosen, busy, onLink, onCancel, excludeAlbumId,
}: {
  albums: AlbumSummary[] | null;
  chosen: string;
  setChosen(next: string): void;
  busy: boolean;
  onLink(): void;
  onCancel(): void;
  excludeAlbumId?: string;
}) {
  const { t } = useI18n();
  const options = (albums ?? []).filter((a) => a.id !== excludeAlbumId);

  if (albums !== null && options.length === 0) {
    return (
      <div className="party-inline-form">
        <p className="muted">{t('party.album.none')}</p>
        <button type="button" className="row-action" onClick={onCancel}>
          {t('party.album.cancel')}
        </button>
      </div>
    );
  }

  return (
    <div className="party-inline-form" data-testid="party-album-picker">
      <label>
        <span className="visually-hidden">{t('party.album.pick')}</span>
        <select
          value={chosen} disabled={busy || albums === null}
          aria-label={t('party.album.pick')}
          onChange={(e) => setChosen(e.target.value)}
        >
          <option value="">{t('party.album.pick')}</option>
          {options.map((album) => (
            <option key={album.id} value={album.id}>{album.name}</option>
          ))}
        </select>
      </label>
      <button
        type="button" className="row-action-primary"
        disabled={busy || chosen === ''} onClick={onLink}
      >
        {t('party.album.link')}
      </button>
      <button type="button" className="row-action" onClick={onCancel}>
        {t('party.album.cancel')}
      </button>
    </div>
  );
}
