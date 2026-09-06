import { useEffect, useState } from 'react';
import {
  deletePartyChallenge, listAlbumItems, listPartyChallenges, reorderPartyChallenges,
  type AlbumItemSummary, type PartyChallenge,
} from '@nubarca/api-client';
import { Modal } from '../components/Overlay';
import { PartyChallengeCard } from '../party/PartyChallengeCard';
import { useI18n } from '../i18n';
import { PartyChallengeComposer } from './PartyChallengeComposer';
import './PartyDeck.css';

// The deck: what the host has prepared, in the order the game will play it.
//
// This surface used to be an editor AND a list in one column, which meant the
// form was always on screen whether or not anybody was writing anything. Editing
// one activity is now its own overlay (PartyChallengeComposer) and what remains
// here is the deck itself: read it, reorder it, and open one.
//
// The rows render the canonical card in `compact`, so an activity looks like
// itself everywhere — the list, the preview and the television are the same
// component at three sizes.

type Editing = { challenge: PartyChallenge | null } | null;

export function PartyChallengeManager({ albumId }: { albumId: string }) {
  const { t } = useI18n();
  const [items, setItems] = useState<PartyChallenge[]>([]);
  const [media, setMedia] = useState<AlbumItemSummary[]>([]);
  const [editing, setEditing] = useState<Editing>(null);
  const [deleting, setDeleting] = useState<PartyChallenge | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);

  const refresh = async () => {
    const [deck, members] = await Promise.all([listPartyChallenges(albumId), listAlbumItems(albumId)]);
    setItems(deck.items);
    setMedia(members.filter((x) => x.thumbnailUrl));
  };
  useEffect(() => { void refresh().catch(() => setError(true)); }, [albumId]);

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await deletePartyChallenge(albumId, deleting.id);
      setDeleting(null);
      await refresh();
    } catch { setError(true); } finally { setBusy(false); }
  };

  const move = async (at: number, delta: -1 | 1) => {
    const next = [...items];
    const to = at + delta;
    if (to < 0 || to >= next.length) return;
    [next[at], next[to]] = [next[to], next[at]];
    setItems(next);
    try { await reorderPartyChallenges(albumId, next.map((x) => x.id)); }
    catch { setError(true); await refresh(); }
  };

  return (
    <section className="party-game-manager" data-testid="party-challenge-manager">
      <div className="party-deck-head">
        <div>
          <h4>{t('partyGame.deckTitle')}</h4>
          <p className="muted">{t('partyGame.deckHelp')}</p>
        </div>
        <button
          type="button"
          className="party-composer-primary"
          data-testid="party-deck-add"
          onClick={() => setEditing({ challenge: null })}
        >
          {t('partyComposer.createTitle')}
        </button>
      </div>

      {error && <p className="inline-error" role="alert">{t('partyGame.error')}</p>}

      {items.length === 0 ? <p className="empty-state">{t('partyGame.empty')}</p> : (
        <ol className="party-deck-list">
          {items.map((item, at) => (
            <li key={item.id} className={item.isEnabled ? undefined : 'is-off'}>
              <PartyChallengeCard
                mode="compact"
                challenge={{
                  kind: item.kind,
                  title: item.title,
                  body: item.body,
                  mediaUrl: item.mediaUrl,
                  durationSeconds: item.durationSeconds,
                }}
                context={{ round: at + 1, total: items.length }}
                testId={`party-deck-item-${item.id}`}
              />
              <div className="party-deck-row-actions">
                {/* An activity that is off is stated, not merely dimmed. */}
                <span className={`status-badge status-badge--${item.isEnabled ? 'on' : 'off'}`}>
                  {item.isEnabled ? t('partyGame.on') : t('partyGame.off')}
                </span>
                <button type="button" aria-label={t('partyGame.moveUp')}
                  disabled={at === 0 || busy} onClick={() => void move(at, -1)}>↑</button>
                <button type="button" aria-label={t('partyGame.moveDown')}
                  disabled={at === items.length - 1 || busy} onClick={() => void move(at, 1)}>↓</button>
                <button type="button" disabled={busy}
                  onClick={() => setEditing({ challenge: item })}>{t('partyGame.edit')}</button>
                <button type="button" className="btn-danger" disabled={busy}
                  onClick={() => setDeleting(item)}>{t('common.delete')}</button>
              </div>
            </li>
          ))}
        </ol>
      )}

      {editing && (
        <PartyChallengeComposer
          albumId={albumId}
          media={media}
          challenge={editing.challenge}
          position={editing.challenge
            ? items.findIndex((x) => x.id === editing.challenge!.id) + 1
            : items.length + 1}
          total={editing.challenge ? items.length : items.length + 1}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); void refresh().catch(() => setError(true)); }}
        />
      )}

      {deleting && (
        // Deleting an activity used to be window.confirm, which is a browser
        // dialog with no product in it and no way to say what is being lost.
        <Modal
          title={t('partyDeck.deleteTitle')}
          onClose={() => setDeleting(null)}
          dismissable={!busy}
          ownsKeyboard
          layer="workspace"
          testId="party-deck-delete"
          footer={(
            <div className="party-composer-actions">
              <button type="button" disabled={busy} onClick={() => setDeleting(null)}>
                {t('common.cancel')}
              </button>
              <button type="button" className="btn-danger" data-testid="party-deck-delete-confirm"
                disabled={busy} onClick={() => void remove()}>
                {t('common.delete')}
              </button>
            </div>
          )}
        >
          <p>{t('partyDeck.deleteBody', { title: deleting.title })}</p>
        </Modal>
      )}
    </section>
  );
}
