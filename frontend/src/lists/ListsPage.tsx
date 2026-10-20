import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, createMediaList, listMediaLists, type MediaListSummary } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { Icon } from '../components/icons/Icon';

// The owner's named lists. A list collects REFERENCES: a photo can be in any
// number of lists, deleting a list never touches a file, and what a list shows
// is always the media its owner can still see.
type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; lists: MediaListSummary[] }
  | { kind: 'error' };

export function ListsPage() {
  const { t, tn } = useI18n();
  const { invalidateAuth } = useAuth();
  const navigate = useNavigate();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setState({ kind: 'ready', lists: await listMediaLists(signal) });
    } catch (err) {
      if ((err as Error)?.name === 'AbortError') return;
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setState({ kind: 'error' });
    }
  }, [invalidateAuth]);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  async function create(e: FormEvent) {
    e.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length === 0 || busy) return;
    setBusy(true);
    setError(null);
    try {
      const created = await createMediaList(trimmed);
      void navigate(`/lists/${created.id}`);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      setError(t('mediaLists.createError'));
      setBusy(false);
    }
  }

  return (
    <section className="ws-page-outer media-lists-page" data-testid="media-lists-page">
      <header className="ws-page-header">
        <h1>{t('mediaLists.title')}</h1>
        <p className="muted">{t('mediaLists.intro')}</p>
      </header>

      <form className="media-lists-create" onSubmit={(e) => void create(e)}>
        <input
          type="text"
          value={name}
          maxLength={120}
          placeholder={t('mediaLists.newName')}
          aria-label={t('mediaLists.newName')}
          disabled={busy}
          onChange={(e) => setName(e.target.value)}
          data-testid="media-lists-new-name"
        />
        <button type="submit" className="row-action-primary" disabled={busy || name.trim().length === 0}
          data-testid="media-lists-create">
          {t('mediaLists.create')}
        </button>
      </form>
      {error && <p className="inline-error" role="alert">{error}</p>}

      {state.kind === 'loading' && <p className="muted" role="status">{t('common.loading')}</p>}
      {state.kind === 'error' && <p className="inline-error" role="alert">{t('mediaLists.loadError')}</p>}
      {state.kind === 'ready' && state.lists.length === 0 && (
        <p className="muted" data-testid="media-lists-empty">{t('mediaLists.empty')}</p>
      )}
      {state.kind === 'ready' && state.lists.length > 0 && (
        <ul className="media-lists" data-testid="media-lists">
          {state.lists.map((l) => (
            <li key={l.id}>
              <Link to={`/lists/${l.id}`} className="media-lists-card" data-testid="media-lists-card">
                <Icon name="list" />
                <span className="media-lists-card-text">
                  <span className="media-lists-card-name">{l.name}</span>
                  <span className="muted">{tn(l.count, 'mediaLists.count')}</span>
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
